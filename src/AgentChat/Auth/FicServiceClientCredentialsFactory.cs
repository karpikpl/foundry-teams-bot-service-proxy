using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Rest;

namespace AgentChat.Auth;

/// <summary>
/// Bot Framework outbound credentials factory backed by Federated Identity
/// Credentials (FIC) — no client secrets stored on disk.
///
/// FLOW (per outbound reply):
///   1. The container's User-Assigned Managed Identity (UAMI) gets a token
///      from IMDS with audience <c>api://AzureADTokenExchange</c>. This is
///      the "client assertion" — it asserts to AAD that "I am the workload
///      with this UAMI principal".
///   2. We POST that assertion to AAD's /oauth2/v2.0/token endpoint as a
///      JWT bearer client assertion, with <c>client_id</c> set to the bot's
///      app reg id and <c>scope=https://api.botframework.com/.default</c>.
///   3. AAD checks: "does the bot app reg have a Federated Identity
///      Credential whose issuer/subject/audience matches this assertion?"
///      If yes, AAD mints a Bot Framework access token signed with the bot
///      app reg's identity. We cache it per-appId until ~5 min before
///      expiry.
///
/// WHY THIS EXISTS:
/// Multiple bot app regs share one container. Storing N client secrets is
/// painful (rotation, key vault refs, accidental disclosure). FICs let
/// every bot inherit credentials from a single workload identity (the
/// container UAMI) without ever materializing a secret on disk.
///
/// CONTAINS NO SECRETS — only token caches.
/// </summary>
public sealed class FicServiceClientCredentialsFactory : ServiceClientCredentialsFactory
{
    private static readonly string[] UamiAssertionScope = ["api://AzureADTokenExchange/.default"];
    private const string BotFrameworkScope = "https://api.botframework.com/.default";

    private readonly TokenCredential _uamiCredential;
    private readonly HttpClient _http;
    private readonly ILogger<FicServiceClientCredentialsFactory> _logger;
    private readonly string _tenantId;
    private readonly HashSet<string> _validAppIds;

    // Cache BF tokens per-appId. Refresh ~5 minutes before expiry to avoid
    // edge-case clock skew at the connector. ConcurrentDictionary so two
    // simultaneous outbound replies for the same bot don't double-mint.
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FicServiceClientCredentialsFactory(
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        ILogger<FicServiceClientCredentialsFactory> logger)
    {
        _logger = logger;
        _http = httpFactory.CreateClient(nameof(FicServiceClientCredentialsFactory));
        _tenantId = cfg["MicrosoftAppTenantId"] ?? cfg["AZURE_TENANT_ID"]
            ?? throw new InvalidOperationException("MicrosoftAppTenantId not configured.");

        var managedIdentityClientId = cfg["AZURE_CLIENT_ID"];
        _uamiCredential = string.IsNullOrEmpty(managedIdentityClientId)
            ? new ManagedIdentityCredential()
            : new ManagedIdentityCredential(managedIdentityClientId);

        _validAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routesJson = cfg["Bots:Routes"];
        if (!string.IsNullOrWhiteSpace(routesJson))
        {
            try
            {
                var routes = JsonSerializer.Deserialize<List<RouteEntry>>(routesJson) ?? new();
                foreach (var r in routes)
                {
                    var aud = r.EffectiveProxyAppId;
                    if (!string.IsNullOrEmpty(aud))
                    {
                        _validAppIds.Add(aud);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bots:Routes is not valid JSON ({Json}); FIC factory has no valid app ids.", routesJson);
            }
        }

        _logger.LogInformation("FIC factory initialized with {Count} valid app ids in tenant {Tenant}.", _validAppIds.Count, _tenantId);
    }

    public override Task<bool> IsValidAppIdAsync(string appId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_validAppIds.Contains(appId));
    }

    // We never run anonymously — every bot has its own appId.
    public override Task<bool> IsAuthenticationDisabledAsync(CancellationToken cancellationToken)
        => Task.FromResult(false);

    public override Task<ServiceClientCredentials> CreateCredentialsAsync(
        string appId, string oauthScope, string loginEndpoint, bool validateAuthority, CancellationToken cancellationToken)
    {
        // oauthScope is the BF endpoint scope; we always mint for /.default.
        // loginEndpoint is the AAD authority — we use it to build the v2.0
        // token endpoint for THIS tenant (loginEndpoint already contains the
        // tenant from BF's internal config).
        if (!_validAppIds.Contains(appId))
        {
            throw new UnauthorizedAccessException($"appId {appId} is not in the configured Bots:Routes allow list.");
        }

        ServiceClientCredentials creds = new FicCredentials(appId, GetBotFrameworkTokenAsync);
        return Task.FromResult(creds);
    }

    private async Task<string> GetBotFrameworkTokenAsync(string appId, CancellationToken ct)
    {
        if (_cache.TryGetValue(appId, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cached.Token;
        }

        // Step 1: client assertion from UAMI.
        var assertion = await _uamiCredential.GetTokenAsync(
            new TokenRequestContext(UamiAssertionScope), ct).ConfigureAwait(false);

        // Step 2: token exchange against AAD for a BF token.
        var tokenEndpoint = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";
        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = appId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion.Token,
                ["scope"] = BotFrameworkScope,
                ["grant_type"] = "client_credentials",
            })
        };

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("FIC token exchange failed for appId {AppId}: {Status} {Body}", appId, resp.StatusCode, body);
            throw new InvalidOperationException($"FIC token exchange failed: {resp.StatusCode} {body}");
        }

        var doc = JsonSerializer.Deserialize<TokenResponse>(body)
                  ?? throw new InvalidOperationException("Empty token response.");
        var expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, doc.expires_in - 60));
        _cache[appId] = new CachedToken(doc.access_token, expires);
        _logger.LogDebug("Minted BF token for appId {AppId}, expires {Expires}", appId, expires);
        return doc.access_token;
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresOn);

    private sealed class RouteEntry
    {
        public string? AgentName { get; set; }
        public string? ProxyAppId { get; set; }
        public string? DirectAppId { get; set; }
        public string? AppId { get; set; }

        public string? EffectiveProxyAppId =>
            !string.IsNullOrEmpty(ProxyAppId) ? ProxyAppId : AppId;
    }

    private sealed class TokenResponse
    {
        public string access_token { get; set; } = string.Empty;
        public int expires_in { get; set; }
    }

    /// <summary>
    /// ServiceClientCredentials wrapper that injects a fresh BF access token
    /// into every outbound request. The token getter is invoked per-request
    /// (with caching inside the factory) so we never serve a stale token to
    /// the connector.
    /// </summary>
    private sealed class FicCredentials : ServiceClientCredentials
    {
        private readonly string _appId;
        private readonly Func<string, CancellationToken, Task<string>> _getToken;

        public FicCredentials(string appId, Func<string, CancellationToken, Task<string>> getToken)
        {
            _appId = appId;
            _getToken = getToken;
        }

        public override async Task ProcessHttpRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _getToken(_appId, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await base.ProcessHttpRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

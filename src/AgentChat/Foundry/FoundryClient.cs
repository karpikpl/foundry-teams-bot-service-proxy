using System.ClientModel.Primitives;
using Azure.Core;
using OpenAI;

namespace AgentChat.Foundry;

/// <summary>
/// Per-request user-token scope. When a value is set on this AsyncLocal, the
/// <see cref="FoundryClient"/>'s auth policy uses it as the Bearer token for
/// the outgoing Foundry call instead of acquiring an app/UMI token. The
/// scope flows through async/await chains so callers just wrap the call:
///
/// <code>
///   using (FoundryUserAuthScope.Use(userToken))
///   {
///       await foreach (var ev in client.OpenAI.GetResponsesClient().CreateResponseStreamingAsync(opts)) { ... }
///   }
/// </code>
///
/// Outside any scope, the policy falls back to the configured
/// <see cref="TokenCredential"/> (App Service UMI). That fallback is what
/// admin/catalog calls (e.g. /admin/agents) continue to use.
/// </summary>
public static class FoundryUserAuthScope
{
    private static readonly AsyncLocal<string?> _userToken = new();

    /// <summary>Current per-request user token, or null if not set.</summary>
    public static string? Current => _userToken.Value;

    /// <summary>
    /// Set the per-request user token for the duration of the returned scope.
    /// The previous value is restored on dispose so scopes can nest cleanly.
    /// </summary>
    public static IDisposable Use(string userToken)
    {
        if (string.IsNullOrEmpty(userToken))
            throw new ArgumentException("userToken is required", nameof(userToken));

        var previous = _userToken.Value;
        _userToken.Value = userToken;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;
        public Restore(string? previous) { _previous = previous; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _userToken.Value = _previous;
        }
    }
}

/// <summary>
/// Thin per-agent wrapper around the OpenAI .NET SDK, targeting a Foundry
/// per-agent endpoint URL of the form:
///   <c>https://{host}/api/projects/{project}/agents/{agent}/endpoint/protocols/openai/v1</c>
///
/// All we add over the bare SDK is:
///   1. <see cref="EntraIdAuthenticationPolicy"/> — fetches bearer tokens from
///      a <see cref="TokenCredential"/> (UMI / DefaultAzureCredential) with
///      scope <c>https://ai.azure.com/.default</c>. If <see cref="FoundryUserAuthScope.Current"/>
///      is set (e.g. inside a per-turn user-token scope) that token wins.
///   2. <see cref="ApiVersionPolicy"/> — appends <c>?api-version=...</c> to
///      every request, since Foundry requires it.
///
/// One instance per per-agent endpoint URL. Cached in
/// <see cref="Services.AgentClientCache"/>.
/// </summary>
public sealed class FoundryClient
{
    private const string DefaultApiVersion = "2025-05-15-preview";
    private const string TokenScope        = "https://ai.azure.com/.default";

    public string Endpoint { get; }
    public OpenAIClient OpenAI { get; }

    public FoundryClient(string endpoint, TokenCredential credential, string apiVersion = DefaultApiVersion, PipelineTransport? transport = null)
    {
        if (!endpoint.EndsWith("/")) endpoint += "/";
        Endpoint = endpoint;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint)
        };
        if (transport is not null)
        {
            options.Transport = transport;
        }
        options.AddPolicy(new ApiVersionPolicy(apiVersion), PipelinePosition.PerCall);

        OpenAI = new OpenAIClient(new EntraIdAuthenticationPolicy(credential, TokenScope), options);
    }

    /// <summary>Adds <c>Authorization: Bearer &lt;token&gt;</c> using either the
    /// per-request user token from <see cref="FoundryUserAuthScope"/> or, as a
    /// fallback, a cached AAD app token via the configured <see cref="TokenCredential"/>.</summary>
    private sealed class EntraIdAuthenticationPolicy : AuthenticationPolicy
    {
        private readonly TokenCredential _credential;
        private readonly string[] _scopes;
        private AccessToken _cached;
        private readonly object _lock = new();

        public EntraIdAuthenticationPolicy(TokenCredential credential, string scope)
        {
            _credential = credential;
            _scopes     = new[] { scope };
        }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            var userToken = FoundryUserAuthScope.Current;
            var bearer    = userToken ?? GetAppTokenSync();
            message.Request.Headers.Set("Authorization", "Bearer " + bearer);
            if (currentIndex < pipeline.Count - 1)
                pipeline[currentIndex + 1].Process(message, pipeline, currentIndex + 1);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            var userToken = FoundryUserAuthScope.Current;
            var bearer    = userToken ?? await GetAppTokenAsync().ConfigureAwait(false);
            message.Request.Headers.Set("Authorization", "Bearer " + bearer);
            if (currentIndex < pipeline.Count - 1)
                await pipeline[currentIndex + 1].ProcessAsync(message, pipeline, currentIndex + 1).ConfigureAwait(false);
        }

        private string GetAppTokenSync()
        {
            lock (_lock)
            {
                if (_cached.Token is not null && _cached.ExpiresOn > DateTimeOffset.UtcNow.AddSeconds(60))
                    return _cached.Token;
            }
            var fetched = _credential.GetToken(new TokenRequestContext(_scopes), CancellationToken.None);
            lock (_lock) { _cached = fetched; }
            return fetched.Token;
        }

        private async ValueTask<string> GetAppTokenAsync()
        {
            lock (_lock)
            {
                if (_cached.Token is not null && _cached.ExpiresOn > DateTimeOffset.UtcNow.AddSeconds(60))
                    return _cached.Token;
            }
            var fetched = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), CancellationToken.None).ConfigureAwait(false);
            lock (_lock) { _cached = fetched; }
            return fetched.Token;
        }
    }

    /// <summary>Appends <c>?api-version=...</c> to every outgoing request.</summary>
    private sealed class ApiVersionPolicy : PipelinePolicy
    {
        private readonly string _apiVersion;
        public ApiVersionPolicy(string apiVersion) { _apiVersion = apiVersion; }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            InjectApiVersion(message);
            if (currentIndex < pipeline.Count - 1)
                pipeline[currentIndex + 1].Process(message, pipeline, currentIndex + 1);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            InjectApiVersion(message);
            if (currentIndex < pipeline.Count - 1)
                await pipeline[currentIndex + 1].ProcessAsync(message, pipeline, currentIndex + 1).ConfigureAwait(false);
        }

        private void InjectApiVersion(PipelineMessage message)
        {
            var uri = message.Request.Uri;
            if (uri is null) return;
            if (uri.Query.Contains("api-version=", StringComparison.OrdinalIgnoreCase)) return;
            var sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            message.Request.Uri = new Uri(uri.ToString() + sep + "api-version=" + _apiVersion);
        }
    }
}

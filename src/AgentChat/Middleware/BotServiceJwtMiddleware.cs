using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

namespace AgentChat.Middleware;

/// <summary>
/// Multi-bot, route-bound JWT validator for inbound Bot Framework traffic.
///
/// The proxy fronts N agents; each has its own SingleTenant AAD app reg
/// (the bot's <c>msaAppId</c>). Bot Service issues per-bot tokens whose
/// <c>aud</c> equals the bot's appId. We bind each incoming URL path to
/// exactly one expected appId via the <c>Bots:Routes</c> config (injected
/// from the bicep <c>Bots__Routes</c> env var as a JSON array of
/// <c>{ AgentName, AppId }</c> objects).
///
/// THREAT MODEL ADDRESSED:
/// A token issued for Bot A must NOT be accepted on Bot B's URL — even if
/// both bots are ours. A pure "any-of-our-appIds" allowlist would fail this
/// check. By extracting the agent from the URL path
/// <c>/api/messages/{foundry}/{project}/{agent}</c> and looking up the
/// expected <c>aud</c> for that route, we make cross-bot token confusion
/// impossible without compromising a specific bot's identity.
///
/// ISSUER:
/// SingleTenant bots emit tokens whose issuer is the customer's AAD tenant,
/// in either v1 (<c>https://sts.windows.net/&lt;tenantId&gt;/</c>) or v2
/// (<c>https://login.microsoftonline.com/&lt;tenantId&gt;/v2.0</c>) form
/// depending on the token version. We accept BOTH and require the tenant
/// id segment to match <c>MicrosoftAppTenantId</c> (or <c>AZURE_TENANT_ID</c>).
///
/// SIGNATURE:
/// We do NOT verify the JWT signature here — that's the CloudAdapter's job
/// (it performs crypto + JWKS validation against the Bot Framework signing
/// key set). This middleware gates by issuer + aud claims as a fast,
/// defense-in-depth check.
///
/// Set <c>JwtValidation:Enabled=false</c> to disable in dev / local-emulator.
/// </summary>
public class BotServiceJwtMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<BotServiceJwtMiddleware> _logger;
    private readonly Dictionary<string, string> _routeToAud; // agent name → expected appId
    private readonly string _tenantId;
    private readonly bool _enabled;

    public BotServiceJwtMiddleware(RequestDelegate next, IConfiguration cfg, ILogger<BotServiceJwtMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _enabled = cfg.GetValue("JwtValidation:Enabled", true);
        _tenantId = cfg["MicrosoftAppTenantId"] ?? cfg["AZURE_TENANT_ID"] ?? string.Empty;

        // Routes is a JSON string emitted by bicep as `Bots__Routes`:
        //   [{"AgentName":"agent1","AppId":"<guid>"},...]
        // We materialize it once at startup; misshapen JSON disables the
        // middleware (with a loud warning) so requests don't 401-loop.
        _routeToAud = new(StringComparer.OrdinalIgnoreCase);
        var routesJson = cfg["Bots:Routes"];
        if (!string.IsNullOrWhiteSpace(routesJson))
        {
            try
            {
                var routes = JsonSerializer.Deserialize<List<RouteEntry>>(routesJson)
                             ?? new List<RouteEntry>();
                foreach (var r in routes)
                {
                    var aud = r.EffectiveProxyAppId;
                    if (!string.IsNullOrEmpty(r.AgentName) && !string.IsNullOrEmpty(aud))
                    {
                        _routeToAud[r.AgentName] = aud;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bots:Routes is not valid JSON ({Json}); JWT middleware disabled.", routesJson);
                _enabled = false;
            }
        }

        if (_enabled && _routeToAud.Count == 0)
        {
            _logger.LogWarning("Bots:Routes is empty — JWT middleware has no expected audiences and will reject every request. Disabling.");
            _enabled = false;
        }

        if (_enabled && string.IsNullOrEmpty(_tenantId))
        {
            _logger.LogWarning("MicrosoftAppTenantId is not configured — issuer check will fail. Disabling middleware.");
            _enabled = false;
        }
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!_enabled
            || !ctx.Request.Path.StartsWithSegments("/api/messages", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        // URL shape: /api/messages/{foundry}/{project}/{agent}
        // Anything else hitting /api/messages is unexpected — 404 it
        // rather than letting a malformed token reach the adapter.
        var segments = ctx.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // segments[0]=api, [1]=messages, [2]=foundry, [3]=project, [4]=agent
        if (segments.Length < 5)
        {
            _logger.LogWarning("Rejected request to {Path}: URL does not contain agent segment.", ctx.Request.Path);
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var agent = segments[4];

        if (!_routeToAud.TryGetValue(agent, out var expectedAud))
        {
            _logger.LogWarning("Rejected request to {Path}: agent '{Agent}' is not in Bots:Routes.", ctx.Request.Path, agent);
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Rejected request to {Path}: missing or non-bearer Authorization header.", ctx.Request.Path);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var token = authHeader.Substring("Bearer ".Length);
        JwtSecurityToken jwt;
        try
        {
            jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rejected request to {Path}: malformed JWT.", ctx.Request.Path);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Issuer must be the customer's tenant or Bot Framework itself.
        // ABS→bot tokens for SingleTenant bots can come signed by:
        //   - https://sts.windows.net/{tid}/             (v1 AAD)
        //   - https://login.microsoftonline.com/{tid}/v2.0  (v2 AAD)
        //   - https://api.botframework.com               (Bot Framework signing key)
        // The real security boundary is the audience check below
        // (aud == route's expected appId).
        var iss = jwt.Claims.FirstOrDefault(c => c.Type == "iss")?.Value;
        var v1 = $"https://sts.windows.net/{_tenantId}/";
        var v2 = $"https://login.microsoftonline.com/{_tenantId}/v2.0";
        const string bf = "https://api.botframework.com";
        if (!string.Equals(iss, v1, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(iss, v2, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(iss, bf, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Rejected JWT with unexpected issuer {Issuer}; expected {V1}, {V2} or {BF}", iss, v1, v2, bf);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Audience MUST equal the route's expected appId — not just any of
        // our bots' appIds. This is what prevents cross-bot token confusion.
        var aud = jwt.Claims.FirstOrDefault(c => c.Type == "aud")?.Value;
        if (!string.Equals(aud, expectedAud, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Rejected JWT for agent {Agent}: aud={Aud}, expected={Expected}", agent, aud, expectedAud);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Stash the resolved bot appId for downstream code (FoundryBot, FIC
        // factory) so they don't have to re-parse the URL or token.
        ctx.Items["BotAppId"] = expectedAud;
        ctx.Items["AgentName"] = agent;

        await _next(ctx);
    }

    private sealed class RouteEntry
    {
        public string? AgentName { get; set; }
        // New shape: separate proxy + direct ids. AppId is kept as a fallback
        // for backwards-compat with older bicep emissions.
        public string? ProxyAppId { get; set; }
        public string? DirectAppId { get; set; }
        public string? AppId { get; set; }

        public string? EffectiveProxyAppId =>
            !string.IsNullOrEmpty(ProxyAppId) ? ProxyAppId : AppId;
    }
}

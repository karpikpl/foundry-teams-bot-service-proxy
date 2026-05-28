using System.IdentityModel.Tokens.Jwt;

namespace AgentChat.Middleware;

/// <summary>
/// Defense-in-depth validator for inbound Bot Framework JWTs.
///
/// The Bot Framework CloudAdapter already validates the JWT signature and
/// issuer (`api.botframework.com`). This middleware adds an explicit check
/// that the `aud` claim matches our configured BOTSERVICE_UAMI_CLIENTID env
/// var — i.e. the UMI we expect every Bot Service registration to share.
///
/// This guards against:
///   - A misconfigured CloudAdapter (e.g. MicrosoftAppId env var missing)
///   - An attacker registering their own Bot Service pointing at our endpoint
///     (their `aud` would be their own bot id, not ours)
///
/// Set `JwtValidation:Enabled=false` to disable in dev / local-emulator.
/// </summary>
public class BotServiceJwtMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<BotServiceJwtMiddleware> _logger;
    private readonly string? _expectedAud;
    private readonly bool _enabled;

    public BotServiceJwtMiddleware(RequestDelegate next, IConfiguration cfg, ILogger<BotServiceJwtMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _expectedAud = cfg["BOTSERVICE_UAMI_CLIENTID"] ?? cfg["MicrosoftAppId"];
        _enabled = cfg.GetValue("JwtValidation:Enabled", true);

        if (_enabled && string.IsNullOrEmpty(_expectedAud))
        {
            _logger.LogWarning("BOTSERVICE_UAMI_CLIENTID is not configured — bot JWT audience check is disabled.");
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

        // Issuer must be Bot Framework. CloudAdapter checks this too but we
        // refuse early to avoid even invoking the adapter for non-bot tokens.
        var iss = jwt.Claims.FirstOrDefault(c => c.Type == "iss")?.Value;
        if (iss != "https://api.botframework.com")
        {
            _logger.LogWarning("Rejected JWT with unexpected issuer: {Issuer}", iss);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Audience must match our expected UMI client id.
        var aud = jwt.Claims.FirstOrDefault(c => c.Type == "aud")?.Value;
        if (!string.Equals(aud, _expectedAud, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Rejected JWT with audience {Aud}; expected {Expected}", aud, _expectedAud);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // NOTE: We do NOT verify the JWT signature here — that's the
        // CloudAdapter's job (it does crypto + JWKS validation against the
        // Bot Framework signing key set). We just gate by issuer + aud.

        await _next(ctx);
    }
}


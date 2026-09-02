using System.Diagnostics;
using System.Net;

namespace AgentChat.Middleware;

/// <summary>
/// Log-only classifier for inbound traffic on the Bot Service ingress paths
/// (<c>/api/messages*</c> and <c>/api/passthrough*</c>). Never rejects a
/// request — the real trust boundary is the JWT audience check performed by
/// <see cref="BotServiceJwtMiddleware"/>. This middleware exists purely so
/// operators can answer "which upstream is actually calling us?" from App
/// Insights.
///
/// Every gated request is:
///   1. Resolved to a client IP (X-Forwarded-For first hop, then
///      <c>Connection.RemoteIpAddress</c>, with IPv4-in-IPv6 unmapped).
///   2. Classified against a hardcoded CIDR→label table below.
///   3. Emitted as a structured Information log line, AND stamped onto
///      <see cref="Activity.Current"/> as tags <c>SourceLabel</c> and
///      <c>ClientIp</c> so they surface as <c>customDimensions</c> on the
///      Application Insights <c>requests</c> telemetry.
///
/// The label table is intentionally hardcoded (no config surface): the
/// deployment target is Azure Bot Service EastUS + Microsoft Teams, and
/// changing regions is a code change reviewed via PR. If we ever go
/// multi-region we promote this table to config in a follow-up.
///
/// Ranges (as of 2026-07):
///   Teams                    52.112.0.0/14, 52.122.0.0/15
///   AzureBotService-EastUS   20.42.0.64/30, 40.71.12.244/30
///   (anything else)          Unknown
///
/// Sources:
///   - Microsoft 365 URLs and IP address ranges (Teams).
///   - AzureBotService service tag (see
///     https://azservicetags.azurewebsites.net/servicetag/azurebotservice*).
/// </summary>
public class InboundSourceLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<InboundSourceLoggingMiddleware> _logger;

    // Path prefixes we classify. Everything else (/health, /admin/*, static
    // files) is passed through untouched with no log line.
    private static readonly string[] GatedPathPrefixes =
    {
        "/api/messages",
        "/api/passthrough",
    };

    private static readonly (IPNetwork Network, string Label)[] LabelTable = BuildLabelTable();

    public InboundSourceLoggingMiddleware(RequestDelegate next, ILogger<InboundSourceLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!PathIsGated(ctx.Request.Path))
        {
            await _next(ctx);
            return;
        }

        var clientIp = ResolveClientIp(ctx);
        var label = Classify(clientIp);
        var ipText = clientIp?.ToString() ?? "unknown";

        // Stamp Activity so App Insights request telemetry gets customDimensions.
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("SourceLabel", label);
            activity.SetTag("ClientIp", ipText);
        }

        _logger.LogInformation(
            "Inbound {Method} {Path} from {ClientIp} classified as {SourceLabel}",
            ctx.Request.Method,
            ctx.Request.Path.Value,
            ipText,
            label);

        await _next(ctx);
    }

    private static bool PathIsGated(PathString path)
    {
        foreach (var prefix in GatedPathPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static string Classify(IPAddress? ip)
    {
        if (ip is null)
        {
            return "Unknown";
        }
        foreach (var (network, label) in LabelTable)
        {
            if (network.Contains(ip))
            {
                return label;
            }
        }
        return "Unknown";
    }

    internal static IPAddress? ResolveClientIp(HttpContext ctx)
    {
        // ACA's envoy appends the real client IP to X-Forwarded-For. The
        // left-most entry is the originating client; subsequent entries are
        // intermediaries. We do not accept arbitrary upstream input as a
        // security boundary here — this is telemetry, not enforcement.
        var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(xff))
        {
            var first = xff.Split(',', 2)[0].Trim();
            // Strip optional [ipv6]:port or ipv4:port suffix.
            if (first.StartsWith('['))
            {
                var close = first.IndexOf(']');
                if (close > 0) first = first.Substring(1, close - 1);
            }
            else if (first.Count(c => c == ':') == 1)
            {
                first = first.Split(':', 2)[0];
            }
            if (IPAddress.TryParse(first, out var parsed))
            {
                return Unmap(parsed);
            }
        }

        var remote = ctx.Connection.RemoteIpAddress;
        return remote is null ? null : Unmap(remote);
    }

    private static IPAddress Unmap(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static (IPNetwork, string)[] BuildLabelTable() => new (IPNetwork, string)[]
    {
        (IPNetwork.Parse("52.112.0.0/14"),   "Teams"),
        (IPNetwork.Parse("52.122.0.0/15"),   "Teams"),
        (IPNetwork.Parse("20.42.0.64/30"),   "AzureBotService-EastUS"),
        (IPNetwork.Parse("40.71.12.244/30"), "AzureBotService-EastUS"),
    };
}

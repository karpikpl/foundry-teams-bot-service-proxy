using System.IO.Compression;
using System.Net.Http;
using System.Text;
using AgentChat.Bots;
using AgentChat.Foundry;
using AgentChat.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentChat.Controllers;

/// <summary>
/// In-app Teams manifest generator.
///
///   <c>GET /admin/manifest</c>                              — HTML index, agents in the configured project
///   <c>GET /admin/manifest/{agent}</c>                      — download .zip for an agent in the configured project
///   <c>GET /admin/agents</c>                                — JSON list of agents in the configured project
///   <c>GET /admin/{foundryHost}/{project}/manifest</c>      — HTML index, agents in an arbitrary project
///   <c>GET /admin/{foundryHost}/{project}/manifest/{agent}</c> — download .zip for an arbitrary agent
///
/// The arbitrary-project routes let an admin generate a manifest for any
/// Foundry project the App Service UAMI has access to, without changing the
/// app config. The generated zip's manifest embeds the URL-routed messaging
/// path (<c>/api/messages/{foundryHost}/{project}/{agent}</c>) so the Bot
/// Service registration created from this manifest can be configured to use
/// that path and land on the right per-agent route in the proxy.
/// </summary>
[ApiController]
[Route("admin")]
public class ManifestController : ControllerBase
{
    private readonly AgentService _agents;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ManifestController> _logger;

    public ManifestController(
        AgentService agents,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<ManifestController> logger)
    {
        _agents       = agents;
        _httpFactory  = httpFactory;
        _config       = config;
        _env          = env;
        _logger       = logger;
    }

    // ====================================================== JSON: configured project

    [HttpGet("agents")]
    public async Task<IActionResult> ListAgents(CancellationToken ct)
    {
        var catalog = await _agents.GetDescriptorsAsync(forceRefresh: false, ct: ct);
        return Ok(catalog.Select(d => new
        {
            key         = d.Key,
            name        = d.Name,
            description = d.Description,
            endpoint    = d.Endpoint
        }));
    }

    // ====================================================== HTML: configured project

    [HttpGet("manifest")]
    [Produces("text/html")]
    public async Task<ContentResult> Index(CancellationToken ct)
    {
        var catalog = await _agents.GetDescriptorsAsync(forceRefresh: false, ct: ct);
        var html = RenderIndex(
            agents:           catalog,
            downloadRoot:     "/admin/manifest",
            sourceLabel:      _agents.DefaultProjectEndpoint,
            isCustomRoute:    false);
        return new ContentResult { Content = html, ContentType = "text/html", StatusCode = 200 };
    }

    [HttpGet("manifest/{agentName}")]
    public async Task<IActionResult> Download(string agentName, CancellationToken ct)
    {
        var catalog = await _agents.GetDescriptorsAsync(forceRefresh: false, ct: ct);
        var agent = catalog.FirstOrDefault(d =>
            string.Equals(d.Name, agentName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Key,  agentName, StringComparison.OrdinalIgnoreCase));
        if (agent is null) return NotFound($"Agent '{agentName}' not found in configured project.");

        var botId = _config["MicrosoftAppId"];
        if (string.IsNullOrEmpty(botId))
            return StatusCode(500, "MicrosoftAppId not configured on the App Service.");

        var zipBytes = await BuildManifestZipAsync(agent.Name, agent.Description, botId, botEndpointPath: null, ct);
        return File(zipBytes, "application/zip", $"{Sanitize(agent.Name)}.zip");
    }

    // ====================================================== Arbitrary foundry/project

    [HttpGet("{foundryHost}/{project}/manifest")]
    [Produces("text/html")]
    public async Task<ContentResult> CustomIndex(string foundryHost, string project, CancellationToken ct)
    {
        var projectEndpoint = ComposeProjectEndpoint(foundryHost, project);
        var http = _httpFactory.CreateClient("foundry-agents");
        IReadOnlyList<FoundryAgentsApi.AgentSummary> agents;
        try
        {
            agents = await FoundryAgentsApi.ListAgentsAsync(http, projectEndpoint, _agents.Credential, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list agents for arbitrary project {Project}", projectEndpoint);
            var msg = $"<html><body><h1>Cannot list agents for {System.Net.WebUtility.HtmlEncode(projectEndpoint)}</h1><pre>{System.Net.WebUtility.HtmlEncode(ex.Message)}</pre></body></html>";
            return new ContentResult { Content = msg, ContentType = "text/html", StatusCode = 502 };
        }

        var descriptors = agents
            .Where(a => a.IsActive)
            .Select(a => new AgentService.AgentDescriptor(
                Key:         SlugForUrl(a.Name),
                Name:        a.Name,
                Description: string.IsNullOrWhiteSpace(a.Description)
                            ? (a.Model is null ? "Foundry agent" : $"Foundry agent ({a.Model})")
                            : a.Description,
                Endpoint:    FoundryAgentsApi.ComposeAgentEndpoint(projectEndpoint, a.Name)))
            .ToList();

        var html = RenderIndex(
            agents:           descriptors,
            downloadRoot:     $"/admin/{Uri.EscapeDataString(foundryHost)}/{Uri.EscapeDataString(project)}/manifest",
            sourceLabel:      projectEndpoint,
            isCustomRoute:    true);
        return new ContentResult { Content = html, ContentType = "text/html", StatusCode = 200 };
    }

    [HttpGet("{foundryHost}/{project}/manifest/{agentName}")]
    public async Task<IActionResult> CustomDownload(string foundryHost, string project, string agentName, CancellationToken ct)
    {
        var projectEndpoint = ComposeProjectEndpoint(foundryHost, project);

        // Verify the agent exists in the foreign project (defensive — Foundry would
        // otherwise let us download a manifest for a nonexistent agent).
        var http = _httpFactory.CreateClient("foundry-agents");
        IReadOnlyList<FoundryAgentsApi.AgentSummary> agents;
        try
        {
            agents = await FoundryAgentsApi.ListAgentsAsync(http, projectEndpoint, _agents.Credential, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list agents for {Project} while resolving {AgentName}", projectEndpoint, agentName);
            return StatusCode(502, $"Cannot reach Foundry project: {ex.Message}");
        }

        var agent = agents.FirstOrDefault(a =>
            string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (agent is null) return NotFound($"Agent '{agentName}' not found in project {projectEndpoint}.");

        var botId = _config["MicrosoftAppId"];
        if (string.IsNullOrEmpty(botId))
            return StatusCode(500, "MicrosoftAppId not configured on the App Service.");

        // The URL-routed messaging path. The Bot Service registration created
        // for this manifest must be configured with this endpoint.
        var botEndpointPath = $"/api/messages/{foundryHost}/{project}/{agent.Name}";

        var description = string.IsNullOrWhiteSpace(agent.Description)
            ? (agent.Model is null ? "Foundry agent" : $"Foundry agent ({agent.Model})")
            : agent.Description;

        var zipBytes = await BuildManifestZipAsync(agent.Name, description, botId, botEndpointPath, ct);
        return File(zipBytes, "application/zip", $"{Sanitize(agent.Name)}.zip");
    }

    // ====================================================== helpers

    /// <summary>
    /// Build the full project endpoint URL from a route's foundryHost+project pair.
    /// Same logic as BotMessagesController so both routes interpret URLs identically.
    /// </summary>
    private static string ComposeProjectEndpoint(string foundryHost, string project)
    {
        if (foundryHost.StartsWith("https%3A", StringComparison.OrdinalIgnoreCase))
        {
            // URL-encoded full project URL (already includes /api/projects/{name}).
            return Uri.UnescapeDataString(foundryHost).TrimEnd('/');
        }
        return $"https://{foundryHost}.services.ai.azure.com/api/projects/{project}";
    }

    private static string SlugForUrl(string s)
    {
        if (string.IsNullOrEmpty(s)) return "agent";
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "agent" : slug;
    }

    private async Task<byte[]> BuildManifestZipAsync(
        string agentName, string agentDescription, string botId, string? botEndpointPath, CancellationToken ct)
    {
        // When Teams SSO is configured for the bot, embed webApplicationInfo
        // so Teams attempts silent SSO instead of showing an interactive
        // OAuthCard. The AAD app id may differ from the bot UAMI's client id
        // — TeamsSso:AadAppId overrides it.
        var ssoAppId   = _config["TeamsSso:AadAppId"];
        var ssoResource = _config["TeamsSso:Resource"];
        var manifest   = ManifestBuilder.Build(
            agentName, agentDescription, botId,
            botEndpointPath: botEndpointPath,
            ssoAadAppId: ssoAppId,
            ssoResource: ssoResource);

        var colorPath   = Path.Combine(_env.WebRootPath, "color.png");
        var outlinePath = Path.Combine(_env.WebRootPath, "outline.png");

        await using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddTextEntryAsync(zip, "manifest.json", manifest.ToString(Formatting.Indented), ct);
            AddFileEntry(zip, "color.png",   colorPath);
            AddFileEntry(zip, "outline.png", outlinePath);
        }
        ms.Position = 0;
        return ms.ToArray();
    }

    private static async Task AddTextEntryAsync(ZipArchive zip, string entryName, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await s.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
    }

    private static void AddFileEntry(ZipArchive zip, string entryName, string sourcePath)
    {
        if (!System.IO.File.Exists(sourcePath)) return;
        zip.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
    }

    private static string Sanitize(string s) => ManifestBuilder.SanitizeForFilename(s);

    private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    // ====================================================== HTML rendering

    private string RenderIndex(
        IReadOnlyList<AgentService.AgentDescriptor> agents,
        string downloadRoot,
        string sourceLabel,
        bool isCustomRoute)
    {
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>Foundry → Teams Manifest Generator</title>
<style>
  *{box-sizing:border-box}
  body{margin:0;font-family:-apple-system,Segoe UI,Roboto,sans-serif;background:#f3f4f6;color:#1f2328;min-height:100vh}
  header{background:#0d1117;color:#fff;padding:16px 24px}
  header h1{margin:0;font-size:18px;font-weight:600}
  header p{margin:4px 0 0;opacity:.7;font-size:13px;font-family:ui-monospace,Consolas,monospace}
  main{max-width:880px;margin:0 auto;padding:24px}
  .agent{background:#fff;border:1px solid #d0d7de;border-radius:8px;padding:16px;margin-bottom:12px;display:flex;align-items:center;justify-content:space-between;gap:16px}
  .agent h3{margin:0 0 4px;font-size:16px}
  .agent .desc{margin:0;color:#656d76;font-size:14px}
  .agent .id{font-family:ui-monospace,Consolas,monospace;font-size:11px;color:#8b949e;margin-top:6px;word-break:break-all}
  .agent .key{display:inline-block;background:#ddf4ff;color:#0969da;padding:2px 8px;border-radius:10px;font-size:11px;margin-top:6px;font-weight:600;font-family:ui-monospace,Consolas,monospace}
  .btn{background:#0969da;color:#fff;border:0;padding:10px 18px;border-radius:6px;font-weight:600;cursor:pointer;text-decoration:none;display:inline-block;font-size:14px;white-space:nowrap}
  .btn:hover{background:#0550ae}
  .note{background:#fff8c5;border:1px solid #eac54f;padding:12px 16px;border-radius:6px;margin-bottom:16px;font-size:13px;line-height:1.6}
  .note.routed{background:#ddf4ff;border-color:#54aeff}
  .note code{background:rgba(0,0,0,.08);padding:1px 5px;border-radius:3px;font-family:ui-monospace,Consolas,monospace;font-size:12px}
  .empty{padding:48px;text-align:center;color:#656d76}
</style></head><body>
<header>
  <h1>Foundry → Teams Manifest Generator</h1>
""");
        sb.Append($"  <p>{Html(sourceLabel)}</p>\n</header>\n<main>\n");

        if (isCustomRoute)
        {
            sb.Append($$"""
              <div class="note routed">
                <strong>URL-routed manifest.</strong> Each zip embeds the URL-routed messaging path
                <code>/api/messages/{foundryHost}/{project}/{agent}</code>. The Bot Service
                registration created from this manifest must be configured with that exact path
                so traffic lands on the right per-agent route in the proxy.
              </div>
            """);
        }
        else
        {
            sb.Append($$"""
              <div class="note">
                <strong>How it works.</strong> Each zip uses the same bot ID
                (<code>{{Html(_config["MicrosoftAppId"] ?? "(missing)")}}</code>) so they all route to this App Service.
                Use <code>/agents</code> in the bot to switch between agents at runtime.
              </div>
            """);
        }

        if (agents.Count == 0)
        {
            sb.Append("<div class=\"empty\">No active agents found in this project.</div>");
        }
        else
        {
            foreach (var a in agents.OrderBy(a => a.Name))
            {
                sb.Append("<div class=\"agent\">");
                sb.Append("<div>");
                sb.Append($"<h3>{Html(a.Name)}</h3>");
                if (!string.IsNullOrEmpty(a.Description))
                    sb.Append($"<p class=\"desc\">{Html(a.Description)}</p>");
                sb.Append($"<span class=\"key\">{Html(a.Key)}</span>");
                sb.Append($"<div class=\"id\">{Html(a.Endpoint)}</div>");
                sb.Append("</div>");
                sb.Append($"<a class=\"btn\" href=\"{downloadRoot}/{Uri.EscapeDataString(a.Name)}\">Download zip</a>");
                sb.Append("</div>");
            }
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}

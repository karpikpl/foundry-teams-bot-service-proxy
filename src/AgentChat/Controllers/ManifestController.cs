using System.IO.Compression;
using System.Text;
using AgentChat.Bots;
using AgentChat.Foundry;
using AgentChat.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentChat.Controllers;

/// <summary>
/// In-app Teams manifest generator. Routes are always scoped to a specific
/// Foundry (host, project) triple — the configured default <c>Foundry__ProjectEndpoint</c>
/// is just the convenient entry point for the HTML index page; downloads
/// always go through the explicit route to keep behavior consistent.
///
/// Routes:
///   <c>GET  /admin/agents</c>
///       JSON list of agents in the configured default project.
///   <c>GET  /admin/manifest</c>
///       HTML index → redirects to the configured default's
///       /admin/{foundryHost}/{project}/manifest page.
///   <c>GET  /admin/{foundryHost}/{project}/manifest</c>
///       HTML index for an arbitrary project + registration status per agent.
///   <c>GET  /admin/{foundryHost}/{project}/manifest/{agentName}</c>
///       Download the .zip. Requires a bot registration via
///       <see cref="RegistrationsController"/> — returns 409 with a helpful
///       message when no botId is registered for this triple.
/// </summary>
[ApiController]
[Route("admin")]
public class ManifestController : ControllerBase
{
    private readonly AgentService _agents;
    private readonly BotRegistrationStore _registrations;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ManifestController> _logger;

    public ManifestController(
        AgentService agents,
        BotRegistrationStore registrations,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<ManifestController> logger)
    {
        _agents        = agents;
        _registrations = registrations;
        _httpFactory   = httpFactory;
        _config        = config;
        _env           = env;
        _logger        = logger;
    }

    // ====================================================== JSON: configured project's agents

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

    // ====================================================== Default-project entry point

    [HttpGet("manifest")]
    public IActionResult DefaultRedirect()
    {
        // The configured default project's HTML index lives at the explicit
        // (foundryHost, project) route. Redirect there so all downloads flow
        // through the registration-aware code path.
        if (!TryDeriveFoundryHostAndProject(_agents.DefaultProjectEndpoint, out var foundryHost, out var project))
        {
            return BadRequest(new
            {
                error = $"Could not derive foundryHost / project from configured Foundry:ProjectEndpoint " +
                        $"'{_agents.DefaultProjectEndpoint}'. Visit /admin/{{foundryHost}}/{{project}}/manifest directly."
            });
        }
        return RedirectPermanent($"/admin/{foundryHost}/{project}/manifest");
    }

    // ====================================================== Arbitrary foundry/project

    [HttpGet("{foundryHost}/{project}/manifest")]
    [Produces("text/html")]
    public async Task<ContentResult> Index(string foundryHost, string project, CancellationToken ct)
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
            _logger.LogWarning(ex, "Failed to list agents for project {Project}", projectEndpoint);
            var msg = $"<html><body><h1>Cannot list agents for {Html(projectEndpoint)}</h1><pre>{Html(ex.Message)}</pre></body></html>";
            return new ContentResult { Content = msg, ContentType = "text/html", StatusCode = 502 };
        }

        // For each agent, look up its registered botId so the index can show
        // a status badge ("✓ registered" vs "needs registration").
        var rows = new List<RenderedRow>();
        foreach (var a in agents.Where(a => a.IsActive))
        {
            var reg = await _registrations.GetAsync(foundryHost, project, a.Name, ct);
            rows.Add(new RenderedRow(
                Name:        a.Name,
                Description: string.IsNullOrWhiteSpace(a.Description)
                            ? (a.Model is null ? "Foundry agent" : $"Foundry agent ({a.Model})")
                            : a.Description,
                Endpoint:    FoundryAgentsApi.ComposeAgentEndpoint(projectEndpoint, a.Name),
                BotId:       reg?.BotId,
                DisplayName: reg?.DisplayName));
        }

        var html = RenderIndex(foundryHost, project, projectEndpoint, rows);
        return new ContentResult { Content = html, ContentType = "text/html", StatusCode = 200 };
    }

    [HttpGet("{foundryHost}/{project}/manifest/{agentName}")]
    public async Task<IActionResult> Download(string foundryHost, string project, string agentName, CancellationToken ct)
    {
        // 1. The registration is the source of truth for botId. Without one
        //    we have no idea which Bot Service to wire the manifest to.
        var registration = await _registrations.GetAsync(foundryHost, project, agentName, ct);
        if (registration is null)
        {
            return Conflict(new
            {
                error = $"No bot registration for {foundryHost}/{project}/{agentName}.",
                fix   = $"PUT /admin/registrations/{foundryHost}/{project}/{agentName} with body {{ \"botId\": \"<bot-service-msa-app-id>\" }}"
            });
        }

        // 2. Verify the agent still exists in Foundry — defensive, otherwise
        //    we'd happily generate a manifest pointing at a nonexistent agent.
        var projectEndpoint = ComposeProjectEndpoint(foundryHost, project);
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

        var agent = agents.FirstOrDefault(a => string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (agent is null) return NotFound($"Agent '{agentName}' not found in project {projectEndpoint}.");

        var botEndpointPath = $"/api/messages/{foundryHost}/{project}/{agent.Name}";

        var description = string.IsNullOrWhiteSpace(agent.Description)
            ? (agent.Model is null ? "Foundry agent" : $"Foundry agent ({agent.Model})")
            : agent.Description;

        var zipBytes = await BuildManifestZipAsync(
            agentName:        registration.DisplayName ?? agent.Name,
            agentDescription: description,
            botId:            registration.BotId,
            botEndpointPath:  botEndpointPath,
            ct: ct);

        return File(zipBytes, "application/zip", $"{Sanitize(agent.Name)}.zip");
    }

    // ====================================================== helpers

    private record RenderedRow(string Name, string Description, string Endpoint, string? BotId, string? DisplayName);

    private static string ComposeProjectEndpoint(string foundryHost, string project)
    {
        if (foundryHost.StartsWith("https%3A", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(foundryHost).TrimEnd('/');
        }
        return $"https://{foundryHost}.services.ai.azure.com/api/projects/{project}";
    }

    /// <summary>
    /// Inverse: derive (foundryHost, project) from a project endpoint URL like
    /// https://{name}.services.ai.azure.com/api/projects/{project}.
    /// Returns false for unusual shapes (callers fall back to the explicit route).
    /// </summary>
    private static bool TryDeriveFoundryHostAndProject(string endpoint, out string foundryHost, out string project)
    {
        foundryHost = ""; project = "";
        try
        {
            var uri = new Uri(endpoint);
            if (!uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)) return false;
            foundryHost = uri.Host.Split('.')[0];
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var idx = Array.IndexOf(segments, "projects");
            if (idx < 0 || idx + 1 >= segments.Length) return false;
            project = segments[idx + 1];
            return true;
        }
        catch { return false; }
    }

    private async Task<byte[]> BuildManifestZipAsync(
        string agentName, string agentDescription, string botId, string? botEndpointPath, CancellationToken ct)
    {
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
    private static string Html(string s)     => System.Net.WebUtility.HtmlEncode(s ?? "");

    // ====================================================== HTML rendering

    private string RenderIndex(string foundryHost, string project, string projectEndpoint, IReadOnlyList<RenderedRow> agents)
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
  main{max-width:980px;margin:0 auto;padding:24px}
  .agent{background:#fff;border:1px solid #d0d7de;border-radius:8px;padding:16px;margin-bottom:12px;display:flex;align-items:center;justify-content:space-between;gap:16px}
  .agent h3{margin:0 0 4px;font-size:16px}
  .agent .desc{margin:0;color:#656d76;font-size:14px}
  .agent .id{font-family:ui-monospace,Consolas,monospace;font-size:11px;color:#8b949e;margin-top:6px;word-break:break-all}
  .badge{display:inline-block;font-size:11px;padding:2px 8px;border-radius:10px;font-weight:600;font-family:ui-monospace,Consolas,monospace;margin-top:6px}
  .badge-ok{background:#dafbe1;color:#1a7f37}
  .badge-warn{background:#fff8c5;color:#9a6700}
  .actions{display:flex;flex-direction:column;gap:6px;align-items:flex-end}
  .btn{background:#0969da;color:#fff;border:0;padding:8px 14px;border-radius:6px;font-weight:600;cursor:pointer;text-decoration:none;display:inline-block;font-size:13px;white-space:nowrap}
  .btn:hover{background:#0550ae}
  .btn.secondary{background:#fff;color:#0969da;border:1px solid #0969da}
  .btn.secondary:hover{background:#ddf4ff}
  .btn.danger{background:#fff;color:#cf222e;border:1px solid #cf222e}
  .btn.danger:hover{background:#ffebe9}
  .btn[disabled]{background:#d0d7de;color:#8b949e;cursor:not-allowed}
  .note{background:#ddf4ff;border:1px solid #54aeff;padding:12px 16px;border-radius:6px;margin-bottom:16px;font-size:13px;line-height:1.6}
  .note code{background:rgba(0,0,0,.08);padding:1px 5px;border-radius:3px;font-family:ui-monospace,Consolas,monospace;font-size:12px}
  .empty{padding:48px;text-align:center;color:#656d76}
  dialog{border:0;border-radius:8px;padding:20px;box-shadow:0 8px 24px rgba(0,0,0,.15);max-width:520px;width:90%}
  dialog::backdrop{background:rgba(0,0,0,.4)}
  dialog h3{margin:0 0 12px}
  dialog label{display:block;font-size:13px;color:#656d76;margin-top:10px}
  dialog input{width:100%;padding:8px;border:1px solid #d0d7de;border-radius:6px;font-size:14px;margin-top:4px;font-family:ui-monospace,Consolas,monospace}
  dialog .row{display:flex;gap:8px;justify-content:flex-end;margin-top:16px}
</style></head><body>
<header>
  <h1>Foundry → Teams Manifest Generator</h1>
""");
        sb.Append($"  <p>{Html(projectEndpoint)}</p>\n</header>\n<main>\n");
        sb.Append("""
  <div class="note">
    Each agent needs a registered <strong>bot ID</strong> (the MSA app id from its Bot Service registration)
    before you can download a sideloadable Teams manifest. Click <strong>Register bot</strong> to set or
    update the mapping. Manifests embed the URL-routed messaging path
    <code>/api/messages/{foundryHost}/{project}/{agent}</code>.
  </div>
""");

        if (agents.Count == 0)
        {
            sb.Append("<div class=\"empty\">No active agents found in this project.</div>");
        }
        else
        {
            foreach (var a in agents.OrderBy(a => a.Name))
            {
                var badge = a.BotId is null
                    ? "<span class=\"badge badge-warn\">⚠ needs registration</span>"
                    : $"<span class=\"badge badge-ok\">✓ {Html(a.BotId)}</span>";

                sb.Append("<div class=\"agent\">");
                sb.Append("<div>");
                sb.Append($"<h3>{Html(a.Name)}</h3>");
                if (!string.IsNullOrEmpty(a.Description))
                    sb.Append($"<p class=\"desc\">{Html(a.Description)}</p>");
                sb.Append(badge);
                sb.Append($"<div class=\"id\">{Html(a.Endpoint)}</div>");
                sb.Append("</div>");

                sb.Append("<div class=\"actions\">");
                var downloadAttrs = a.BotId is null ? "disabled" : "";
                sb.Append($"<a class=\"btn\" {downloadAttrs} href=\"/admin/{Html(foundryHost)}/{Html(project)}/manifest/{Uri.EscapeDataString(a.Name)}\">Download zip</a>");
                sb.Append($"<button class=\"btn secondary\" onclick=\"openRegister('{Html(a.Name)}', '{Html(a.BotId ?? "")}', '{Html(a.DisplayName ?? "")}')\">{(a.BotId is null ? "Register bot" : "Edit registration")}</button>");
                if (a.BotId is not null)
                    sb.Append($"<button class=\"btn danger\" onclick=\"unregister('{Html(a.Name)}')\">Unregister</button>");
                sb.Append("</div>");
                sb.Append("</div>");
            }
        }

        sb.Append($$"""
<dialog id="regDialog">
  <h3 id="regTitle">Register bot</h3>
  <form id="regForm">
    <label>Bot ID (MSA app id from Bot Service)
      <input id="regBotId" type="text" placeholder="00000000-0000-0000-0000-000000000000" required pattern="[0-9a-fA-F-]{36}"/>
    </label>
    <label>Display name (optional)
      <input id="regDisplayName" type="text" placeholder="e.g. Docs Bot — Production"/>
    </label>
    <div class="row">
      <button type="button" class="btn secondary" onclick="document.getElementById('regDialog').close()">Cancel</button>
      <button type="submit" class="btn">Save</button>
    </div>
  </form>
</dialog>
<script>
  const FOUNDRY_HOST = {{JsonConvert.SerializeObject(foundryHost)}};
  const PROJECT      = {{JsonConvert.SerializeObject(project)}};
  let currentAgent = null;

  function openRegister(agentName, currentBotId, currentDisplayName) {
    currentAgent = agentName;
    document.getElementById('regTitle').textContent = `Register bot for ${agentName}`;
    document.getElementById('regBotId').value = currentBotId || '';
    document.getElementById('regDisplayName').value = currentDisplayName || '';
    document.getElementById('regDialog').showModal();
  }
  document.getElementById('regForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const botId = document.getElementById('regBotId').value.trim();
    const displayName = document.getElementById('regDisplayName').value.trim() || null;
    const r = await fetch(`/admin/registrations/${encodeURIComponent(FOUNDRY_HOST)}/${encodeURIComponent(PROJECT)}/${encodeURIComponent(currentAgent)}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ botId, displayName })
    });
    if (!r.ok) { alert(`Failed: ${await r.text()}`); return; }
    location.reload();
  });
  async function unregister(agentName) {
    if (!confirm(`Unregister ${agentName}?`)) return;
    const r = await fetch(`/admin/registrations/${encodeURIComponent(FOUNDRY_HOST)}/${encodeURIComponent(PROJECT)}/${encodeURIComponent(agentName)}`, { method: 'DELETE' });
    if (!r.ok) { alert(`Failed: ${await r.text()}`); return; }
    location.reload();
  }
</script>
""");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}

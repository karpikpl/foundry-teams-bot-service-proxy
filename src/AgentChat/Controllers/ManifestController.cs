using System.IO.Compression;
using System.Text;
using AgentChat.Bots;
using AgentChat.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentChat.Controllers;

/// <summary>
/// In-app Teams manifest generator. Hit /admin/manifest in a browser to see a
/// list of Foundry agents with a "Download" button for each. The zip you get
/// is sideloadable in Teams.
///
/// All generated manifests share the same botId (= the App Service's Bot
/// Service registration), so the bot routes them identically; users switch
/// the underlying Foundry agent inside the chat with /agents.
/// </summary>
[ApiController]
[Route("admin")]
public class ManifestController : ControllerBase
{
    private readonly AgentService _agents;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ManifestController> _logger;

    public ManifestController(
        AgentService agents,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<ManifestController> logger)
    {
        _agents = agents;
        _config = config;
        _env    = env;
        _logger = logger;
    }

    // ---------------- JSON API ----------------

    [HttpGet("agents")]
    public IActionResult ListAgents()
    {
        var result = _agents.Descriptors.Select(d => new
        {
            key         = d.Key,
            name        = d.Name,
            description = d.Description,
            endpoint    = d.Endpoint
        });
        return Ok(result);
    }

    [HttpGet("manifest/{agentName}")]
    public async Task<IActionResult> Download(string agentName, CancellationToken ct)
    {
        var agent = _agents.Descriptors.FirstOrDefault(d =>
            string.Equals(d.Name, agentName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Key,  agentName, StringComparison.OrdinalIgnoreCase));
        if (agent is null) return NotFound($"Agent {agentName} not found");

        var botId = _config["MicrosoftAppId"];
        if (string.IsNullOrEmpty(botId))
            return StatusCode(500, "MicrosoftAppId not configured on the App Service.");

        var zipBytes = await BuildManifestZipAsync(agent.Name, agent.Description, botId, ct);

        var safeName = Sanitize(agent.Name);
        return File(zipBytes, "application/zip", $"{safeName}.zip");
    }

    // ---------------- HTML UI ----------------

    [HttpGet("manifest")]
    [Produces("text/html")]
    public ContentResult Index()
    {
        var agents = _agents.Descriptors.OrderBy(a => a.Name).ToList();

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
  header p{margin:4px 0 0;opacity:.7;font-size:13px}
  main{max-width:880px;margin:0 auto;padding:24px}
  .agent{background:#fff;border:1px solid #d0d7de;border-radius:8px;padding:16px;margin-bottom:12px;display:flex;align-items:center;justify-content:space-between;gap:16px}
  .agent h3{margin:0 0 4px;font-size:16px}
  .agent .desc{margin:0;color:#656d76;font-size:14px}
  .agent .id{font-family:ui-monospace,Consolas,monospace;font-size:11px;color:#8b949e;margin-top:6px}
  .agent .model{display:inline-block;background:#ddf4ff;color:#0969da;padding:2px 8px;border-radius:10px;font-size:11px;margin-top:6px;font-weight:600}
  .btn{background:#0969da;color:#fff;border:0;padding:10px 18px;border-radius:6px;font-weight:600;cursor:pointer;text-decoration:none;display:inline-block;font-size:14px;white-space:nowrap}
  .btn:hover{background:#0550ae}
  .note{background:#fff8c5;border:1px solid #eac54f;padding:12px 16px;border-radius:6px;margin-bottom:16px;font-size:13px;line-height:1.6}
  .note code{background:rgba(0,0,0,.08);padding:1px 5px;border-radius:3px;font-family:ui-monospace,Consolas,monospace;font-size:12px}
  .empty{padding:48px;text-align:center;color:#656d76}
</style></head><body>
<header>
  <h1>Foundry → Teams Manifest Generator</h1>
  <p>Pick a Foundry agent, download its Teams manifest zip, sideload in Teams.</p>
</header>
<main>
""");

        sb.Append($$"""
  <div class="note">
    <strong>How it works.</strong> Each zip uses the same bot ID
    (<code>{{_config["MicrosoftAppId"]}}</code>) so they all route to this App Service.
    The agent name + description on each app entry comes from Foundry.
    To switch agents at runtime inside the bot, type <code>/agents</code>.
  </div>
""");

        if (agents.Count == 0)
        {
            sb.Append("<div class=\"empty\">No agents in this project yet.</div>");
        }
        else
        {
            foreach (var a in agents)
            {
                sb.Append("<div class=\"agent\">");
                sb.Append("<div>");
                sb.Append($"<h3>{Html(a.Name)}</h3>");
                if (!string.IsNullOrEmpty(a.Description))
                    sb.Append($"<p class=\"desc\">{Html(a.Description)}</p>");
                sb.Append($"<span class=\"model\">{Html(a.Key)}</span>");
                sb.Append($"<div class=\"id\">{Html(a.Endpoint)}</div>");
                sb.Append("</div>");
                sb.Append($"<a class=\"btn\" href=\"/admin/manifest/{Html(a.Name)}\">Download zip</a>");
                sb.Append("</div>");
            }
        }

        sb.Append("</main></body></html>");

        return new ContentResult
        {
            Content     = sb.ToString(),
            ContentType = "text/html",
            StatusCode  = 200
        };
    }

    // ---------------- internals ----------------

    private async Task<byte[]> BuildManifestZipAsync(string agentName, string agentDescription, string botId, CancellationToken ct)
    {
        var manifest = ManifestBuilder.Build(agentName, agentDescription, botId);

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
}

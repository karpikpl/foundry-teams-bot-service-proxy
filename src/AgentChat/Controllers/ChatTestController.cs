using System.ClientModel;
using System.Text;
using System.Text.Json;
using AgentChat.Bots;
using AgentChat.Foundry;
using AgentChat.Services;
using Microsoft.AspNetCore.Mvc;
using OpenAI.Responses;

namespace AgentChat.Controllers;

/// <summary>
/// Browser test harness: chat with any agent in the configured Foundry project
/// without sideloading to Teams. Mirrors what the Teams bot does — creates a
/// Foundry conversation, posts the user message as an item, streams the
/// response — but renders to a plain web page over SSE instead of Bot
/// Framework activities.
///
/// Auth: uses the App Service UMI (same as the bot's catalog calls today).
/// Per-user OBO isn't wired in here because the browser context doesn't carry
/// Teams SSO; for end-to-end user-identity testing, use the actual Teams bot
/// instead.
///
/// Routes:
///   <c>GET  /admin/chat</c>                              HTML page
///   <c>POST /admin/chat/conversations</c>                start a new Foundry conversation for an agent
///   <c>POST /admin/chat/messages</c>                     send a user message + stream the response
///   <c>DELETE /admin/chat/conversations/{id}</c>         clean up
/// </summary>
[ApiController]
[Route("admin/chat")]
public class ChatTestController : ControllerBase
{
    private readonly AgentService _agents;
    private readonly AgentClientCache _clientCache;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ChatTestController> _logger;

    public ChatTestController(
        AgentService agents,
        AgentClientCache clientCache,
        IWebHostEnvironment env,
        ILogger<ChatTestController> logger)
    {
        _agents      = agents;
        _clientCache = clientCache;
        _env         = env;
        _logger      = logger;
    }

    // ====================================================== HTML page

    [HttpGet("")]
    [Produces("text/html")]
    public IActionResult Page()
    {
        var path = Path.Combine(_env.WebRootPath, "chat.html");
        if (!System.IO.File.Exists(path))
            return NotFound("chat.html missing from wwwroot");
        return PhysicalFile(path, "text/html");
    }

    // ====================================================== Conversation lifecycle

    public sealed record CreateConvRequest(string AgentKey);
    public sealed record CreateConvResponse(string ConversationId, string AgentName, string Endpoint);

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation([FromBody] CreateConvRequest body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body?.AgentKey)) return BadRequest(new { error = "agentKey is required" });

        var agent = await _agents.FindByKeyAsync(body.AgentKey, projectEndpoint: null, ct);
        if (agent is null) return NotFound(new { error = $"agent '{body.AgentKey}' not found in configured project" });

        var foundry = _clientCache.For(agent.Endpoint);
        var convClient = foundry.OpenAI.GetConversationClient();
        var result = await convClient.CreateConversationAsync(
            BinaryContent.Create(BinaryData.FromString("{}")), options: null);
        var doc = JsonDocument.Parse(result.GetRawResponse().Content.ToString());
        var id = doc.RootElement.GetProperty("id").GetString()!;
        return Ok(new CreateConvResponse(id, agent.Name, agent.Endpoint));
    }

    [HttpDelete("conversations/{conversationId}")]
    public async Task<IActionResult> DeleteConversation(string conversationId, [FromQuery] string agentKey, CancellationToken ct)
    {
        var agent = await _agents.FindByKeyAsync(agentKey, projectEndpoint: null, ct);
        if (agent is null) return NotFound(new { error = $"agent '{agentKey}' not found" });

        var foundry = _clientCache.For(agent.Endpoint);
        try
        {
            await foundry.OpenAI.GetConversationClient().DeleteConversationAsync(conversationId, options: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Delete conversation failed");
        }
        return NoContent();
    }

    // ====================================================== Streaming chat

    public sealed record MessageRequest(string AgentKey, string ConversationId, string Message);

    /// <summary>
    /// POST the user's message and stream the response back as Server-Sent
    /// Events. We translate Foundry's StreamingResponseUpdate hierarchy into
    /// a small set of named events the browser can switch on without parsing
    /// the OpenAI SDK shapes:
    ///
    ///   event: text     — text delta chunk (data is the delta string, raw)
    ///   event: tool     — MCP / function tool call (JSON: { tool, server, output })
    ///   event: consent  — OAuth consent required (JSON: { serverLabel, consentLink })
    ///   event: done     — final usage block (JSON: { inputTokens, outputTokens, totalTokens })
    ///   event: error    — error (data: human-readable message)
    /// </summary>
    [HttpPost("messages")]
    public async Task StreamMessage([FromBody] MessageRequest body, CancellationToken ct)
    {
        Response.Headers["Content-Type"]      = "text/event-stream";
        Response.Headers["Cache-Control"]     = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        if (string.IsNullOrEmpty(body?.AgentKey) || string.IsNullOrEmpty(body.ConversationId) || string.IsNullOrEmpty(body.Message))
        {
            await WriteSseAsync("error", "agentKey, conversationId, and message are all required", ct);
            return;
        }

        var agent = await _agents.FindByKeyAsync(body.AgentKey, projectEndpoint: null, ct);
        if (agent is null)
        {
            await WriteSseAsync("error", $"agent '{body.AgentKey}' not found", ct);
            return;
        }

        var foundry   = _clientCache.For(agent.Endpoint);
        var convs     = foundry.OpenAI.GetConversationClient();
        var responses = foundry.OpenAI.GetResponsesClient();

        // 1. Post the user message to the conversation.
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\"items\":[");
            sb.Append(System.ClientModel.Primitives.ModelReaderWriter.Write(
                ResponseItem.CreateUserMessageItem(body.Message)).ToString());
            sb.Append("]}");
            await convs.CreateConversationItemsAsync(
                body.ConversationId,
                BinaryContent.Create(BinaryData.FromString(sb.ToString())),
                include: null,
                options: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POST conversation items failed");
            await WriteSseAsync("error", $"Failed to post message: {ex.Message}", ct);
            return;
        }

        // 2. Stream the response.
        try
        {
            var opts = new CreateResponseOptions
            {
                ConversationOptions = new ResponseConversationOptions(body.ConversationId),
                StreamingEnabled    = true
            };
            var seenIds = new HashSet<string>();
            await foreach (var update in responses.CreateResponseStreamingAsync(opts, ct))
            {
                switch (update)
                {
                    case StreamingResponseOutputTextDeltaUpdate d when !string.IsNullOrEmpty(d.Delta):
                        await WriteSseAsync("text", d.Delta!, ct);
                        break;

                    case StreamingResponseOutputItemDoneUpdate done:
                        var item = done.Item;
                        if (item.Id is { } id && !seenIds.Add(id)) break;
                        await HandleItemAsync(item, ct);
                        break;

                    case StreamingResponseCompletedUpdate completed:
                        var u = completed.Response?.Usage;
                        var payload = JsonSerializer.Serialize(new
                        {
                            inputTokens  = u?.InputTokenCount  ?? 0,
                            outputTokens = u?.OutputTokenCount ?? 0,
                            totalTokens  = u?.TotalTokenCount  ?? 0
                        });
                        await WriteSseAsync("done", payload, ct);
                        break;

                    case StreamingResponseFailedUpdate failed:
                        await WriteSseAsync("error", failed.Response?.Error?.Message ?? "Run failed", ct);
                        return;

                    case StreamingResponseErrorUpdate err:
                        await WriteSseAsync("error", $"{err.Code ?? "error"}: {err.Message ?? "unknown"}", ct);
                        return;

                    default:
                        if (TryParseConsentEvent(update, out var serverLabel, out var link))
                        {
                            await WriteSseAsync("consent", JsonSerializer.Serialize(new { serverLabel, consentLink = link }), ct);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream failed");
            await WriteSseAsync("error", ex.Message, ct);
        }
    }

    private async Task HandleItemAsync(ResponseItem item, CancellationToken ct)
    {
        switch (item)
        {
            case McpToolCallItem mcp:
                await WriteSseAsync("tool", JsonSerializer.Serialize(new
                {
                    kind   = "mcp",
                    tool   = mcp.ToolName,
                    server = mcp.ServerLabel,
                    output = Truncate(mcp.ToolOutput ?? mcp.Error?.ToString() ?? "(no output)", 2000)
                }), ct);
                break;

            case FunctionCallResponseItem fc:
                await WriteSseAsync("tool", JsonSerializer.Serialize(new
                {
                    kind = "function",
                    tool = fc.FunctionName,
                    args = fc.FunctionArguments?.ToString() ?? "{}"
                }), ct);
                break;

            default:
                // Try the Foundry-specific oauth_consent_request shape via raw JSON.
                if (TryParseConsent(item, out var serverLabel, out var link))
                {
                    await WriteSseAsync("consent", JsonSerializer.Serialize(new { serverLabel, consentLink = link }), ct);
                }
                break;
        }
    }

    private bool TryParseConsent(ResponseItem item, out string serverLabel, out string consentLink)
    {
        serverLabel = ""; consentLink = "";
        try
        {
            var bd = System.ClientModel.Primitives.ModelReaderWriter.Write(item);
            using var doc = JsonDocument.Parse(bd);
            var root = doc.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "oauth_consent_request", StringComparison.OrdinalIgnoreCase))
                return false;

            var rawLink = root.TryGetProperty("consent_link", out var cl) ? cl.GetString() : null;
            var cleanUrl = ConsentLinkParser.ExtractConsentUrl(rawLink);
            if (string.IsNullOrEmpty(cleanUrl))
            {
                _logger.LogWarning("Skipping OAuth consent request {ItemId}: no URL found in consent_link", root.TryGetProperty("id", out var id) ? id.GetString() : null);
                return false;
            }

            consentLink = cleanUrl;
            serverLabel = root.TryGetProperty("server_label", out var sl) ? sl.GetString() ?? "" : "";
            return true;
        }
        catch { return false; }
    }

    private bool TryParseConsentEvent(StreamingResponseUpdate update, out string serverLabel, out string consentLink)
    {
        serverLabel = ""; consentLink = "";
        try
        {
            var bd = System.ClientModel.Primitives.ModelReaderWriter.Write(update);
            using var doc = JsonDocument.Parse(bd);
            var root = doc.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "response.oauth_consent_requested", StringComparison.OrdinalIgnoreCase))
                return false;

            var rawLink = root.TryGetProperty("consent_link", out var cl) ? cl.GetString() : null;
            var cleanUrl = ConsentLinkParser.ExtractConsentUrl(rawLink);
            if (string.IsNullOrEmpty(cleanUrl))
            {
                _logger.LogWarning("Skipping OAuth consent event {ItemId}: no URL found in consent_link", root.TryGetProperty("item_id", out var id) ? id.GetString() : null);
                return false;
            }

            consentLink = cleanUrl;
            serverLabel = root.TryGetProperty("server_label", out var sl) ? sl.GetString() ?? "" : "";
            return true;
        }
        catch { return false; }
    }

    private async Task WriteSseAsync(string eventName, string data, CancellationToken ct)
    {
        // SSE: every line of data must be prefixed with "data: "; multi-line
        // values are split on '\n' so the browser reassembles them with '\n'.
        var sb = new StringBuilder();
        sb.Append("event: ").Append(eventName).Append('\n');
        foreach (var line in data.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await Response.Body.WriteAsync(bytes, ct);
        await Response.Body.FlushAsync(ct);
    }

    private static string Truncate(string s, int max)
        => s.Length > max ? s.Substring(0, max) + "…(truncated)" : s;
}

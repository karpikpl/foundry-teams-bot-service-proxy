using System.ClientModel;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AgentChat.Auth;
using AgentChat.Bots;
using AgentChat.Foundry;
using AgentChat.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;
using OpenAI.Responses;

namespace AgentChat.Controllers;

/// <summary>
/// Browser test harness: chat with any agent in the configured Foundry project
/// without sideloading to Teams. Mirrors what the Teams bot does — creates a
/// Foundry conversation, posts the user message as an item, streams the
/// response — but renders to a plain web page over SSE instead of Bot
/// Framework activities.
///
/// Auth: when AdminChatAuth is enabled, this browser harness requires Entra ID
/// sign-in and forwards the signed-in user's Foundry token per request. Agent
/// catalog lookup does not fall back to the App Service UMI.
///
/// Routes:
///   <c>GET  /admin/chat</c>                              HTML page
///   <c>POST /admin/chat/conversations</c>                start a new Foundry conversation for an agent
///   <c>POST /admin/chat/messages</c>                     send a user message + stream the response
///   <c>DELETE /admin/chat/conversations/{id}</c>         clean up
/// </summary>
[ApiController]
[Route("admin/chat")]
[ServiceFilter(typeof(AdminChatAuthFilter))]
[AuthorizeForScopes(Scopes = new[] { AdminChatAuthOptions.FoundryScope })]
public class ChatTestController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, PendingMcpApproval> PendingApprovals = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> CurrentResponseIds = new(StringComparer.Ordinal);

    private readonly AgentService _agents;
    private readonly AgentClientCache _clientCache;
    private readonly AgentFileService _files;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ChatTestController> _logger;
    private readonly AdminChatAuthOptions _adminChatAuth;
    private readonly ITokenAcquisition? _tokenAcquisition;

    public ChatTestController(
        AgentService agents,
        AgentClientCache clientCache,
        AgentFileService files,
        IWebHostEnvironment env,
        ILogger<ChatTestController> logger,
        AdminChatAuthOptions? adminChatAuth = null,
        ITokenAcquisition? tokenAcquisition = null)
    {
        _agents           = agents;
        _clientCache      = clientCache;
        _files            = files;
        _env              = env;
        _logger           = logger;
        _adminChatAuth    = adminChatAuth ?? new AdminChatAuthOptions();
        _tokenAcquisition = tokenAcquisition;
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

    [HttpGet("whoami")]
    public IActionResult WhoAmI()
    {
        var name = User.FindFirst("name")?.Value
            ?? User.FindFirst(ClaimTypes.Name)?.Value
            ?? User.Identity?.Name;
        var email = User.FindFirst("preferred_username")?.Value
            ?? User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value;

        return Ok(new
        {
            enabled = _adminChatAuth.Enabled,
            authenticated = User.Identity?.IsAuthenticated == true,
            name,
            email
        });
    }

    [HttpGet("signout")]
    public IActionResult SignOutOfAdminChat()
    {
        if (!_adminChatAuth.Enabled)
            return Redirect("/admin/chat");

        return SignOut(
            new AuthenticationProperties { RedirectUri = "/admin/chat" },
            OpenIdConnectDefaults.AuthenticationScheme,
            CookieAuthenticationDefaults.AuthenticationScheme);
    }

    // ====================================================== Conversation lifecycle

    public sealed record CreateConvRequest(string AgentKey, string? FoundryHost = null, string? Project = null);
    public sealed record CreateConvResponse(string ConversationId, string AgentName, string Endpoint);

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation([FromBody] CreateConvRequest body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body?.AgentKey)) return BadRequest(new { error = "agentKey is required" });
        if (!TryProjectEndpoint(body.FoundryHost, body.Project, out var projectEndpoint, out var projectError))
            return BadRequest(new { error = projectError });

        var user = await GetFoundryUserContextAsync();
        using var userAuth = BeginFoundryUserAuthScope(user?.Token);
        var agent = await _agents.FindByKeyAsync(body.AgentKey, user?.ObjectId, user?.Token, projectEndpoint, ct);
        if (agent is null) return NotFound(new { error = $"agent '{body.AgentKey}' not found" });

        var foundry = _clientCache.For(agent.Endpoint);
        var convClient = foundry.OpenAI.GetConversationClient();
        var result = await convClient.CreateConversationAsync(
            BinaryContent.Create(BinaryData.FromString("{}")), options: null);
        var doc = JsonDocument.Parse(result.GetRawResponse().Content.ToString());
        var id = doc.RootElement.GetProperty("id").GetString()!;
        return Ok(new CreateConvResponse(id, agent.Name, agent.Endpoint));
    }

    [HttpDelete("conversations/{conversationId}")]
    public async Task<IActionResult> DeleteConversation(
        string conversationId,
        [FromQuery] string agentKey,
        [FromQuery] string? foundryHost,
        [FromQuery] string? project,
        CancellationToken ct)
    {
        if (!TryProjectEndpoint(foundryHost, project, out var projectEndpoint, out var projectError))
            return BadRequest(new { error = projectError });

        var user = await GetFoundryUserContextAsync();
        using var userAuth = BeginFoundryUserAuthScope(user?.Token);
        var agent = await _agents.FindByKeyAsync(agentKey, user?.ObjectId, user?.Token, projectEndpoint, ct);
        if (agent is null) return NotFound(new { error = $"agent '{agentKey}' not found" });

        PendingApprovals.TryRemove(PendingKey(agentKey, conversationId), out _);
        CurrentResponseIds.TryRemove(PendingKey(agentKey, conversationId), out _);
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

    public sealed record ApprovalRequest(string RequestId, bool Approve);
    public sealed record MessageRequest(string AgentKey, string ConversationId, string? Message, string? FoundryHost = null, string? Project = null, ApprovalRequest? Approval = null);
    public sealed class FileMessageRequest
    {
        public string AgentKey { get; init; } = "";
        public string ConversationId { get; init; } = "";
        public string? Message { get; init; }
        public string? FoundryHost { get; init; }
        public string? Project { get; init; }
        public List<IFormFile> Files { get; init; } = [];
    }

    /// <summary>
    /// POST the user's message and stream the response back as Server-Sent
    /// Events. We translate Foundry's StreamingResponseUpdate hierarchy into
    /// a small set of named events the browser can switch on without parsing
    /// the OpenAI SDK shapes:
    ///
    ///   event: text     — text delta chunk (data is the delta string, raw)
    ///   event: tool     — MCP, web-search, code-interpreter, or function call
    ///                     (JSON: { kind, tool, server, args, output })
    ///   event: consent  — OAuth consent required (JSON: { serverLabel, consentLink })
    ///   event: approval — MCP tool-call approval required (JSON: { approval_request_id, server_label, tool_name, arguments_summary })
    ///   event: file     — generated file (JSON: { fileName, contentType, size, url })
    ///   event: done     — final usage block (JSON: { inputTokens, outputTokens, totalTokens })
    ///   event: error    — error (data: human-readable message)
    /// </summary>
    [HttpPost("messages")]
    [Consumes("application/json")]
    public async Task StreamMessage([FromBody] MessageRequest body, CancellationToken ct)
        => await StreamMessageCoreAsync(body, [], ct);

    [HttpPost("messages")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(60L * 1024 * 1024)]
    public async Task StreamMessageWithFiles([FromForm] FileMessageRequest body, CancellationToken ct)
    {
        try
        {
            var inputFiles = await _files.ReadFormFilesAsync(body.Files, ct);
            await StreamMessageCoreAsync(
                new MessageRequest(
                    body.AgentKey,
                    body.ConversationId,
                    body.Message,
                    body.FoundryHost,
                    body.Project),
                inputFiles,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            await WriteSseAsync("error", ex.Message, ct);
        }
    }

    private async Task StreamMessageCoreAsync(
        MessageRequest body,
        IReadOnlyList<AgentInputFile> inputFiles,
        CancellationToken ct)
    {
        Response.Headers["Content-Type"]      = "text/event-stream";
        Response.Headers["Cache-Control"]     = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        if (string.IsNullOrEmpty(body?.AgentKey) ||
            string.IsNullOrEmpty(body.ConversationId) ||
            (string.IsNullOrWhiteSpace(body.Message) && body.Approval is null && inputFiles.Count == 0))
        {
            await WriteSseAsync("error", "agentKey, conversationId, and a message, attachment, or approval are required", ct);
            return;
        }

        var pendingKey = PendingKey(body.AgentKey, body.ConversationId);
        if (body.Approval is null && PendingApprovals.ContainsKey(pendingKey))
        {
            await WriteSseAsync("error", McpApproval.PendingReminder, ct);
            return;
        }

        if (!TryProjectEndpoint(body.FoundryHost, body.Project, out var projectEndpoint, out var projectError))
        {
            await WriteSseAsync("error", projectError, ct);
            return;
        }

        var user = await GetFoundryUserContextAsync();
        using var userAuth = BeginFoundryUserAuthScope(user?.Token);
        var agent = await _agents.FindByKeyAsync(body.AgentKey, user?.ObjectId, user?.Token, projectEndpoint, ct);
        if (agent is null)
        {
            await WriteSseAsync("error", $"agent '{body.AgentKey}' not found", ct);
            return;
        }

        var foundry   = _clientCache.For(agent.Endpoint);
        var responses = foundry.OpenAI.GetResponsesClient();

        IReadOnlyList<ResponseItem>? inputItems;
        string? firstPreviousResponseId = null;
        if (body.Approval is { } approval)
        {
            if (!PendingApprovals.TryGetValue(pendingKey, out var pending) ||
                !string.Equals(pending.ApprovalRequestId, approval.RequestId, StringComparison.Ordinal))
            {
                await WriteSseAsync("error", "I don't see that pending MCP approval anymore. Send your message again to retry.", ct);
                return;
            }
            inputItems = new[] { ResponseItem.CreateMcpApprovalResponseItem(approval.RequestId, approval.Approve) };
            firstPreviousResponseId = pending.PreviousResponseId;
        }
        else
        {
            inputItems = new[] { _files.CreateUserMessage(body.Message, inputFiles) };
        }

        // Stream the response. A user turn binds the Foundry conversation only
        // when no prior response id is known; otherwise every hop chains via
        // previous_response_id, matching the Foundry Responses sample.
        try
        {
            int safety = 0;
            var clearApprovalOnNextStream = body.Approval is not null;
            while (true)
            {
                if (++safety > 8)
                {
                    await WriteSseAsync("error", "Aborting after too many tool/approval round-trips.", ct);
                    return;
                }

                var opts = BuildResponseOptions(body.ConversationId, pendingKey, inputItems, firstPreviousResponseId);
                var step = await StreamFoundryOnceAsync(foundry, responses, opts, pendingKey, clearApprovalOnNextStream, ct);
                clearApprovalOnNextStream = false;
                if (step.Stop) return;
                inputItems = step.NextInputItems;
                firstPreviousResponseId = null;
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream failed");
            await WriteSseAsync("error", ex.Message, ct);
        }
    }

    private sealed record StreamStep(bool Stop, IReadOnlyList<ResponseItem>? NextInputItems = null);

    private static CreateResponseOptions BuildResponseOptions(
        string conversationId,
        string pendingKey,
        IReadOnlyList<ResponseItem>? inputItems,
        string? previousResponseIdOverride = null)
    {
        var opts = new CreateResponseOptions { StreamingEnabled = true };
        var previousResponseId = previousResponseIdOverride
                                 ?? (CurrentResponseIds.TryGetValue(pendingKey, out var current) ? current : null);
        if (!string.IsNullOrEmpty(previousResponseId))
        {
            opts.PreviousResponseId = previousResponseId;
        }
        else
        {
            opts.ConversationOptions = new ResponseConversationOptions(conversationId);
        }

        if (inputItems is not null)
        {
            foreach (var item in inputItems)
                opts.InputItems.Add(item);
        }

        return opts;
    }

    private async Task<StreamStep> StreamFoundryOnceAsync(
        FoundryClient foundry,
        ResponsesClient responses,
        CreateResponseOptions opts,
        string pendingKey,
        bool clearsPendingApproval,
        CancellationToken ct)
    {
        var seenIds = new HashSet<string>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var responseIdForResume = opts.PreviousResponseId;
        await foreach (var update in responses.CreateResponseStreamingAsync(opts, ct))
        {
            switch (update)
            {
                case StreamingResponseCreatedUpdate created:
                    responseIdForResume = created.Response?.Id ?? responseIdForResume;
                    if (!string.IsNullOrEmpty(created.Response?.Id))
                    {
                        CurrentResponseIds[pendingKey] = created.Response.Id;
                    }
                    if (clearsPendingApproval)
                    {
                        PendingApprovals.TryRemove(pendingKey, out _);
                        clearsPendingApproval = false;
                    }
                    break;

                case StreamingResponseOutputTextDeltaUpdate d when !string.IsNullOrEmpty(d.Delta):
                    await WriteSseAsync("text", d.Delta!, ct);
                    break;

                case StreamingResponseOutputItemDoneUpdate done:
                    var item = done.Item;
                    if (item.Id is { } id && !seenIds.Add(id)) break;
                    if (await HandleItemAsync(foundry, item, pendingKey, responseIdForResume, seenFiles, ct))
                        return new StreamStep(true);
                    break;

                case StreamingResponseCompletedUpdate completed:
                    responseIdForResume = completed.Response?.Id ?? responseIdForResume;
                    if (!string.IsNullOrEmpty(completed.Response?.Id))
                    {
                        CurrentResponseIds[pendingKey] = completed.Response.Id;
                    }
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
                    return new StreamStep(true);

                case StreamingResponseErrorUpdate err:
                    await WriteSseAsync("error", $"{err.Code ?? "error"}: {err.Message ?? "unknown"}", ct);
                    return new StreamStep(true);

                default:
                    if (TryParseApprovalEvent(update, responseIdForResume, out var approvalEvent))
                    {
                        await EmitApprovalAsync(pendingKey, approvalEvent, ct);
                        return new StreamStep(true);
                    }
                    if (TryParseConsentEvent(update, out var serverLabel, out var link))
                    {
                        await WriteSseAsync("consent", JsonSerializer.Serialize(new { serverLabel, consentLink = link }), ct);
                    }
                    break;
            }
        }

        // MCP tools execute server-side within this response. Once Foundry
        // completes the stream, another POST would have no input and fail
        // with missing_required_parameter. Only explicit approval responses
        // start a new request, handled by StreamMessage before this call.
        return new StreamStep(true);
    }

    private async Task<bool> HandleItemAsync(
        FoundryClient foundry,
        ResponseItem item,
        string pendingKey,
        string? responseIdForResume,
        HashSet<string> seenFiles,
        CancellationToken ct)
    {
        switch (item)
        {
            case McpToolCallApprovalRequestItem approval when !string.IsNullOrEmpty(responseIdForResume):
                await EmitApprovalAsync(pendingKey, McpApproval.FromSdkItem(approval, responseIdForResume!), ct);
                return true;

            case McpToolCallItem mcp:
                await WriteSseAsync("tool", JsonSerializer.Serialize(new
                {
                    kind   = "mcp",
                    tool   = mcp.ToolName,
                    server = mcp.ServerLabel,
                    output = Truncate(mcp.ToolOutput ?? mcp.Error?.ToString() ?? "(no output)", 2000)
                }), ct);
                return false;

            case WebSearchCallResponseItem webSearch:
                await WriteSseAsync("tool", JsonSerializer.Serialize(new
                {
                    kind = "web_search",
                    tool = "web_search",
                    args = ExtractWebSearchQuery(webSearch) ?? "(query unavailable)"
                }), ct);
                return false;

            case CodeInterpreterCallResponseItem codeInterpreter:
                var (code, output) = ExtractCodeInterpreterDetails(codeInterpreter);
                await WriteSseAsync("tool", JsonSerializer.Serialize(new
                {
                    kind = "code_interpreter",
                    tool = "code_interpreter",
                    args = code ?? "(code unavailable)",
                    output = output
                }), ct);
                return false;

            case FunctionCallResponseItem fc:
                await WriteSseAsync("tool", JsonSerializer.Serialize(new
                {
                    kind = "function",
                    tool = fc.FunctionName,
                    args = fc.FunctionArguments?.ToString() ?? "{}"
                }), ct);
                return false;

            case MessageResponseItem message:
                foreach (var part in message.Content)
                {
                    foreach (var annotation in part.OutputTextAnnotations ?? [])
                    {
                        if (annotation is not ContainerFileCitationMessageAnnotation file ||
                            !seenFiles.Add($"{file.ContainerId}\n{file.FileId}"))
                        {
                            continue;
                        }

                        try
                        {
                            var generatedFile = await _files.CaptureContainerFileAsync(
                                foundry,
                                file,
                                AgentFileService.PublicBaseUri(HttpContext?.Request),
                                ct);
                            await WriteSseAsync("file", JsonSerializer.Serialize(generatedFile), ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Could not capture generated container file {FileName} ({ContainerId}/{FileId})",
                                file.Filename,
                                file.ContainerId,
                                file.FileId);
                            await WriteSseAsync("file_error", JsonSerializer.Serialize(new
                            {
                                fileName = file.Filename,
                                message = "The generated file could not be downloaded."
                            }), ct);
                        }
                    }
                }
                return false;

            default:
                // Try Foundry-specific shapes via raw JSON.
                if (TryParseApproval(item, responseIdForResume, out var parsedApproval))
                {
                    await EmitApprovalAsync(pendingKey, parsedApproval, ct);
                    return true;
                }
                if (TryParseConsent(item, out var serverLabel, out var link))
                {
                    await WriteSseAsync("consent", JsonSerializer.Serialize(new { serverLabel, consentLink = link }), ct);
                }
                return false;
        }
    }

    private static string? ExtractWebSearchQuery(WebSearchCallResponseItem item)
    {
        using var doc = JsonDocument.Parse(System.ClientModel.Primitives.ModelReaderWriter.Write(item));
        if (!doc.RootElement.TryGetProperty("action", out var action)) return null;
        if (action.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String)
            return query.GetString();
        if (action.TryGetProperty("search_query", out var searchQuery) && searchQuery.ValueKind == JsonValueKind.String)
            return searchQuery.GetString();
        return null;
    }

    private static (string? Code, string? Output) ExtractCodeInterpreterDetails(CodeInterpreterCallResponseItem item)
    {
        using var doc = JsonDocument.Parse(System.ClientModel.Primitives.ModelReaderWriter.Write(item));
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
            ? codeElement.GetString()
            : null;
        var output = root.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array
            ? Truncate(outputs.GetRawText(), 2000)
            : null;
        return (code, output);
    }

    public static string PendingKey(string agentKey, string conversationId) => $"{agentKey}\n{conversationId}";

    public static CreateResponseOptions BuildApprovalResumeOptions(
        string conversationId, string previousResponseId, string approvalRequestId, bool approve)
        => McpApproval.BuildResumeOptions(conversationId, previousResponseId, approvalRequestId, approve);

    public static string SerializeApprovalEventPayload(PendingMcpApproval approval)
        => JsonSerializer.Serialize(new
        {
            approval_request_id = approval.ApprovalRequestId,
            server_label = approval.ServerLabel,
            tool_name = approval.ToolName,
            arguments_summary = approval.ArgumentsSummary
        });

    private async Task EmitApprovalAsync(string pendingKey, PendingMcpApproval approval, CancellationToken ct)
    {
        PendingApprovals[pendingKey] = approval;
        await WriteSseAsync("approval", SerializeApprovalEventPayload(approval), ct);
    }

    private bool TryParseApproval(ResponseItem item, string? responseIdForResume, out PendingMcpApproval approval)
    {
        approval = null!;
        if (string.IsNullOrEmpty(responseIdForResume)) return false;
        try
        {
            var bd = System.ClientModel.Primitives.ModelReaderWriter.Write(item);
            using var doc = JsonDocument.Parse(bd);
            return McpApproval.TryParseJson(doc.RootElement, responseIdForResume!, out approval);
        }
        catch { return false; }
    }

    private bool TryParseApprovalEvent(StreamingResponseUpdate update, string? responseIdForResume, out PendingMcpApproval approval)
    {
        approval = null!;
        if (string.IsNullOrEmpty(responseIdForResume)) return false;
        try
        {
            var bd = System.ClientModel.Primitives.ModelReaderWriter.Write(update);
            using var doc = JsonDocument.Parse(bd);
            return McpApproval.TryParseJson(doc.RootElement, responseIdForResume!, out approval);
        }
        catch { return false; }
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

    private sealed record FoundryUserContext(string ObjectId, string Token);

    private async Task<FoundryUserContext?> GetFoundryUserContextAsync()
    {
        if (!_adminChatAuth.Enabled)
            return null;
        if (_tokenAcquisition is null)
            throw new InvalidOperationException("Admin chat authentication is enabled but token acquisition is not configured.");

        var objectId = User.FindFirst("oid")?.Value
            ?? User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(objectId))
            throw new InvalidOperationException("Signed-in user object id claim is missing.");

        var token = await _tokenAcquisition.GetAccessTokenForUserAsync(
            new[] { AdminChatAuthOptions.FoundryScope },
            user: User);
        return string.IsNullOrEmpty(token) ? null : new FoundryUserContext(objectId, token);
    }

    private static IDisposable BeginFoundryUserAuthScope(string? token)
        => string.IsNullOrEmpty(token) ? NoopDisposable.Instance : FoundryUserAuthScope.Use(token);

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    private static bool TryProjectEndpoint(string? foundryHost, string? project, out string? projectEndpoint, out string error)
    {
        var hasHost = !string.IsNullOrWhiteSpace(foundryHost);
        var hasProject = !string.IsNullOrWhiteSpace(project);

        projectEndpoint = null;
        error = "";

        if (!hasHost && !hasProject) return true;
        if (!hasHost || !hasProject)
        {
            error = "foundryHost and project must be provided together";
            return false;
        }

        projectEndpoint = FoundryAgentsApi.ComposeProjectEndpoint(foundryHost!.Trim(), project!.Trim());
        return true;
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

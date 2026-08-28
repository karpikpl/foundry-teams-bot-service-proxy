using System.Security.Claims;
using System.Text;
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
    private readonly IWebHostEnvironment _env;
    private readonly AdminChatAuthOptions _adminChatAuth;
    private readonly ITokenAcquisition? _tokenAcquisition;
    private readonly ChatSessionService _chatSessions;
    private readonly AgentFileService _files;

    public ChatTestController(
        AgentService agents,
        AgentClientCache clientCache,
        AgentFileService files,
        IWebHostEnvironment env,
        ILogger<ChatTestController> logger,
        AdminChatAuthOptions? adminChatAuth = null,
        ITokenAcquisition? tokenAcquisition = null,
        ChatSessionService? chatSessions = null)
    {
        _env              = env;
        _adminChatAuth    = adminChatAuth ?? new AdminChatAuthOptions();
        _tokenAcquisition = tokenAcquisition;
        _files            = files;
        _chatSessions     = chatSessions ?? new ChatSessionService(
            agents,
            clientCache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChatSessionService>.Instance,
            files);
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
        var conversation = await _chatSessions.CreateConversationAsync(
            body.AgentKey,
            ChatSessionService.AgentLookup.Key,
            projectEndpoint,
            ToChatUser(user),
            ct);
        return conversation is null
            ? NotFound(new { error = $"agent '{body.AgentKey}' not found" })
            : Ok(new CreateConvResponse(
                conversation.ConversationId,
                conversation.AgentName,
                conversation.Endpoint));
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
        var found = await _chatSessions.DeleteConversationAsync(
            agentKey,
            ChatSessionService.AgentLookup.Key,
            conversationId,
            projectEndpoint,
            ToChatUser(user),
            ct);
        return found
            ? NoContent()
            : NotFound(new { error = $"agent '{agentKey}' not found" });
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

        if (string.IsNullOrEmpty(body?.AgentKey)
            || string.IsNullOrEmpty(body.ConversationId)
            || (string.IsNullOrWhiteSpace(body.Message) && body.Approval is null && inputFiles.Count == 0))
        {
            await WriteSseAsync("error", "agentKey, conversationId, and a message, attachment, or approval are required", ct);
            return;
        }

        if (!TryProjectEndpoint(body.FoundryHost, body.Project, out var projectEndpoint, out var projectError))
        {
            await WriteSseAsync("error", projectError, ct);
            return;
        }

        var user = await GetFoundryUserContextAsync();
        await _chatSessions.StreamMessageAsync(
            body.AgentKey,
            ChatSessionService.AgentLookup.Key,
            new ChatSessionService.Message(
                body.ConversationId,
                body.Message,
                body.Approval is null
                    ? null
                    : new ChatSessionService.Approval(body.Approval.RequestId, body.Approval.Approve),
                inputFiles,
                HttpContext?.Request.Host.HasValue == true
                    ? AgentFileService.PublicBaseUri(HttpContext.Request)
                    : null),
            projectEndpoint,
            ToChatUser(user),
            WriteSseAsync,
            ct);
    }

    public static string PendingKey(string agentKey, string conversationId)
        => ChatSessionService.PendingKey(null, agentKey, conversationId);

    public static CreateResponseOptions BuildApprovalResumeOptions(
        string conversationId, string previousResponseId, string approvalRequestId, bool approve)
        => ChatSessionService.BuildApprovalResumeOptions(
            conversationId,
            previousResponseId,
            approvalRequestId,
            approve);

    public static string SerializeApprovalEventPayload(PendingMcpApproval approval)
        => ChatSessionService.SerializeApprovalEventPayload(approval);

    private sealed record FoundryUserContext(string ObjectId, string Token);

    private static ChatSessionService.UserContext? ToChatUser(FoundryUserContext? user)
        => user is null ? null : new ChatSessionService.UserContext(user.ObjectId, user.Token);

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

using AgentChat.Bots;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace AgentChat.Controllers;

/// <summary>
/// Proactive messaging endpoint. POST a JSON body { conversationId, text } and
/// the bot pushes the text to the recorded conversation using
/// BotAdapter.ContinueConversationAsync.
///
/// Requires that the bot has seen at least one activity from that conversation
/// (so we've stored its ConversationReference).
/// </summary>
[ApiController]
[Route("api/notify")]
public class NotifyController : ControllerBase
{
    private readonly ConversationStore _store;
    private readonly IBotFrameworkHttpAdapter _adapter;
    private readonly IConfiguration _config;
    private readonly ILogger<NotifyController> _logger;

    public NotifyController(
        ConversationStore store,
        IBotFrameworkHttpAdapter adapter,
        IConfiguration config,
        ILogger<NotifyController> logger)
    {
        _store    = store;
        _adapter  = adapter;
        _config   = config;
        _logger   = logger;
    }

    public record NotifyRequest(string ConversationId, string Text);

    [HttpPost]
    public async Task<IActionResult> Notify([FromBody] NotifyRequest req, CancellationToken ct)
    {
        var state = await _store.TryGetAsync(req.ConversationId, ct);
        if (state?.ConversationReference is null)
            return NotFound("Conversation unknown — no message has been received yet.");

        var botAppId = _config["MicrosoftAppId"] ?? "";

        await ((BotAdapter)_adapter).ContinueConversationAsync(
            botAppId,
            state.ConversationReference,
            async (turnContext, innerCt) =>
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(req.Text), innerCt);
            },
            ct);

        return Ok(new { sent = true });
    }

    [HttpGet]
    public IActionResult List()
    {
        // Enumeration would require a Cosmos query; not exposed via IStorage.
        return Ok(new { note = "Pass a conversationId from Bot Framework logs / App Insights to POST." });
    }
}

using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using AgentChat.Foundry;
using AgentChat.Services;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Schema;
using Newtonsoft.Json.Linq;
using OpenAI.Responses;

namespace AgentChat.Bots;

/// <summary>
/// Foundry bot wired directly to the per-agent OpenAI Responses endpoint via
/// the standard OpenAI .NET SDK (transport tweaks live in <see cref="Foundry.FoundryClient"/>).
///
/// Features: streaming, MCP approvals (with per-tool "always approve"),
/// function tools, code-interpreter rendering, agent picker, /reset deletes
/// the Foundry conversation, /tokens, /agent info, /usage toggle, URL-routed
/// multi-agent.
/// </summary>
public class FoundryBot : TeamsActivityHandler
{
    private readonly AgentService _agents;
    private readonly ConversationStore _state;
    private readonly IConfiguration _config;
    private readonly IHttpContextAccessor _httpContext;
    private readonly AgentClientCache _clientCache;
    private readonly TeamsSsoService _sso;
    private readonly ILogger<FoundryBot> _logger;

    public FoundryBot(
        AgentService agents,
        ConversationStore state,
        IConfiguration config,
        IHttpContextAccessor httpContext,
        AgentClientCache clientCache,
        TeamsSsoService sso,
        ILogger<FoundryBot> logger)
    {
        _agents      = agents;
        _state       = state;
        _config      = config;
        _httpContext = httpContext;
        _clientCache = clientCache;
        _sso         = sso;
        _logger      = logger;
    }

    // ---------------------------------------------------------------- members added

    protected override async Task OnMembersAddedAsync(
        IList<ChannelAccount> membersAdded,
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        const string welcome =
            "👋 Hi! I'm a Foundry-backed agent.\n\n" +
            "**Commands:** `/agents` pick an agent · `/reset` new conversation · " +
            "`/stop` cancel a run · `/help` this message.";

        foreach (var m in membersAdded)
        {
            if (m.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(welcome, welcome), cancellationToken);
            }
        }
    }

    // ---------------------------------------------------------------- entry

    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken ct)
    {
        if (turnContext.Activity.Value is not null)
        {
            await HandleCardSubmitAsync(turnContext, ct);
            return;
        }

        if (turnContext.Activity.ChannelId == "msteams")
            turnContext.Activity.RemoveRecipientMention();

        var raw = (turnContext.Activity.Text ?? "").Trim();
        if (string.IsNullOrEmpty(raw)) return;

        var convId = turnContext.Activity.Conversation.Id;
        var state  = await _state.GetOrCreateAsync(convId, ct);
        await _state.TouchAsync(convId, turnContext.Activity.GetConversationReference(), ct);

        if (raw.StartsWith("/", StringComparison.Ordinal))
        {
            await HandleCommandAsync(turnContext, state, raw, ct);
            return;
        }

        await RunAgentTurnAsync(turnContext, state, raw, ct);
    }

    // ---------------------------------------------------------------- commands

    private async Task HandleCommandAsync(ITurnContext turnContext, ConversationState state, string raw, CancellationToken ct)
    {
        var parts = raw.Split(' ', 2);
        switch (parts[0].ToLowerInvariant())
        {
            case "/help":
            case "/commands":
                await turnContext.SendActivityAsync(
                    MessageFactory.Attachment(AdaptiveCardBuilder.BuildHelpCard(new[]
                    {
                        ("/agents",        "Pick a Foundry agent"),
                        ("/agent",         "Show active agent + endpoint"),
                        ("/tokens",        "Show token usage for this conversation"),
                        ("/usage on|off",  "Toggle the per-run usage footer"),
                        ("/tools on|off",  "Show or hide tool-call cards (off by default)"),
                        ("/auto list|clear", "Manage auto-approved MCP tools"),
                        ("/reset",         "Start a new conversation (loses memory)"),
                        ("/stop",          "Cancel the running agent turn"),
                        ("/help",          "Show this card")
                    })), ct);
                break;

            case "/reset":
                await ResetConversationAsync(turnContext, state, ct);
                break;

            case "/agents":
            {
                var forceRefresh    = parts.Length > 1 && parts[1].Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase);
                var routing         = TurnRouting.From(_httpContext, _agents);
                var projectEndpoint = ProjectEndpointForTurn(state, routing);
                var catalog         = await _agents.GetDescriptorsAsync(projectEndpoint, forceRefresh: forceRefresh, ct: ct);
                if (catalog.Count == 0)
                {
                    await turnContext.SendActivityAsync(MessageFactory.Text(
                        "No agents are available in this Foundry project. Create one in Foundry first, then `/agents refresh`."), ct);
                    break;
                }
                var currentKey = await _agents.FindKeyForEndpointAsync(state.AgentEndpoint, projectEndpoint, ct);
                await turnContext.SendActivityAsync(
                    MessageFactory.Attachment(AdaptiveCardBuilder.BuildAgentPickerCard(catalog, currentKey)),
                    ct);
                break;
            }

            case "/stop":
                await CancelCurrentRunAsync(turnContext, state, ct);
                break;

            case "/usage":
                await HandleUsageCommandAsync(turnContext, state, parts.Length > 1 ? parts[1].Trim() : "", ct);
                break;

            case "/tools":
                await HandleToolsCommandAsync(turnContext, state, parts.Length > 1 ? parts[1].Trim() : "", ct);
                break;

            case "/auto":
                await HandleAutoCommandAsync(turnContext, state, parts.Length > 1 ? parts[1].Trim() : "", ct);
                break;

            case "/tokens":
                await HandleTokensCommandAsync(turnContext, state, ct);
                break;

            case "/agent":
            case "/info":
                await HandleAgentInfoCommandAsync(turnContext, state, ct);
                break;

            default:
                await turnContext.SendActivityAsync(MessageFactory.Text($"Unknown command `{parts[0]}`. Try `/help`."), ct);
                break;
        }
    }


    private string ProjectEndpointForTurn(ConversationState state, TurnRouting routing)
        => FoundryAgentsApi.ProjectEndpointFor(state.AgentEndpoint)
           ?? routing.ProjectEndpoint
           ?? _agents.DefaultProjectEndpoint;

    private async Task ResetConversationAsync(ITurnContext turnContext, ConversationState state, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(state.ConversationId) && !string.IsNullOrEmpty(state.AgentEndpoint))
        {
            try
            {
                var convClient = _clientCache.For(state.AgentEndpoint!).OpenAI.GetConversationClient();
                await convClient.DeleteConversationAsync(state.ConversationId!, options: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete Foundry conversation {ConvId}", state.ConversationId);
            }
        }
        await _state.ResetAsync(turnContext.Activity.Conversation.Id, ct);
        await turnContext.SendActivityAsync(MessageFactory.Text("🧹 Conversation reset."), ct);
    }

    private async Task HandleUsageCommandAsync(ITurnContext turnContext, ConversationState state, string arg, CancellationToken ct)
    {
        bool? newValue = arg.ToLowerInvariant() switch
        {
            "on" or "1" or "true"  => true,
            "off" or "0" or "false" => false,
            _ => null
        };
        if (newValue is null)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"Usage footer is currently **{(state.ShowUsage ? "on" : "off")}**. Use `/usage on` or `/usage off`."), ct);
            return;
        }
        state.ShowUsage = newValue.Value;
        await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
        await turnContext.SendActivityAsync(MessageFactory.Text($"📊 Usage footer **{(newValue.Value ? "on" : "off")}**."), ct);
    }

    private async Task HandleToolsCommandAsync(ITurnContext turnContext, ConversationState state, string arg, CancellationToken ct)
    {
        bool? newValue = arg.ToLowerInvariant() switch
        {
            "on" or "1" or "true"   => true,
            "off" or "0" or "false" => false,
            _ => null
        };
        if (newValue is null)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"Tool-call cards are currently **{(state.ShowToolCalls ? "on" : "off")}**. Use `/tools on` to show them (handy for troubleshooting) or `/tools off` to hide them."), ct);
            return;
        }
        state.ShowToolCalls = newValue.Value;
        await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
        await turnContext.SendActivityAsync(MessageFactory.Text(
            newValue.Value
                ? "🔧 Tool-call cards **on** — you'll see what the agent called and the raw output."
                : "🔧 Tool-call cards **off** — the agent's text summary is all you'll see."), ct);
    }

    private async Task HandleAutoCommandAsync(ITurnContext turnContext, ConversationState state, string arg, CancellationToken ct)
    {
        if (arg.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            state.AutoApproveMcpTools.Clear();
            await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
            await turnContext.SendActivityAsync(MessageFactory.Text("🧹 Cleared auto-approved tools."), ct);
            return;
        }
        if (state.AutoApproveMcpTools.Count == 0)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text("No tools are auto-approved. Click **🔁 Always approve** on an MCP approval card to add one."), ct);
        }
        else
        {
            var list = string.Join("\n", state.AutoApproveMcpTools.OrderBy(s => s).Select(s => $"• `{s}`  _(server:tool)_"));
            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"**Auto-approved MCP tools** (use `/auto clear` to forget all):\n{list}"), ct);
        }
    }

    private async Task HandleTokensCommandAsync(ITurnContext turnContext, ConversationState state, CancellationToken ct)
    {
        if (state.RunCount == 0)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                "No runs in this conversation yet. Ask the agent something and the counters will start."), ct);
            return;
        }
        var avg = state.TotalTokensTotal / Math.Max(1, state.RunCount);
        var facts = new List<(string, string)>
        {
            ("--- Cumulative", ""),
            ("Runs",              state.RunCount.ToString("N0")),
            ("Prompt tokens",     state.PromptTokensTotal.ToString("N0")),
            ("Completion tokens", state.CompletionTokensTotal.ToString("N0")),
            ("Total tokens",      $"{state.TotalTokensTotal:N0}  (avg {avg:N0}/run)"),
            ("--- Last run", ""),
            ("Prompt",     state.LastPromptTokens.ToString("N0")),
            ("Completion", state.LastCompletionTokens.ToString("N0")),
            ("Total",      state.LastTotalTokens.ToString("N0"))
        };
        if (state.LastRunUtc.HasValue) facts.Add(("At", state.LastRunUtc.Value.ToString("u")));

        await turnContext.SendActivityAsync(
            MessageFactory.Attachment(AdaptiveCardBuilder.BuildInfoCard("Token usage for this conversation", "📊", facts)),
            ct);
    }

    private async Task HandleAgentInfoCommandAsync(ITurnContext turnContext, ConversationState state, CancellationToken ct)
    {
        var routing = TurnRouting.From(_httpContext, _agents);
        var projectEndpoint = ProjectEndpointForTurn(state, routing);
        var endpoint = state.AgentEndpoint ?? (await _agents.DefaultAsync(projectEndpoint, ct)).Endpoint;
        var catalog  = await _agents.GetDescriptorsAsync(projectEndpoint, ct: ct);
        var desc     = catalog.FirstOrDefault(d => string.Equals(d.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
        var facts = new List<(string, string)>
        {
            ("--- Agent", ""),
            ("Name",     desc?.Name ?? "(routed)"),
            ("Key",      desc?.Key ?? "(routed)")
        };
        if (!string.IsNullOrEmpty(desc?.Description)) facts.Add(("About", desc.Description));
        facts.AddRange(new[]
        {
            ("--- Foundry", ""),
            ("Endpoint",        endpoint),
            ("--- Conversation", ""),
            ("Conversation ID", state.ConversationId ?? "(not yet created)"),
            ("Teams convo ID",  turnContext.Activity.Conversation.Id)
        });
        await turnContext.SendActivityAsync(
            MessageFactory.Attachment(AdaptiveCardBuilder.BuildInfoCard("Agent info", "🤖", facts)), ct);
    }

    // ---------------------------------------------------------------- card submit

    private async Task HandleCardSubmitAsync(ITurnContext turnContext, CancellationToken ct)
    {
        var data   = JObject.FromObject(turnContext.Activity.Value!);
        var action = data.Value<string>("action") ?? "";
        var state  = await _state.GetOrCreateAsync(turnContext.Activity.Conversation.Id, ct);
        await _state.TouchAsync(turnContext.Activity.Conversation.Id, turnContext.Activity.GetConversationReference(), ct);

        switch (action)
        {
            case "approve":
            case "deny":
            case "approve_always":
                await HandleApprovalSubmitAsync(turnContext, state, data, ct);
                break;
            case "select_agent":
                await HandleAgentSelectAsync(turnContext, state, data, ct);
                break;
            case "consent_continue":
                await HandleConsentContinueAsync(turnContext, state, ct);
                break;
            case "consent_cancel":
                state.PendingConsentResponseId = null;
                await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
                await turnContext.SendActivityAsync(MessageFactory.Text(
                    "❌ Sign-in cancelled. Type your question again to retry."), ct);
                break;
            case "cancel":
                await CancelCurrentRunAsync(turnContext, state, ct);
                break;
            default:
                _logger.LogWarning("Unknown card action: {Action}", action);
                break;
        }
    }

    /// <summary>
    /// User clicked "I've signed in" on a consent card. Re-stream the previously
    /// paused response by passing <c>previous_response_id</c>; Foundry will
    /// retry the MCP tool call using the now-cached user credential.
    /// </summary>
    private async Task HandleConsentContinueAsync(ITurnContext turnContext, ConversationState state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state.PendingConsentResponseId))
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                "I don't see a pending sign-in for this conversation. Type your question to start over."), ct);
            return;
        }
        if (string.IsNullOrEmpty(state.ConversationId))
        {
            await turnContext.SendActivityAsync(MessageFactory.Text("Conversation state lost — type your question to start over."), ct);
            state.PendingConsentResponseId = null;
            await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
            return;
        }

        var routing  = TurnRouting.From(_httpContext, _agents);
        var foundry  = _clientCache.For(state.AgentEndpoint ?? routing.AgentEndpoint);

        // Honor SSO for the resume call too.
        var auth = await TryAcquireUserAuthAsync(turnContext, state, pendingMessage: null, ct);
        if (!auth.ShouldProceed) return;

        // The streaming loop builds CreateResponseOptions per iteration; we need
        // the first one to include PreviousResponseId. The simplest way is a
        // one-shot override: drain the response with previous_response_id set,
        // then resume the regular loop for any subsequent follow-ups.
        var sw         = Stopwatch.StartNew();
        var streaming  = new StreamingMessageHelper(turnContext);
        await turnContext.SendActivityAsync(new Microsoft.Bot.Schema.Activity { Type = Microsoft.Bot.Schema.ActivityTypes.Typing }, ct);

        using (auth.Scope)
        {
            try
            {
                var resumeOpts = new CreateResponseOptions
                {
                    ConversationOptions = new ResponseConversationOptions(state.ConversationId!),
                    PreviousResponseId  = state.PendingConsentResponseId!,
                    StreamingEnabled    = true,
                };

                // Clear the pending marker BEFORE the call so a failed retry doesn't
                // cause an infinite "I've signed in" loop.
                state.PendingConsentResponseId = null;
                await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);

                var responses = foundry.OpenAI.GetResponsesClient();
                var stop = await ProcessStreamAsync(turnContext, state, foundry, responses, streaming, resumeOpts, sw, ct);
                if (stop) return;

                // If the agent has further tool calls / approvals queued after the
                // resume, drop into the regular loop.
                await StreamResponseLoopAsync(turnContext, state, foundry, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resume-after-consent failed");
                try { await streaming.FinalizeAsync(ct); } catch { /* swallow */ }
                await turnContext.SendActivityAsync(MessageFactory.Text(
                    "⚠️ Couldn't resume after sign-in: " + ex.Message), ct);
            }
        }
    }

    private async Task HandleApprovalSubmitAsync(ITurnContext turnContext, ConversationState state, JObject data, CancellationToken ct)
    {
        var approvalRequestId = data.Value<string>("approvalRequestId") ?? "";
        var conversationId    = data.Value<string>("conversationId")    ?? state.ConversationId ?? "";
        var toolName          = data.Value<string>("toolName")          ?? "";
        var serverLabel       = data.Value<string>("serverLabel")       ?? "";
        var action            = data.Value<string>("action")            ?? "";
        var approve           = action != "deny";

        if (action == "approve_always" && !string.IsNullOrEmpty(toolName))
        {
            state.AutoApproveMcpTools.Add($"{serverLabel}:{toolName}");
            await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"🔁 **{toolName}** on **{serverLabel}** will be auto-approved from now on (clear with `/auto clear`)."), ct);
        }
        else
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(approve ? "✅ Approved." : "❌ Denied."), ct);
        }

        var routing = TurnRouting.From(_httpContext, _agents);
        var endpoint = state.AgentEndpoint ?? (await _agents.DefaultAsync(routing.ProjectEndpoint, ct)).Endpoint;
        var foundry  = _clientCache.For(endpoint);

        // The approval flow is itself a follow-on Foundry call; honor SSO.
        var auth = await TryAcquireUserAuthAsync(turnContext, state, pendingMessage: null, ct);
        if (!auth.ShouldProceed) return;

        using (auth.Scope)
        {
            await PostConversationItemsAsync(foundry, conversationId, new[]
            {
                ResponseItem.CreateMcpApprovalResponseItem(approvalRequestId, approve)
            }, ct);

            await StreamResponseLoopAsync(turnContext, state, foundry, ct);
        }
    }

    private async Task HandleAgentSelectAsync(ITurnContext turnContext, ConversationState state, JObject data, CancellationToken ct)
    {
        var key = data.Value<string>("agentKey");
        if (string.IsNullOrEmpty(key))
        {
            await turnContext.SendActivityAsync(MessageFactory.Text("Pick an agent first."), ct);
            return;
        }
        var routing = TurnRouting.From(_httpContext, _agents);
        var projectEndpoint = ProjectEndpointForTurn(state, routing);
        var agent = await _agents.FindByKeyAsync(key!, projectEndpoint, ct);
        if (agent is null)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"Agent `{key}` is no longer available. Type `/agents` to see the current list."), ct);
            return;
        }
        if (!string.Equals(state.AgentEndpoint, agent.Endpoint, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(state.ConversationId) && !string.IsNullOrEmpty(state.AgentEndpoint))
            {
                try
                {
                    var convClient = _clientCache.For(state.AgentEndpoint!).OpenAI.GetConversationClient();
                    await convClient.DeleteConversationAsync(state.ConversationId!, options: null);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed cleanup of old conv during agent switch"); }
            }
            state.ConversationId = null;
        }
        state.AgentEndpoint = agent.Endpoint;
        await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
        await turnContext.SendActivityAsync(MessageFactory.Text($"✅ Now using **{agent.Name}**."), ct);
    }

    private async Task CancelCurrentRunAsync(ITurnContext turnContext, ConversationState state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state.CurrentResponseId))
        {
            await turnContext.SendActivityAsync(MessageFactory.Text("Nothing is running right now."), ct);
            return;
        }
        state.CurrentResponseId = null;
        await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
        await turnContext.SendActivityAsync(MessageFactory.Text("🛑 Cancellation requested."), ct);
    }

    // ---------------------------------------------------------------- main turn

    /// <summary>
    /// Outcome of acquiring per-turn user auth: either a scope to wrap the
    /// turn in (which may be null when SSO is disabled and we fall back to
    /// UMI), or a signal that we sent a sign-in card and the caller should
    /// stop processing this turn.
    /// </summary>
    private readonly record struct UserAuth(IDisposable? Scope, bool SignInSent)
    {
        public bool ShouldProceed => !SignInSent;
    }

    /// <summary>
    /// Acquire a user-delegated Foundry token via Teams SSO and return a
    /// <see cref="FoundryUserAuthScope"/> to wrap subsequent Foundry calls.
    /// When SSO is disabled, returns a no-op scope so callers fall back to
    /// the UMI/app token (today's behavior). When SSO is enabled but the
    /// user isn't signed in, sends an OAuthCard and signals the caller to
    /// pause; the user's silent Teams SSO (or explicit sign-in) will
    /// re-trigger via the <c>signin/tokenExchange</c> invoke handler.
    /// </summary>
    private async Task<UserAuth> TryAcquireUserAuthAsync(
        ITurnContext turnContext, ConversationState state, string? pendingMessage, CancellationToken ct)
    {
        if (!_sso.Enabled) return new UserAuth(null, false);

        var token = await _sso.TryGetUserTokenAsync(turnContext, ct);
        if (token is not null && !string.IsNullOrEmpty(token.Token))
        {
            return new UserAuth(FoundryUserAuthScope.Use(token.Token), false);
        }

        // No token yet — Teams will normally attempt silent SSO when the
        // OAuthCard is shown with a tokenExchangeResource. The user
        // experience varies: invisible if SSO + consent already granted;
        // an interactive Sign In button otherwise.
        if (!string.IsNullOrEmpty(pendingMessage))
        {
            state.PendingSsoMessage = pendingMessage;
            await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
        }
        await SendSignInCardAsync(turnContext, ct);
        return new UserAuth(null, true);
    }

    /// <summary>
    /// Send an OAuthCard so Teams can attempt silent SSO or surface a sign-in
    /// button. The <c>tokenExchangeResource</c> on the card is what makes
    /// Teams attempt silent SSO instead of opening a browser window.
    /// </summary>
    private async Task SendSignInCardAsync(ITurnContext turnContext, CancellationToken ct)
    {
        var resource = await _sso.GetSignInResourceAsync(turnContext, ct);
        if (resource is null)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                "⚠️ Sign-in is required but the OAuth connection isn't configured properly. Contact your admin."), ct);
            return;
        }

        var oauthCard = new OAuthCard
        {
            Text                   = "Sign in to use the Foundry agent",
            ConnectionName         = _sso.ConnectionName,
            TokenExchangeResource  = resource.TokenExchangeResource,
            Buttons = new List<CardAction>
            {
                new CardAction
                {
                    Title = "Sign in",
                    Type  = ActionTypes.Signin,
                    Value = resource.SignInLink
                }
            }
        };

        await turnContext.SendActivityAsync(
            MessageFactory.Attachment(new Attachment
            {
                ContentType = OAuthCard.ContentType,
                Content     = oauthCard
            }),
            ct);
    }

    private async Task RunAgentTurnAsync(ITurnContext turnContext, ConversationState state, string userText, CancellationToken ct)
    {
        var auth = await TryAcquireUserAuthAsync(turnContext, state, pendingMessage: userText, ct);
        if (!auth.ShouldProceed) return;

        using (auth.Scope)
        {
            await RunAgentTurnInnerAsync(turnContext, state, userText, ct);
        }
    }

    private async Task RunAgentTurnInnerAsync(ITurnContext turnContext, ConversationState state, string userText, CancellationToken ct)
    {
        var routing = TurnRouting.From(_httpContext, _agents);
        var targetEndpoint = routing.IsRouted
            ? routing.AgentEndpoint
            : state.AgentEndpoint ?? (await _agents.DefaultAsync(routing.ProjectEndpoint, ct)).Endpoint;

        // Endpoint switch wipes the bound Foundry conversation.
        if (!string.Equals(state.AgentEndpoint, targetEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(state.ConversationId) && !string.IsNullOrEmpty(state.AgentEndpoint))
            {
                try
                {
                    var oldClient = _clientCache.For(state.AgentEndpoint!).OpenAI.GetConversationClient();
                    await oldClient.DeleteConversationAsync(state.ConversationId!, options: null);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed cleanup of old conv during endpoint switch"); }
            }
            state.AgentEndpoint  = targetEndpoint;
            state.ConversationId = null;
        }

        var foundry = _clientCache.For(targetEndpoint);

        if (string.IsNullOrEmpty(state.ConversationId))
        {
            var convClient = foundry.OpenAI.GetConversationClient();
            // ConversationClient.CreateConversationAsync takes BinaryContent — we
            // can pass an empty object to accept defaults.
            var result = await convClient.CreateConversationAsync(
                BinaryContent.Create(BinaryData.FromString("{}")), options: null);
            var doc = JsonDocument.Parse(result.GetRawResponse().Content.ToString());
            state.ConversationId = doc.RootElement.GetProperty("id").GetString();
            await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
        }

        // Post the user message into the conversation as an explicit item so
        // Foundry traces show user attribution cleanly.
        await PostConversationItemsAsync(foundry, state.ConversationId!, new[]
        {
            ResponseItem.CreateUserMessageItem(userText)
        }, ct);

        await StreamResponseLoopAsync(turnContext, state, foundry, ct);
    }

    // ---------------------------------------------------------------- streaming loop

    /// <summary>
    /// Drives the agent's Responses stream against the bound conversation.
    /// Iterates multiple times when the agent makes function calls or when we
    /// auto-resolve MCP approvals; stops on completion / failure / pause-for-user.
    /// </summary>
    private async Task StreamResponseLoopAsync(
        ITurnContext turnContext,
        ConversationState state,
        Foundry.FoundryClient foundry,
        CancellationToken ct)
    {
        var sw         = Stopwatch.StartNew();
        var streaming  = new StreamingMessageHelper(turnContext);
        await turnContext.SendActivityAsync(new Microsoft.Bot.Schema.Activity { Type = ActivityTypes.Typing }, ct);

        var responses = foundry.OpenAI.GetResponsesClient();

        try
        {
            int safety = 0;
            while (true)
            {
                if (++safety > 8)
                {
                    _logger.LogWarning("Aborting turn after {Hops} tool/approval round-trips.", safety);
                    break;
                }

                var opts = new CreateResponseOptions
                {
                    ConversationOptions = new ResponseConversationOptions(state.ConversationId!),
                    StreamingEnabled    = true,
                };

                var stop = await ProcessStreamAsync(turnContext, state, foundry, responses, streaming, opts, sw, ct);
                if (stop) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream processing failed");
            try { await streaming.FinalizeAsync(ct); } catch { /* swallow */ }
            await turnContext.SendActivityAsync(MessageFactory.Text("⚠️ The agent encountered an error: " + ex.Message), ct);
        }
        finally
        {
            try { await streaming.FinalizeAsync(ct); } catch { /* swallow */ }
        }
    }

    /// <summary>Pending OAuth consent request surfaced by Foundry's MCP passthrough.</summary>
    private sealed record PendingConsent(string Id, string ServerLabel, string ConsentLink);

    /// <returns>True when the loop should stop (success, error, or paused for user).</returns>
    private async Task<bool> ProcessStreamAsync(
        ITurnContext turnContext,
        ConversationState state,
        Foundry.FoundryClient foundry,
        ResponsesClient responses,
        StreamingMessageHelper streaming,
        CreateResponseOptions opts,
        Stopwatch sw,
        CancellationToken ct)
    {
        var pendingFunctionCalls = new List<FunctionCallResponseItem>();
        var pendingApprovals     = new List<McpToolCallApprovalRequestItem>();
        var pendingConsents      = new List<PendingConsent>();
        var seenIds              = new HashSet<string>();
        bool hadError = false;

        await foreach (var update in responses.CreateResponseStreamingAsync(opts, ct))
        {
            switch (update)
            {
                case StreamingResponseCreatedUpdate created:
                    state.CurrentResponseId = created.Response?.Id;
                    break;

                case StreamingResponseOutputTextDeltaUpdate delta when !string.IsNullOrEmpty(delta.Delta):
                    streaming.AppendDelta(delta.Delta!);
                    await streaming.MaybeFlushAsync(ct);
                    break;

                case StreamingResponseOutputItemDoneUpdate done:
                    await HandleCompletedItemAsync(
                        turnContext, state, streaming, done.Item,
                        pendingFunctionCalls, pendingApprovals, pendingConsents, seenIds, ct);
                    break;

                case StreamingResponseCompletedUpdate completed:
                    if (completed.Response is { } resp)
                    {
                        state.CurrentResponseId = resp.Id;
                        if (resp.Usage is { } u)
                        {
                            state.LastPromptTokens      = u.InputTokenCount;
                            state.LastCompletionTokens  = u.OutputTokenCount;
                            state.LastTotalTokens       = u.TotalTokenCount;
                            state.PromptTokensTotal     += u.InputTokenCount;
                            state.CompletionTokensTotal += u.OutputTokenCount;
                            state.TotalTokensTotal      += u.TotalTokenCount;
                        }
                    }
                    state.RunCount++;
                    state.LastRunUtc = DateTime.UtcNow;
                    state.CurrentResponseId = null;
                    await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);
                    break;

                case StreamingResponseFailedUpdate failed:
                    hadError = true;
                    await streaming.FinalizeAsync(ct);
                    await turnContext.SendActivityAsync(MessageFactory.Text(
                        "⚠️ Run failed: " + (failed.Response?.Error?.Message ?? "unknown error")), ct);
                    break;

                case StreamingResponseErrorUpdate err:
                    hadError = true;
                    await streaming.FinalizeAsync(ct);
                    await turnContext.SendActivityAsync(MessageFactory.Text(
                        $"⚠️ {err.Code ?? "error"}: {err.Message ?? "unknown"}"), ct);
                    break;

                default:
                    // Unknown / new event type the OpenAI SDK doesn't model yet.
                    // Log the raw payload so we can spot Foundry adding new event
                    // types we should handle (e.g. azure_ai_search, bing_grounding).
                    LogUnknownStreamEvent(update);
                    break;
            }
        }

        if (hadError) return true;

        // 1) Function calls — dispatch and post outputs; continue loop.
        if (pendingFunctionCalls.Count > 0)
        {
            await streaming.FinalizeAsync(ct);
            var outputs = new List<ResponseItem>();
            foreach (var fc in pendingFunctionCalls)
            {
                var argsStr = fc.FunctionArguments?.ToString() ?? "{}";
                var result  = await FunctionToolDispatcher.ExecuteAsync(fc.FunctionName, argsStr, ct);

                if (state.ShowToolCalls)
                {
                    // Same Bot Connector size limit applies to function-tool cards.
                    const int previewMax = 800;
                    var preview = result.Length > previewMax
                        ? result.Substring(0, previewMax) + $"\n\n…(+{result.Length - previewMax} chars)"
                        : result;
                    await turnContext.SendActivityAsync(
                        MessageFactory.Attachment(AdaptiveCardBuilder.BuildToolCallCard(
                            toolName: fc.FunctionName, serverLabel: "function", arguments: argsStr,
                            output: preview, toolKind: "Function")), ct);
                }

                // The full untruncated result still goes to the agent.
                outputs.Add(ResponseItem.CreateFunctionCallOutputItem(fc.CallId, result));
            }
            await PostConversationItemsAsync(foundry, state.ConversationId!, outputs, ct);
            return false;
        }

        // 2) MCP approvals — auto-approve where the user said "always",
        //    show cards for the rest and pause until the card submit handler resumes us.
        if (pendingApprovals.Count > 0)
        {
            await streaming.FinalizeAsync(ct);
            var autoItems = new List<ResponseItem>();
            var needsUser = new List<McpToolCallApprovalRequestItem>();
            foreach (var req in pendingApprovals)
            {
                if (state.AutoApproveMcpTools.Contains($"{req.ServerLabel}:{req.ToolName}"))
                    autoItems.Add(ResponseItem.CreateMcpApprovalResponseItem(req.Id, true));
                else
                    needsUser.Add(req);
            }

            if (autoItems.Count > 0 && needsUser.Count == 0)
            {
                var names = string.Join(", ", pendingApprovals.Select(r => $"`{r.ToolName}`"));
                await turnContext.SendActivityAsync(MessageFactory.Text($"🔁 Auto-approved {names} ✓"), ct);
                await PostConversationItemsAsync(foundry, state.ConversationId!, autoItems, ct);
                return false;
            }

            if (autoItems.Count > 0)
                await PostConversationItemsAsync(foundry, state.ConversationId!, autoItems, ct);

            foreach (var req in needsUser)
            {
                var argsStr = req.ToolArguments?.ToString() ?? "{}";
                await turnContext.SendActivityAsync(
                    MessageFactory.Attachment(AdaptiveCardBuilder.BuildApprovalCard(
                        toolName: req.ToolName, serverLabel: req.ServerLabel, arguments: argsStr,
                        approvalRequestId: req.Id, conversationId: state.ConversationId!)),
                    ct);
            }
            return true; // pause for user
        }

        // 2b) OAuth consent — Foundry's MCP identity passthrough wants the user to
        //     sign in to the MCP server. Show a card with the consent link;
        //     resume via card submit (consent_continue), which retries this
        //     same response with previous_response_id.
        if (pendingConsents.Count > 0)
        {
            await streaming.FinalizeAsync(ct);
            state.PendingConsentResponseId = state.CurrentResponseId;
            await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, ct);

            foreach (var c in pendingConsents)
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Attachment(AdaptiveCardBuilder.BuildConsentCard(
                        serverLabel: c.ServerLabel, consentLink: c.ConsentLink, conversationId: state.ConversationId!)),
                    ct);
            }
            return true; // pause for user
        }

        // 3) Done — emit usage card if enabled.
        await streaming.FinalizeAsync(ct);
        if (state.ShowUsage && state.LastTotalTokens > 0)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Attachment(AdaptiveCardBuilder.BuildUsageCard(
                    (int?)state.LastPromptTokens,
                    (int?)state.LastCompletionTokens,
                    (int?)state.LastTotalTokens,
                    sw.Elapsed)),
                ct);
        }
        return true;
    }

    private async Task HandleCompletedItemAsync(
        ITurnContext turnContext,
        ConversationState state,
        StreamingMessageHelper streaming,
        ResponseItem item,
        List<FunctionCallResponseItem> pendingFunctionCalls,
        List<McpToolCallApprovalRequestItem> pendingApprovals,
        List<PendingConsent> pendingConsents,
        HashSet<string> seenIds,
        CancellationToken ct)
    {
        if (item.Id is { } id && !seenIds.Add(id)) return; // de-dup repeated done events for the same item

        switch (item)
        {
            case FunctionCallResponseItem fc:
                pendingFunctionCalls.Add(fc);
                break;

            case McpToolCallApprovalRequestItem appr:
                pendingApprovals.Add(appr);
                break;

            case McpToolCallItem mcp:
                if (!state.ShowToolCalls) break;
                await streaming.FinalizeAsync(ct);
                var argsStr = mcp.ToolArguments?.ToString() ?? "{}";
                var output  = mcp.ToolOutput ?? mcp.Error?.ToString() ?? "(no output)";
                // Bot Connector caps activity payload at ~28 KB for Teams. MCP
                // tool outputs (especially docs search) routinely exceed that.
                // The agent always summarizes the tool output in the next text
                // delta anyway, so we just show a status card with a small preview.
                const int previewMax  = 800;   // visible card preview
                const int fileMaxKB   = 18;    // attach as file only if under this
                var preview = output.Length > previewMax
                    ? output.Substring(0, previewMax)
                    : output;
                var msg = MessageFactory.Attachment(AdaptiveCardBuilder.BuildToolCallCard(
                    toolName: mcp.ToolName, serverLabel: mcp.ServerLabel, arguments: argsStr,
                    output: preview + (output.Length > previewMax ? $"\n\n…(+{output.Length - previewMax} chars; full output omitted to fit Teams limits)" : ""),
                    toolKind: "MCP"));
                if (output.Length > previewMax && output.Length < fileMaxKB * 1024)
                {
                    msg.Attachments.Add(AgentMessageRenderer.TextAsFile($"tool-output-{mcp.Id}.txt", output));
                }
                await turnContext.SendActivityAsync(msg, ct);
                break;

            case CodeInterpreterCallResponseItem ci:
                if (!state.ShowToolCalls) break;
                await streaming.FinalizeAsync(ct);
                // CodeInterpreterCallResponseItem doesn't expose Code directly in
                // OpenAI 2.9; we'd need to inspect ci.Input or similar. Skip for now —
                // the streamed text deltas already include the result narrative.
                break;

            default:
                // Unknown item type — could be (a) a Foundry-specific item kind
                // the OpenAI SDK doesn't model (oauth_consent_request,
                // azure_ai_search_call, …) or (b) something brand new. Parse
                // raw JSON and handle the cases we know about; log the rest.
                if (TryExtractConsentRequest(item, out var consent))
                {
                    pendingConsents.Add(consent);
                }
                else
                {
                    LogUnknownItem(item);
                }
                break;

            // MessageResponseItem and McpToolDefinitionListItem don't need cards;
            // their text is already streamed via output_text deltas.
        }
    }

    /// <summary>
    /// Parse a base ResponseItem the SDK didn't model and recognize the
    /// Foundry-specific <c>oauth_consent_request</c> shape. Returns true and
    /// fills <paramref name="consent"/> on match.
    /// </summary>
    private static bool TryExtractConsentRequest(ResponseItem item, out PendingConsent consent)
    {
        consent = null!;
        try
        {
            var bd = System.ClientModel.Primitives.ModelReaderWriter.Write(item);
            using var doc = System.Text.Json.JsonDocument.Parse(bd);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "oauth_consent_request", StringComparison.OrdinalIgnoreCase))
                return false;

            var link = root.TryGetProperty("consent_link", out var cl) ? cl.GetString() : null;
            if (string.IsNullOrEmpty(link)) return false;

            var id    = root.TryGetProperty("id",           out var i)  ? i.GetString()  : null;
            var label = root.TryGetProperty("server_label", out var sl) ? sl.GetString() : null;

            consent = new PendingConsent(id ?? "?", label ?? "(unknown)", link!);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Serialize an unknown streaming update back to JSON for diagnostics.
    /// Captures both the SDK-recognized fields and anything stashed in
    /// <c>Patch</c> (the SDK's bag of unknown properties).
    /// </summary>
    private void LogUnknownStreamEvent(StreamingResponseUpdate update)
    {
        try
        {
            var raw = System.ClientModel.Primitives.ModelReaderWriter.Write(update).ToString();
            _logger.LogWarning("Unhandled stream event ({Type}): {Raw}",
                update.GetType().Name, Truncate(raw, 2000));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unhandled stream event of type {Type}; could not serialize", update.GetType().Name);
        }
    }

    /// <summary>
    /// Serialize an unknown response item back to JSON for diagnostics.
    /// Captures the typed shape plus any unknown fields stored in <c>Patch</c>.
    /// </summary>
    private void LogUnknownItem(ResponseItem item)
    {
        try
        {
            var raw = System.ClientModel.Primitives.ModelReaderWriter.Write(item).ToString();
            _logger.LogWarning("Unhandled output item ({Type}): {Raw}",
                item.GetType().Name, Truncate(raw, 2000));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unhandled output item of type {Type}; could not serialize", item.GetType().Name);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length > max ? s.Substring(0, max) + "…(+" + (s.Length - max) + "ch)" : s;

    /// <summary>
    /// POST one or more ResponseItem instances to <c>/conversations/{id}/items</c>.
    /// ConversationClient.CreateConversationItemsAsync takes raw BinaryContent;
    /// we serialize each <see cref="ResponseItem"/> using its IJsonModel writer
    /// so all the discriminator + nested-shape work is handled by the SDK.
    /// </summary>
    private static async Task PostConversationItemsAsync(
        Foundry.FoundryClient foundry,
        string conversationId,
        IReadOnlyList<ResponseItem> items,
        CancellationToken ct)
    {
        // Serialize each ResponseItem via IJsonModel<ResponseItem>.Write so the
        // SDK owns the wire shape (call_id, approval_request_id, role, etc.).
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"items\":[");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var bd = System.ClientModel.Primitives.ModelReaderWriter.Write(items[i]);
            sb.Append(bd.ToString());
        }
        sb.Append("]}");

        var convClient = foundry.OpenAI.GetConversationClient();
        await convClient.CreateConversationItemsAsync(
            conversationId,
            BinaryContent.Create(BinaryData.FromString(sb.ToString())),
            include: null,
            options: null);
    }

    // ---------------------------------------------------------------- Teams SSO invoke

    /// <summary>
    /// Called by Bot Framework when an invoke activity with name
    /// <c>signin/verifyState</c> or <c>signin/tokenExchange</c> arrives —
    /// Teams' SSO flow. We just confirm the token is now cached, then replay
    /// the user's pending message (if we stored one) so the original turn
    /// resumes seamlessly.
    /// </summary>
    protected override async Task OnSignInInvokeAsync(ITurnContext<IInvokeActivity> turnContext, CancellationToken cancellationToken)
    {
        var state = await _state.GetOrCreateAsync(turnContext.Activity.Conversation.Id, cancellationToken);
        await _state.TouchAsync(turnContext.Activity.Conversation.Id, turnContext.Activity.GetConversationReference(), cancellationToken);

        // Confirm the token is now cached server-side. If not, the user's
        // sign-in didn't complete successfully.
        var token = await _sso.TryGetUserTokenAsync(turnContext, cancellationToken);
        if (token is null || string.IsNullOrEmpty(token.Token))
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                "⚠️ Sign-in didn't complete. Try again or contact your admin."), cancellationToken);
            return;
        }

        // Replay the user's pending message, if we saved one.
        var pending = state.PendingSsoMessage;
        if (string.IsNullOrEmpty(pending))
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                "✅ Signed in. Send your message to start the conversation."), cancellationToken);
            return;
        }

        state.PendingSsoMessage = null;
        await _state.SaveAsync(turnContext.Activity.Conversation.Id, state, cancellationToken);

        // We're inside an invoke turn — replay the message as if the user just
        // typed it. The TryAcquireUserAuthAsync inside RunAgentTurnAsync will
        // now succeed silently because the token is cached.
        await RunAgentTurnAsync(turnContext, state, pending, cancellationToken);
    }
}

using AgentChat.Bots;
using AgentChat.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Adapters;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;
using ConversationState = AgentChat.Bots.ConversationState;

namespace AgentChat.Tests;

public class FoundryBotTests
{
    [Fact]
    public async Task Message_sends_typing_before_agent_turn()
    {
        var bot = MakeBot();
        var adapter = new TestAdapter();
        var turn = MakeMessageTurn(adapter, "hello");

        await bot.InvokeMessageAsync(turn);

        bot.AgentTurns.Should().ContainSingle().Which.Should().Be("hello");
        adapter.GetNextReply().Type.Should().Be(ActivityTypes.Typing);
        adapter.GetNextReply().AsMessageActivity().Text.Should().Be("agent:hello");
    }

    [Fact]
    public async Task Plain_text_message_with_unknown_value_is_not_swallowed()
    {
        var bot = MakeBot();
        var adapter = new TestAdapter();
        var turn = MakeMessageTurn(adapter, "metadata text", value: JObject.FromObject(new { source = "teams-metadata" }));

        await bot.InvokeMessageAsync(turn);

        bot.AgentTurns.Should().ContainSingle().Which.Should().Be("metadata text");
        adapter.GetNextReply().Type.Should().Be(ActivityTypes.Typing);
        adapter.GetNextReply().AsMessageActivity().Text.Should().Be("agent:metadata text");
    }

    [Fact]
    public async Task Known_card_submit_is_routed_without_agent_turn()
    {
        var bot = MakeBot();
        var adapter = new TestAdapter();
        var turn = MakeMessageTurn(adapter, "cancel text should not run", value: JObject.FromObject(new { action = "cancel" }));

        await bot.InvokeMessageAsync(turn);

        bot.AgentTurns.Should().BeEmpty();
        adapter.GetNextReply().AsMessageActivity().Text.Should().Be("Nothing is running right now.");
    }

    [Fact]
    public async Task Signin_token_exchange_replays_saved_pending_sso_message()
    {
        var sso = new FakeSsoService(token: "foundry-user-token");
        var bot = MakeBot(sso);
        var adapter = new TestAdapter();
        var convId = "conv-sso";
        var state = new ConversationState { PendingSsoMessage = "pending question" };
        await bot.Store.SaveAsync(convId, state);

        var turn = MakeInvokeTurn(adapter, convId, "signin/tokenExchange", JObject.FromObject(new
        {
            id = "exchange-id",
            token = "teams-token",
            connectionName = "foundry-oauth"
        }));

        await bot.InvokeSignInAsync(turn);

        sso.ExchangeCalls.Should().Be(1);
        bot.AgentTurns.Should().ContainSingle().Which.Should().Be("pending question");
        bot.AgentTurnTokens.Should().ContainSingle().Which.Should().Be("foundry-user-token");
        adapter.GetNextReply().Type.Should().Be(ActivityTypes.Typing);
        adapter.GetNextReply().AsMessageActivity().Text.Should().Be("agent:pending question");
        (await bot.Store.GetOrCreateAsync(convId)).PendingSsoMessage.Should().BeNull();
    }

    private static SpyFoundryBot MakeBot(TeamsSsoService? sso = null)
    {
        var agents = TestServices.AgentService(new CatalogHandler("agent-a"));
        var store = new ConversationStore(new MemoryStorage(), NullLogger<ConversationStore>.Instance);
        return new SpyFoundryBot(
            agents,
            store,
            TestServices.Config(),
            new HttpContextAccessor(),
            new AgentClientCache(agents),
            sso ?? new FakeSsoService(token: null),
            NullLogger<FoundryBot>.Instance);
    }

    private static ITurnContext MakeMessageTurn(TestAdapter adapter, string text, object? value = null, string convId = "conv-1")
    {
        var activity = MessageFactory.Text(text);
        activity.ChannelId = "msteams";
        activity.Conversation = new ConversationAccount(id: convId);
        activity.From = new ChannelAccount("user-1", "User");
        activity.Recipient = new ChannelAccount("bot-1", "Bot");
        activity.Value = value;
        return new TurnContext(adapter, activity);
    }

    private static ITurnContext MakeInvokeTurn(TestAdapter adapter, string convId, string name, object value)
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Invoke,
            Name = name,
            ChannelId = "msteams",
            Conversation = new ConversationAccount(id: convId),
            From = new ChannelAccount("user-1", "User"),
            Recipient = new ChannelAccount("bot-1", "Bot"),
            Value = value
        };
        return new TurnContext(adapter, activity);
    }

    private sealed class SpyFoundryBot : FoundryBot
    {
        public ConversationStore Store { get; }
        public List<string> AgentTurns { get; } = new();
        public List<string?> AgentTurnTokens { get; } = new();

        public SpyFoundryBot(
            AgentService agents,
            ConversationStore state,
            IConfiguration config,
            IHttpContextAccessor httpContext,
            AgentClientCache clientCache,
            TeamsSsoService sso,
            ILogger<FoundryBot> logger)
            : base(agents, state, config, httpContext, clientCache, sso, logger)
        {
            Store = state;
        }

        public Task InvokeMessageAsync(ITurnContext turnContext)
            => OnTurnAsync(turnContext, CancellationToken.None);

        public Task InvokeSignInAsync(ITurnContext turnContext)
            => OnTurnAsync(turnContext, CancellationToken.None);

        protected override async Task RunAgentTurnAsync(ITurnContext turnContext, ConversationState state, string userText, CancellationToken ct, string? userTokenOverride = null)
        {
            AgentTurns.Add(userText);
            AgentTurnTokens.Add(userTokenOverride);
            await turnContext.SendActivityAsync(MessageFactory.Text("agent:" + userText), ct);
        }
    }

    private sealed class FakeSsoService : TeamsSsoService
    {
        private readonly string? _token;
        public int ExchangeCalls { get; private set; }

        public FakeSsoService(string? token)
            : base(TestServices.Config(new KeyValuePair<string, string?>("TeamsSso:ConnectionName", "foundry-oauth")), NullLogger<TeamsSsoService>.Instance)
        {
            _token = token;
        }

        public override Task<TokenResponse?> TryGetUserTokenAsync(ITurnContext turnContext, CancellationToken ct = default)
            => Task.FromResult<TokenResponse?>(_token is null ? null : new TokenResponse { Token = _token });

        public override Task<TokenResponse?> ExchangeTokenAsync(ITurnContext turnContext, TokenExchangeRequest request, CancellationToken ct = default)
        {
            ExchangeCalls++;
            return Task.FromResult<TokenResponse?>(_token is null ? null : new TokenResponse { Token = _token });
        }
    }
}

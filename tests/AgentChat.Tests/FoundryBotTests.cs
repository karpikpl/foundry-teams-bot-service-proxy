using AgentChat.Bots;
using AgentChat.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Storage;
using Microsoft.Agents.Builder.Testing;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
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
        adapter.GetNextReply().Text.Should().Be("agent:hello");
    }

    [Fact]
    public async Task Attachment_only_message_is_forwarded_to_agent_turn()
    {
        var inputFile = new AgentInputFile(
            "notes.txt",
            "text/plain",
            BinaryData.FromString("attachment contents"));
        var bot = MakeBot(attachments: new FakeTeamsAttachmentService(inputFile));
        var adapter = new TestAdapter();
        var turn = MakeMessageTurn(adapter, "");
        turn.Activity.Attachments =
        [
            new Attachment
            {
                Name = "notes.txt",
                ContentType = "text/plain",
                ContentUrl = "https://files.example/notes.txt"
            }
        ];

        await bot.InvokeMessageAsync(turn);

        bot.AgentTurns.Should().ContainSingle()
            .Which.Should().Be("Use the attached file(s) to complete this request.");
        bot.AgentTurnFiles.Should().ContainSingle()
            .Which.Should().ContainSingle()
            .Which.FileName.Should().Be("notes.txt");
    }

    [Fact]
    public async Task Teams_attachment_survives_interactive_sso_replay()
    {
        var sso = new ReplaySsoService();
        var catalog = new CatalogHandler("agent-a");
        var agents = TestServices.AgentService(catalog);
        var foundry = new RecordingFoundryHandler();
        foundry.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"conv_replay\"}");
        foundry.EnqueueSse(
            ResponseCreated("resp_replay"),
            TextDelta("reviewed"),
            ResponseCompleted("resp_replay"));
        var store = new ConversationStore(new MemoryStorage(), NullLogger<ConversationStore>.Instance);
        var fileService = new AgentFileService(TestServices.Config());
        var inputFile = new AgentInputFile(
            "proposal.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            BinaryData.FromString("document contents"));
        var bot = new ExposedFoundryBot(
            agents,
            store,
            TestServices.Config(),
            new HttpContextAccessor(),
            foundry.ToClientCache(agents),
            sso,
            NullLogger<FoundryBot>.Instance,
            fileService,
            new FakeTeamsAttachmentService(inputFile));
        var adapter = new TestAdapter();
        var convId = "conv-attachment-sso";
        var messageTurn = MakeMessageTurn(adapter, "Review this", convId: convId);
        messageTurn.Activity.Attachments =
        [
            new Attachment
            {
                Name = "proposal.docx",
                ContentType = inputFile.ContentType,
                ContentUrl = "https://files.example/proposal.docx"
            }
        ];

        await bot.InvokeAsync(messageTurn);

        foundry.Requests.Should().BeEmpty();
        (await store.GetOrCreateAsync(convId)).PendingSsoMessage.Should().Be("Review this");

        var invoke = MakeInvokeTurn(adapter, convId, "signin/tokenExchange", JObject.FromObject(new
        {
            id = "exchange-id",
            token = "teams-token",
            connectionName = "foundry-oauth"
        }));
        await bot.InvokeAsync(invoke);

        var request = foundry.Requests.Single(r => r.Method == "POST" && r.Url.Contains("/responses"));
        request.Body.Should().Contain("Review this");
        request.Body.Should().Contain("\"type\":\"input_file\"");
        request.Body.Should().Contain("\"filename\":\"proposal.docx\"");
        foundry.AuthorizationHeaders.Should().OnlyContain(
            header => header != null && header.StartsWith("Bearer ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Teams_attachment_is_embedded_in_responses_input()
    {
        var catalog = new CatalogHandler("agent-a");
        var agents = TestServices.AgentService(catalog);
        var foundry = new RecordingFoundryHandler();
        foundry.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"conv_attachment\"}");
        foundry.EnqueueSse(
            ResponseCreated("resp_attachment"),
            TextDelta("processed"),
            ResponseCompleted("resp_attachment"));
        var store = new ConversationStore(new MemoryStorage(), NullLogger<ConversationStore>.Instance);
        var fileService = new AgentFileService(TestServices.Config());
        var bot = new ExposedFoundryBot(
            agents,
            store,
            TestServices.Config(),
            new HttpContextAccessor(),
            foundry.ToClientCache(agents),
            new FakeSsoService(token: "foundry-user-token", enabled: true),
            NullLogger<FoundryBot>.Instance,
            fileService,
            new FakeTeamsAttachmentService(new AgentInputFile(
                "budget.csv",
                "text/csv",
                BinaryData.FromString("month,total\nJan,42"))));
        var adapter = new TestAdapter();
        var turn = MakeMessageTurn(adapter, "", convId: "conv-teams-file");
        turn.Activity.Attachments =
        [
            new Attachment
            {
                Name = "budget.csv",
                ContentType = "text/csv",
                ContentUrl = "https://files.example/budget.csv"
            }
        ];

        await bot.InvokeAsync(turn);

        var request = foundry.Requests.Single(r => r.Method == "POST" && r.Url.Contains("/responses"));
        request.Body.Should().Contain("\"type\":\"input_file\"");
        request.Body.Should().Contain("\"filename\":\"budget.csv\"");
        request.Body.Should().Contain(Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("month,total\nJan,42")));
    }

    [Fact]
    public async Task Teams_response_sends_generated_container_file_as_attachment()
    {
        var catalog = new CatalogHandler("agent-a");
        var agents = TestServices.AgentService(catalog);
        var foundry = new RecordingFoundryHandler();
        foundry.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"conv_generated\"}");
        foundry.EnqueueSse(
            ResponseCreated("resp_generated"),
            GeneratedFileMessageDone("msg_generated", "cntr_teams", "cfile_teams", "report.xlsx"),
            ResponseCompleted("resp_generated"));
        foundry.EnqueueBinary(System.Text.Encoding.UTF8.GetBytes("xlsx bytes"));
        var store = new ConversationStore(new MemoryStorage(), NullLogger<ConversationStore>.Instance);
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Request.Scheme = "https";
        http.HttpContext.Request.Host = new HostString("proxy.example");
        var bot = new ExposedFoundryBot(
            agents,
            store,
            TestServices.Config(),
            http,
            foundry.ToClientCache(agents),
            new FakeSsoService(token: "foundry-user-token", enabled: true),
            NullLogger<FoundryBot>.Instance,
            new AgentFileService(TestServices.Config()));
        var adapter = new TestAdapter();

        await bot.InvokeAsync(MakeMessageTurn(adapter, "create a spreadsheet", convId: "conv-generated-file"));

        var replies = new List<IActivity>();
        IActivity? reply;
        while ((reply = adapter.GetNextReply()) is not null)
            replies.Add(reply);

        var fileReply = replies.Single(activity =>
            activity.Attachments?.Any(attachment => attachment.Name == "report.xlsx") == true);
        var attachment = fileReply.Attachments!.Single();
        attachment.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        attachment.ContentUrl.Should().StartWith("https://proxy.example/api/files/");
        foundry.Requests.Should().Contain(r =>
            r.Method == "GET" &&
            r.Url.Contains("/containers/cntr_teams/files/cfile_teams/content"));
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
        adapter.GetNextReply().Text.Should().Be("agent:metadata text");
    }

    [Fact]
    public async Task Known_card_submit_is_routed_without_agent_turn()
    {
        var bot = MakeBot();
        var adapter = new TestAdapter();
        var turn = MakeMessageTurn(adapter, "cancel text should not run", value: JObject.FromObject(new { action = "cancel" }));

        await bot.InvokeMessageAsync(turn);

        bot.AgentTurns.Should().BeEmpty();
        adapter.GetNextReply().Text.Should().Be("Nothing is running right now.");
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
        adapter.GetNextReply().Text.Should().Be("agent:pending question");
        (await bot.Store.GetOrCreateAsync(convId)).PendingSsoMessage.Should().BeNull();
    }

    [Fact]
    public async Task OnSignInInvokeAsync_SurfacesError_When_TokenExchangeThrows()
    {
        var sso = new FakeSsoService(token: null, exchangeException: new InvalidOperationException("test failure"));
        var bot = MakeBot(sso);
        var adapter = new TestAdapter();
        var turn = MakeInvokeTurn(adapter, "conv-sso-error", "signin/tokenExchange", JObject.FromObject(new
        {
            id = "exchange-id",
            token = "teams-token",
            connectionName = "foundry-oauth"
        }));

        await bot.InvokeSignInAsync(turn);

        sso.ExchangeCalls.Should().Be(1);
        var errorReply = adapter.GetNextReply().Text;
        errorReply.Should().Contain("Sign-in failed");
        errorReply.Should().Contain("InvalidOperationException");
        errorReply.Should().Contain("test failure");
    }

    [Fact]
    public async Task Invoke_Name_Logging()
    {
        var logger = new ListLogger<FoundryBot>();
        var bot = MakeBot(logger: logger);
        var adapter = new TestAdapter();
        var turn = MakeInvokeTurn(adapter, "conv-log", "signin/tokenExchange", JObject.FromObject(new
        {
            id = "exchange-id",
            token = "teams-token",
            connectionName = "foundry-oauth"
        }));

        await bot.InvokeSignInAsync(turn);

        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Information
            && m.Message.Contains("Invoke received: name=signin/tokenExchange"));
    }

    [Fact]
    public async Task Signin_failure_surfaces_full_body_and_logs_error()
    {
        var logger = new ListLogger<FoundryBot>();
        var bot = MakeBot(logger: logger);
        var adapter = new TestAdapter();
        var value = JObject.FromObject(new
        {
            code = "invokeerror",
            message = "Invoke error occurred",
            details = "full diagnostic body"
        });
        var turn = MakeInvokeTurn(adapter, "conv-signin-failure", "signin/failure", value);

        await bot.InvokeSignInAsync(turn);

        var reply = adapter.GetNextReply().Text;
        reply.Should().Contain("Teams silent SSO failed");
        reply.Should().Contain("```\n{\"code\":\"invokeerror\",\"message\":\"Invoke error occurred\",\"details\":\"full diagnostic body\"}\n```");
        reply.Should().Contain("Common causes");
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Error
            && m.Message.Contains("Teams SSO signin/failure received")
            && m.Message.Contains("full diagnostic body"));
    }

    [Fact]
    public async Task Sign_in_card_logs_token_exchange_resource_details()
    {
        var logger = new ListLogger<FoundryBot>();
        var sso = new FakeSsoService(
            token: null,
            signInResource: new SignInResource
            {
                SignInLink = "https://login.example/signin",
                TokenExchangeResource = new TokenExchangeResource
                {
                    Uri = "api://bot-app/access_as_user",
                    Id = "exchange-id",
                    ProviderId = "aad-v2"
                }
            });
        var catalog = new CatalogHandler("agent-a");
        var agents = TestServices.AgentService(catalog);
        var store = new ConversationStore(new MemoryStorage(), NullLogger<ConversationStore>.Instance);
        var bot = new ExposedFoundryBot(
            agents,
            store,
            TestServices.Config(),
            new HttpContextAccessor(),
            new AgentClientCache(agents),
            sso,
            logger);
        var adapter = new TestAdapter();
        var turn = MakeMessageTurn(adapter, "hello", convId: "conv-signin-card");

        await bot.InvokeAsync(turn);

        adapter.GetNextReply().Type.Should().Be(ActivityTypes.Typing);
        adapter.GetNextReply().Type.Should().Be(ActivityTypes.Message);
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Information
            && m.Message.Contains("Sending OAuthCard for Teams SSO")
            && m.Message.Contains("connection=foundry-oauth")
            && m.Message.Contains("tokenExchangeResourceUri=api://bot-app/access_as_user")
            && m.Message.Contains("tokenExchangeResourceId=exchange-id")
            && m.Message.Contains("tokenExchangeResourceProviderId=aad-v2"));
    }

    [Fact]
    public async Task LoggingMiddleware_logs_incoming_and_outgoing_activity_types()
    {
        var logger = new ListLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(logger);
        var adapter = new TestAdapter();
        var turn = MakeMessageTurn(adapter, "hello", convId: "conv-middleware");

        await middleware.OnTurnAsync(turn, async ct =>
        {
            await turn.SendActivityAsync(MessageFactory.Text("reply"), ct);
        }, CancellationToken.None);

        adapter.GetNextReply().Text.Should().Be("reply");
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Information
            && m.Message.Contains("Incoming activity: type=message"));
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Information
            && m.Message.Contains("Outgoing activity: type=message"));
    }

    [Fact]
    public async Task Token_exchange_invoke_returns_412_when_exchange_fails()
    {
        var sso = new FakeSsoService(token: null, exchangeException: new InvalidOperationException("test failure"));
        var bot = MakeBot(sso);
        var adapter = new TestAdapter();
        var turn = MakeInvokeTurn(adapter, "conv-sso-412", "signin/tokenExchange", JObject.FromObject(new
        {
            id = "exchange-id",
            token = "teams-token",
            connectionName = "foundry-oauth"
        }));

        await bot.InvokeSignInAsync(turn);

        adapter.GetNextReply().Text.Should().Contain("Sign-in failed");
        var invokeResponseActivity = adapter.GetNextReply();
        invokeResponseActivity.Type.Should().Be(ActivityTypes.InvokeResponse);
        var invokeResponse = ((Activity)invokeResponseActivity).Value.Should().BeOfType<InvokeResponse>().Subject;
        invokeResponse.Status.Should().Be(412);
        invokeResponse.Body.Should().BeOfType<TokenExchangeInvokeResponse>()
            .Which.FailureDetail.Should().Contain("test failure");
    }

    [Fact]
    public async Task Signin_verify_state_replays_cached_pending_sso_message()
    {
        var sso = new FakeSsoService(token: "foundry-user-token");
        var bot = MakeBot(sso);
        var adapter = new TestAdapter();
        var convId = "conv-verify-state";
        await bot.Store.SaveAsync(convId, new ConversationState { PendingSsoMessage = "pending verify" });

        var turn = MakeInvokeTurn(adapter, convId, "signin/verifyState", JObject.FromObject(new { state = "123456" }));

        await bot.InvokeSignInAsync(turn);

        sso.ExchangeCalls.Should().Be(0);
        bot.AgentTurns.Should().ContainSingle().Which.Should().Be("pending verify");
        bot.AgentTurnTokens.Should().ContainSingle().Which.Should().Be("foundry-user-token");
        adapter.GetNextReply().Type.Should().Be(ActivityTypes.Typing);
        adapter.GetNextReply().Text.Should().Be("agent:pending verify");
    }

    [Fact]
    public async Task Teams_message_sends_user_visible_error_when_foundry_stream_throws()
    {
        var catalog = new CatalogHandler("agent-a");
        var agents = TestServices.AgentService(catalog);
        var foundry = new RecordingFoundryHandler();
        foundry.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"conv_error\"}");
        foundry.EnqueueJson(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"bad foundry request\"}}");
        var store = new ConversationStore(new MemoryStorage(), NullLogger<ConversationStore>.Instance);
        var bot = new ExposedFoundryBot(
            agents,
            store,
            TestServices.Config(),
            new HttpContextAccessor(),
            foundry.ToClientCache(agents),
            new FakeSsoService(token: "foundry-user-token", enabled: true),
            NullLogger<FoundryBot>.Instance);
        var adapter = new TestAdapter();
        var turn = MakeMessageTurn(adapter, "boom", convId: "conv-teams-error");

        await bot.InvokeAsync(turn);

        adapter.GetNextReply().Type.Should().Be(ActivityTypes.Typing);
        var errorReply = adapter.GetNextReply().Text;
        errorReply.Should().Contain("The agent encountered an error");
        errorReply.Should().Contain("bad foundry request");
    }

    [Fact]
    public async Task Signin_token_exchange_replay_calls_foundry_responses_create()
    {
        var sso = new FakeSsoService(token: "foundry-user-token");
        var catalog = new CatalogHandler("agent-a");
        var agents = TestServices.AgentService(catalog);
        var foundry = new RecordingFoundryHandler();
        foundry.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"conv_foundry\"}");
        foundry.EnqueueSse(
            ResponseCreated("resp_sso"),
            TextDelta("replayed"),
            ResponseCompleted("resp_sso"));
        var store = new ConversationStore(new MemoryStorage(), NullLogger<ConversationStore>.Instance);
        var bot = new ExposedFoundryBot(
            agents,
            store,
            TestServices.Config(),
            new HttpContextAccessor(),
            foundry.ToClientCache(agents),
            sso,
            NullLogger<FoundryBot>.Instance);
        var adapter = new TestAdapter();
        var convId = "conv-sso-foundry";
        await store.SaveAsync(convId, new ConversationState { PendingSsoMessage = "pending question" });

        var turn = MakeInvokeTurn(adapter, convId, "signin/tokenExchange", JObject.FromObject(new
        {
            id = "exchange-id",
            token = "teams-token",
            connectionName = "foundry-oauth"
        }));

        await bot.InvokeAsync(turn);

        foundry.Requests.Should().Contain(r => r.Method == "POST" && r.Url.Contains("/responses"));
        var responsesCreate = foundry.Requests.Single(r => r.Method == "POST" && r.Url.Contains("/responses"));
        responsesCreate.Body.Should().Contain("pending question");
        responsesCreate.Body.Should().Contain("conversation");
        responsesCreate.Body.Should().NotContain("previous_response_id");
        (await store.GetOrCreateAsync(convId)).PendingSsoMessage.Should().BeNull();
        foundry.UserIdentityHeaders.Should().OnlyContain(v => v == null,
            because: "Foundry:SendUserIdentityHeader defaults to false; header must be off out-of-the-box to avoid HTTP 403 UserIdentityImpersonation errors when the calling identity lacks the custom role");
    }

    [Fact]
    public async Task Sends_x_ms_user_identity_header_when_flag_enabled()
    {
        var sso = new FakeSsoService(token: "foundry-user-token");
        var catalog = new CatalogHandler("agent-a");
        var agents = TestServices.AgentService(catalog);
        var foundry = new RecordingFoundryHandler();
        foundry.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"conv_flag\"}");
        foundry.EnqueueSse(
            ResponseCreated("resp_flag"),
            TextDelta("hi"),
            ResponseCompleted("resp_flag"));
        var store = new ConversationStore(new MemoryStorage(), NullLogger<ConversationStore>.Instance);
        var bot = new ExposedFoundryBot(
            agents,
            store,
            TestServices.Config(new KeyValuePair<string, string?>("Foundry:SendUserIdentityHeader", "true")),
            new HttpContextAccessor(),
            foundry.ToClientCache(agents),
            sso,
            NullLogger<FoundryBot>.Instance);
        var adapter = new TestAdapter();
        var convId = "conv-flag-on";
        await store.SaveAsync(convId, new ConversationState { PendingSsoMessage = "hello" });

        var turn = MakeInvokeTurn(adapter, convId, "signin/tokenExchange", JObject.FromObject(new
        {
            id = "exchange-id",
            token = "teams-token",
            connectionName = "foundry-oauth"
        }));

        await bot.InvokeAsync(turn);

        foundry.UserIdentityHeaders.Should().Contain("aad-user-1");
    }

    [Fact]
    public void Welcome_message_uses_agent_name_from_routed_HttpContext_Items()
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext!.Items["AgentName"] = "joe";
        var bot = MakeBot(httpContext: http);

        var text = bot.BuildWelcomeMessage();

        text.Should().Contain("**joe**");
        text.Should().Contain("`/new`");
        text.Should().NotContain("/reset");
        text.Should().NotContain("Foundry-backed agent",
            because: "wording was updated to 'Foundry-hosted' to distinguish from Bot Framework's 'backed by' phrasing");
    }

    [Fact]
    public void Welcome_message_infers_agent_name_from_routed_endpoint_when_AgentName_missing()
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext!.Items[Controllers.BotMessagesController.AgentEndpointKey] =
            "https://foo.services.ai.azure.com/api/projects/p1/agents/researcher/endpoint/protocols/openai/v1";
        var bot = MakeBot(httpContext: http);

        bot.BuildWelcomeMessage().Should().Contain("**researcher**");
    }

    [Fact]
    public void Welcome_message_falls_back_to_generic_intro_when_no_route_context()
    {
        var bot = MakeBot();

        var text = bot.BuildWelcomeMessage();

        text.Should().StartWith("👋 Hi! I'm a Foundry-hosted agent.");
        text.Should().Contain("`/new`");
        text.Should().NotContain("/reset");
    }

    private static string ResponseCreated(string id)
        => $"{{\"type\":\"response.created\",\"response\":{{\"id\":\"{id}\",\"object\":\"response\",\"created_at\":0,\"status\":\"in_progress\",\"output\":[]}}}}";

    private static string TextDelta(string text)
        => $"{{\"type\":\"response.output_text.delta\",\"delta\":\"{text}\",\"output_index\":0,\"content_index\":0,\"item_id\":\"msg_1\"}}";

    private static string GeneratedFileMessageDone(string id, string containerId, string fileId, string fileName)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "response.output_item.done",
            output_index = 0,
            item = new
            {
                id,
                type = "message",
                role = "assistant",
                status = "completed",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = "Download the generated file.",
                        annotations = new[]
                        {
                            new
                            {
                                type = "container_file_citation",
                                container_id = containerId,
                                file_id = fileId,
                                start_index = 0,
                                end_index = 8,
                                filename = fileName
                            }
                        }
                    }
                }
            }
        });

    private static string ResponseCompleted(string id)
        => $"{{\"type\":\"response.completed\",\"response\":{{\"id\":\"{id}\",\"object\":\"response\",\"created_at\":0,\"status\":\"completed\",\"output\":[],\"usage\":{{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}}}}";

    private static SpyFoundryBot MakeBot(
        TeamsSsoService? sso = null,
        ILogger<FoundryBot>? logger = null,
        IHttpContextAccessor? httpContext = null,
        ITeamsAttachmentService? attachments = null)
    {
        var agents = TestServices.AgentService(new CatalogHandler("agent-a"));
        var store = new ConversationStore(new MemoryStorage(), NullLogger<ConversationStore>.Instance);
        return new SpyFoundryBot(
            agents,
            store,
            TestServices.Config(),
            httpContext ?? new HttpContextAccessor(),
            new AgentClientCache(agents),
            sso ?? new FakeSsoService(token: null),
            logger ?? NullLogger<FoundryBot>.Instance,
            new AgentFileService(TestServices.Config()),
            attachments);
    }

    private static ITurnContext MakeMessageTurn(TestAdapter adapter, string text, object? value = null, string convId = "conv-1")
    {
        var activity = MessageFactory.Text(text);
        activity.ChannelId = "msteams";
        activity.Conversation = new ConversationAccount(id: convId);
        activity.From = new ChannelAccount("user-1", "User") { AadObjectId = "aad-user-1" };
        activity.Recipient = new ChannelAccount("bot-1", "Bot");
        activity.Value = value!;
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
            From = new ChannelAccount("user-1", "User") { AadObjectId = "aad-user-1" },
            Recipient = new ChannelAccount("bot-1", "Bot"),
            Value = value
        };
        return new TurnContext(adapter, activity);
    }

    private sealed class ExposedFoundryBot : FoundryBot
    {
        public ExposedFoundryBot(
            AgentService agents,
            ConversationStore state,
            IConfiguration config,
            IHttpContextAccessor httpContext,
            AgentClientCache clientCache,
            TeamsSsoService sso,
            ILogger<FoundryBot> logger,
            AgentFileService? files = null,
            ITeamsAttachmentService? attachments = null)
            : base(agents, state, config, httpContext, clientCache, sso, logger, files, attachments)
        {
        }

        public Task InvokeAsync(ITurnContext turnContext)
            => OnTurnAsync(turnContext, CancellationToken.None);
    }

    private sealed class SpyFoundryBot : FoundryBot
    {
        public ConversationStore Store { get; }
        public List<string> AgentTurns { get; } = new();
        public List<string?> AgentTurnTokens { get; } = new();
        public List<IReadOnlyList<AgentInputFile>> AgentTurnFiles { get; } = new();

        public SpyFoundryBot(
            AgentService agents,
            ConversationStore state,
            IConfiguration config,
            IHttpContextAccessor httpContext,
            AgentClientCache clientCache,
            TeamsSsoService sso,
            ILogger<FoundryBot> logger,
            AgentFileService? files = null,
            ITeamsAttachmentService? attachments = null)
            : base(agents, state, config, httpContext, clientCache, sso, logger, files, attachments)
        {
            Store = state;
        }

        public Task InvokeMessageAsync(ITurnContext turnContext)
            => OnTurnAsync(turnContext, CancellationToken.None);

        public Task InvokeSignInAsync(ITurnContext turnContext)
            => OnTurnAsync(turnContext, CancellationToken.None);

        protected override async Task RunAgentTurnAsync(
            ITurnContext turnContext,
            ConversationState state,
            string userText,
            CancellationToken ct,
            string? userTokenOverride = null,
            IReadOnlyList<AgentInputFile>? inputFiles = null)
        {
            AgentTurns.Add(userText);
            AgentTurnTokens.Add(userTokenOverride);
            AgentTurnFiles.Add(inputFiles ?? []);
            await turnContext.SendActivityAsync(MessageFactory.Text("agent:" + userText), ct);
        }
    }

    private sealed class FakeTeamsAttachmentService(params AgentInputFile[] files) : ITeamsAttachmentService
    {
        public Task<IReadOnlyList<AgentInputFile>> DownloadAsync(
            ITurnContext turnContext,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AgentInputFile>>(files);
    }

    private sealed class FakeSsoService : TeamsSsoService
    {
        private readonly string? _token;
        private readonly Exception? _exchangeException;
        private readonly SignInResource? _signInResource;
        public int ExchangeCalls { get; private set; }

        public FakeSsoService(string? token, bool enabled = true, Exception? exchangeException = null, SignInResource? signInResource = null)
            : base(enabled
                ? TestServices.Config(new KeyValuePair<string, string?>("TeamsSso:ConnectionName", "foundry-oauth"))
                : TestServices.Config(), NullLogger<TeamsSsoService>.Instance)
        {
            _token = token;
            _exchangeException = exchangeException;
            _signInResource = signInResource;
        }

        public override Task<TokenResponse?> TryGetUserTokenAsync(ITurnContext turnContext, CancellationToken ct = default)
            => Task.FromResult<TokenResponse?>(_token is null ? null : new TokenResponse { Token = _token });

        public override Task<SignInResource?> GetSignInResourceAsync(ITurnContext turnContext, CancellationToken ct = default)
            => Task.FromResult(_signInResource);

        public override Task<TokenResponse?> ExchangeTokenAsync(ITurnContext turnContext, TokenExchangeRequest request, CancellationToken ct = default)
        {
            ExchangeCalls++;
            if (_exchangeException is not null)
            {
                return Task.FromException<TokenResponse?>(_exchangeException);
            }
            return Task.FromResult<TokenResponse?>(_token is null ? null : new TokenResponse { Token = _token });
        }
    }

    private sealed class ReplaySsoService : TeamsSsoService
    {
        private bool _signedIn;

        public ReplaySsoService()
            : base(
                TestServices.Config(new KeyValuePair<string, string?>("TeamsSso:ConnectionName", "foundry-oauth")),
                NullLogger<TeamsSsoService>.Instance)
        {
        }

        public override Task<TokenResponse?> TryGetUserTokenAsync(
            ITurnContext turnContext,
            CancellationToken ct = default)
            => Task.FromResult<TokenResponse?>(_signedIn
                ? new TokenResponse { Token = "replayed-foundry-token" }
                : null);

        public override Task<SignInResource?> GetSignInResourceAsync(
            ITurnContext turnContext,
            CancellationToken ct = default)
            => Task.FromResult<SignInResource?>(new SignInResource
            {
                SignInLink = "https://login.example/signin",
                TokenExchangeResource = new TokenExchangeResource
                {
                    Uri = "api://bot-app/access_as_user",
                    Id = "exchange-id",
                    ProviderId = "aad-v2"
                }
            });

        public override Task<TokenResponse?> ExchangeTokenAsync(
            ITurnContext turnContext,
            TokenExchangeRequest request,
            CancellationToken ct = default)
        {
            _signedIn = true;
            return Task.FromResult<TokenResponse?>(new TokenResponse { Token = "replayed-foundry-token" });
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add((logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

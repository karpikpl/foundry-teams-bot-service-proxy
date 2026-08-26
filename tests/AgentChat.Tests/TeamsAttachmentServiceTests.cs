using System.Net;
using System.Security.Claims;
using AgentChat.Services;
using FluentAssertions;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentChat.Tests;

public class TeamsAttachmentServiceTests
{
    [Fact]
    public async Task DownloadUrl_does_not_receive_bot_framework_bearer_token()
    {
        var handler = new RecordingAttachmentHandler();
        var service = CreateService(handler);
        var activity = CreateActivity(new Attachment
        {
            Name = "notes.txt",
            ContentType = "application/vnd.microsoft.teams.file.download.info",
            Content = new Dictionary<string, object>
            {
                ["downloadUrl"] = "https://tenant.sharepoint.com/download/notes.txt"
            }
        });

        await service.DownloadAsync(CreateTurn(activity), CancellationToken.None);

        handler.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task ConnectorContentUrl_receives_bot_framework_bearer_token()
    {
        var handler = new RecordingAttachmentHandler();
        var service = CreateService(handler);
        var activity = CreateActivity(new Attachment
        {
            Name = "notes.txt",
            ContentType = "text/plain",
            ContentUrl = "https://smba.trafficmanager.net/amer/v3/attachments/file/views/original"
        });

        await service.DownloadAsync(CreateTurn(activity), CancellationToken.None);

        handler.Authorization.Should().Be("Bearer bot-token");
    }

    [Fact]
    public async Task ConnectorContentUrl_rejects_non_connector_host_before_sending_token()
    {
        var handler = new RecordingAttachmentHandler();
        var service = CreateService(handler);
        var activity = CreateActivity(new Attachment
        {
            Name = "notes.txt",
            ContentType = "text/plain",
            ContentUrl = "https://tenant.sharepoint.com/download/notes.txt"
        });

        var act = () => service.DownloadAsync(CreateTurn(activity), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*host is not allowed*");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Redirect_revalidates_host_and_does_not_forward_bearer_token()
    {
        var handler = new RecordingAttachmentHandler(
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://tenant.sharepoint.com/download/notes.txt") }
            },
            SuccessResponse());
        var service = CreateService(handler);
        var activity = CreateActivity(new Attachment
        {
            Name = "notes.txt",
            ContentType = "text/plain",
            ContentUrl = "https://smba.trafficmanager.net/amer/v3/attachments/file/views/original"
        });

        await service.DownloadAsync(CreateTurn(activity), CancellationToken.None);

        handler.Authorizations.Should().Equal("Bearer bot-token", null);
    }

    [Fact]
    public async Task Redirect_rejects_disallowed_host()
    {
        var handler = new RecordingAttachmentHandler(
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://169.254.169.254/metadata") }
            });
        var service = CreateService(handler);
        var activity = CreateActivity(new Attachment
        {
            Name = "notes.txt",
            ContentType = "text/plain",
            ContentUrl = "https://smba.trafficmanager.net/amer/v3/attachments/file/views/original"
        });

        var act = () => service.DownloadAsync(CreateTurn(activity), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*redirect host is not allowed*");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Aggregate_limit_is_enforced_while_downloading_files()
    {
        var handler = new RecordingAttachmentHandler(
            SuccessResponse("123456"),
            SuccessResponse("123456"));
        var service = CreateService(
            handler,
            TestServices.Config(KeyValuePair.Create<string, string?>("Files:MaxRequestBytes", "10")));
        var activity = CreateActivity(
            new Attachment
            {
                Name = "one.txt",
                ContentType = "text/plain",
                ContentUrl = "https://smba.trafficmanager.net/amer/v3/attachments/one/views/original"
            },
            new Attachment
            {
                Name = "two.txt",
                ContentType = "text/plain",
                ContentUrl = "https://smba.trafficmanager.net/amer/v3/attachments/two/views/original"
            });

        var act = () => service.DownloadAsync(CreateTurn(activity), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*combined limit*");
    }

    private static TeamsAttachmentService CreateService(
        HttpMessageHandler handler,
        IConfiguration? configuration = null)
    {
        var provider = new Mock<IAccessTokenProvider>();
        provider.Setup(candidate => candidate.GetAccessTokenAsync(
                It.IsAny<string>(),
                It.IsAny<IList<string>?>(),
                It.IsAny<bool>()))
            .ReturnsAsync("bot-token");
        var connections = new Mock<IConnections>();
        connections.Setup(candidate => candidate.GetTokenProvider(
                It.IsAny<ClaimsIdentity>(),
                It.IsAny<IActivity>()))
            .Returns(provider.Object);

        return new TeamsAttachmentService(
            connections.Object,
            new HandlerHttpClientFactory(handler),
            new AgentFileService(configuration ?? TestServices.Config()),
            configuration ?? TestServices.Config());
    }

    private static Activity CreateActivity(params Attachment[] attachments) => new()
    {
        Type = ActivityTypes.Message,
        ChannelId = Channels.Msteams,
        ServiceUrl = "https://smba.trafficmanager.net/amer/",
        Attachments = attachments
    };

    private static ITurnContext CreateTurn(Activity activity)
    {
        var turn = new Mock<ITurnContext>();
        turn.SetupGet(candidate => candidate.Activity).Returns(activity);
        turn.SetupGet(candidate => candidate.Identity).Returns(new ClaimsIdentity());
        return turn.Object;
    }

    private static HttpResponseMessage SuccessResponse(string content = "attachment")
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content))
        };

    private sealed class RecordingAttachmentHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public RecordingAttachmentHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(
                responses.Length == 0 ? [SuccessResponse()] : responses);
        }

        public string? Authorization { get; private set; }
        public List<string?> Authorizations { get; } = [];
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Authorization = request.Headers.Authorization?.ToString();
            Authorizations.Add(Authorization);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}

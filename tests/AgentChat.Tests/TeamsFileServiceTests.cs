using System.Net;
using AgentChat.Services;
using FluentAssertions;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Extensions.Teams.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentChat.Tests;

public class TeamsFileServiceTests
{
    [Fact]
    public void Native_files_are_limited_to_personal_Teams_conversations()
    {
        var service = CreateService(new RecordingHandler());

        service.SupportsNativeFiles(Activity("msteams", "personal")).Should().BeTrue();
        service.SupportsNativeFiles(Activity("msteams", "groupChat")).Should().BeFalse();
        service.SupportsNativeFiles(Activity("m365copilot", "personal")).Should().BeFalse();
    }

    [Fact]
    public async Task Accepted_file_is_uploaded_and_returned_as_a_native_file_info_card()
    {
        var handler = new RecordingHandler();
        var files = new AgentFileService(TestServices.Config());
        var generated = files.CacheGeneratedFile(
            "report.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "xlsx bytes"u8.ToArray(),
            new Uri("https://proxy.example"));
        var service = CreateService(handler, files);
        var consentAttachment = service.CreateConsentCard(generated);
        consentAttachment.ContentType.Should().Be(FileConsentCard.ContentType);
        consentAttachment.Name.Should().Be("report.xlsx");
        var consent = consentAttachment.Content.Should().BeOfType<FileConsentCard>().Subject;
        consent.SizeInBytes.Should().Be("xlsx bytes"u8.Length);
        var response = new FileConsentCardResponse(
            "accept",
            consent.AcceptContext,
            new FileUploadInfo(
                "report.xlsx",
                "https://sn3302.up.1drv.com/up/session",
                "https://tenant.sharepoint.com/files/report.xlsx",
                "drive-item-id",
                "xlsx"));

        var result = await service.UploadAsync(response, CancellationToken.None);

        handler.Method.Should().Be(HttpMethod.Put);
        handler.Url.Should().Be("https://sn3302.up.1drv.com/up/session");
        handler.Body.Should().Equal("xlsx bytes"u8.ToArray());
        handler.Authorization.Should().BeNull();
        handler.ContentRange.Should().Be("bytes 0-9/10");
        result.Name.Should().Be("report.xlsx");
        result.ContentType.Should().Be(FileInfoCard.ContentType);
        result.ContentUrl.Should().Be("https://tenant.sharepoint.com/files/report.xlsx");
        var card = result.Content.Should().BeOfType<FileInfoCard>().Subject;
        card.UniqueId.Should().Be("drive-item-id");
        card.FileType.Should().Be("xlsx");
        files.TryGetDownload(generated.Token, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Upload_rejects_untrusted_hosts_without_sending_file_bytes()
    {
        var handler = new RecordingHandler();
        var files = new AgentFileService(TestServices.Config());
        var generated = files.CacheGeneratedFile(
            "report.txt",
            "text/plain",
            "secret"u8.ToArray(),
            new Uri("https://proxy.example"));
        var service = CreateService(handler, files);
        var consent = (FileConsentCard)service.CreateConsentCard(generated).Content;
        var response = new FileConsentCardResponse(
            "accept",
            consent.AcceptContext,
            new FileUploadInfo(
                "report.txt",
                "https://169.254.169.254/upload",
                "https://tenant.sharepoint.com/files/report.txt",
                "drive-item-id",
                "txt"));

        var act = () => service.UploadAsync(response, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unsupported file host*");
        handler.RequestCount.Should().Be(0);
        files.TryGetDownload(generated.Token, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Upload_rejects_lookalike_OneDrive_host()
    {
        var handler = new RecordingHandler();
        var files = new AgentFileService(TestServices.Config());
        var generated = files.CacheGeneratedFile(
            "report.txt",
            "text/plain",
            "secret"u8.ToArray(),
            new Uri("https://proxy.example"));
        var service = CreateService(handler, files);
        var consent = (FileConsentCard)service.CreateConsentCard(generated).Content;
        var response = new FileConsentCardResponse(
            "accept",
            consent.AcceptContext,
            new FileUploadInfo(
                "report.txt",
                "https://up.1drv.com.attacker.example/upload",
                "https://tenant.sharepoint.com/files/report.txt",
                "drive-item-id",
                "txt"));

        var act = () => service.UploadAsync(response, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unsupported file host*");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Expired_or_unknown_consent_token_is_rejected_before_upload()
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler);
        var response = new FileConsentCardResponse(
            "accept",
            new { token = "missing" },
            new FileUploadInfo(
                "report.txt",
                "https://tenant.sharepoint.com/upload/session",
                "https://tenant.sharepoint.com/files/report.txt",
                "drive-item-id",
                "txt"));

        var act = () => service.UploadAsync(response, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expired*");
        handler.RequestCount.Should().Be(0);
    }

    private static TeamsFileService CreateService(
        HttpMessageHandler handler,
        AgentFileService? files = null,
        IConfiguration? configuration = null)
    {
        configuration ??= TestServices.Config();
        return new TeamsFileService(
            new HandlerHttpClientFactory(handler),
            files ?? new AgentFileService(configuration),
            configuration);
    }

    private static Activity Activity(string channelId, string conversationType) => new()
    {
        ChannelId = channelId,
        Conversation = new ConversationAccount(conversationType: conversationType)
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? ContentRange { get; private set; }
        public byte[] Body { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            Url = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            ContentRange = request.Content?.Headers.ContentRange?.ToString();
            Body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

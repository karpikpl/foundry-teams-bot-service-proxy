using System.IO.Compression;
using System.Text;
using AgentChat.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentChat.Tests;

public class ManifestControllerTests
{
    private const string BotId = "12345678-1234-1234-1234-123456789012";

    [Fact]
    public async Task Get_project_manifest_returns_bot_id_form()
    {
        var controller = MakeController(new CatalogHandler("agent-one"));

        var result = await controller.ProjectManifestForm("host-a", "proj-a", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.ContentType.Should().StartWith("text/html");
        result.Content.Should().Contain("name=\"BotId\"");
        result.Content.Should().Contain("Azure Portal → your Bot Service resource");
        result.Content.Should().Contain("agent-one");
    }

    [Fact]
    public async Task Post_project_manifest_with_valid_bot_guid_returns_zip_download()
    {
        var controller = MakeController(new CatalogHandler("agent-one"));
        var result = await controller.ProjectManifestDownload("host-a", "proj-a", "agent-one", BotId, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/zip");
        file.FileDownloadName.Should().Be("agent-one.zip");

        var manifest = ReadManifest(file.FileContents);
        manifest["bots"]![0]!["botId"]!.ToString().Should().Be(BotId);
        manifest["name"]!["short"]!.ToString().Should().Be("agent-one");
    }

    [Fact]
    public async Task Post_project_manifest_with_invalid_bot_id_renders_inline_error()
    {
        var controller = MakeController(new CatalogHandler("agent-one"));
        var result = await controller.ProjectManifestDownload("host-a", "proj-a", "agent-one", "not-a-guid", CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
        content.Content.Should().Contain("Bot ID must be a valid GUID");
        content.Content.Should().Contain("not-a-guid");
    }

    [Fact]
    public async Task Post_agent_manifest_route_returns_zip_for_single_agent()
    {
        var controller = MakeController(new CatalogHandler("alpha", "bravo"));

        var result = await controller.AgentManifestDownload("host-a", "proj-a", "bravo", BotId, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.FileDownloadName.Should().Be("bravo.zip");
        var manifest = ReadManifest(file.FileContents);
        manifest["name"]!["short"]!.ToString().Should().Be("bravo");
        manifest["bots"]![0]!["botId"]!.ToString().Should().Be(BotId);
    }

    private static ManifestController MakeController(CatalogHandler handler)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(TestServices.WebRootPath());

        return new ManifestController(
            TestServices.AgentService(handler),
            new HandlerHttpClientFactory(handler),
            TestServices.Config(),
            env.Object,
            NullLogger<ManifestController>.Instance);
    }

    private static JObject ReadManifest(byte[] zipBytes)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("manifest.json");
        entry.Should().NotBeNull();
        using var reader = new StreamReader(entry!.Open());
        return JObject.Parse(reader.ReadToEnd());
    }
}

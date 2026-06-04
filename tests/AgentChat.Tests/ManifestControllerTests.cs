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
    private const string SsoAppId = "00000000-0000-0000-0000-deadbeef0001";
    private const string SsoResource = "api://my-bot-app/access_as_user";

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
    public async Task Form_GET_renders_includeSso_checkbox_checked_when_server_has_sso()
    {
        var controller = MakeController(new CatalogHandler("agent-one"), SsoConfig());

        var result = await controller.ProjectManifestForm("host-a", "proj-a", CancellationToken.None);

        result.Content.Should().Contain("name=\"includeSso\" type=\"checkbox\" value=\"true\" checked");
        result.Content.Should().Contain("Include Teams SSO (webApplicationInfo + validDomains + permissions)");
        result.Content.Should().Contain("Server SSO config detected (AAD app 00000000…0001). Uncheck to generate a plain manifest without SSO.");
    }

    [Fact]
    public async Task Form_GET_renders_includeSso_checkbox_unchecked_when_server_has_no_sso()
    {
        var controller = MakeController(new CatalogHandler("agent-one"));

        var result = await controller.ProjectManifestForm("host-a", "proj-a", CancellationToken.None);

        result.Content.Should().Contain("name=\"includeSso\" type=\"checkbox\" value=\"true\"");
        result.Content.Should().NotContain("name=\"includeSso\" type=\"checkbox\" value=\"true\" checked");
        result.Content.Should().Contain("SSO is not configured on this proxy (TeamsSso__AadAppId not set). Check this box only after configuring the server.");
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

    [Fact]
    public async Task POST_with_includeSso_false_returns_manifest_without_webApplicationInfo()
    {
        var controller = MakeController(new CatalogHandler("agent-one"), SsoConfig());

        var result = await controller.AgentManifestDownload("host-a", "proj-a", "agent-one", BotId, CancellationToken.None, includeSso: false);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        var manifest = ReadManifest(file.FileContents);
        manifest["webApplicationInfo"].Should().BeNull();
        ((JArray)manifest["validDomains"]!).Should().BeEmpty();
        manifest["permissions"].Should().BeNull();
    }

    [Fact]
    public async Task POST_with_includeSso_true_and_no_server_config_returns_form_with_error()
    {
        var controller = MakeController(new CatalogHandler("agent-one"));

        var result = await controller.AgentManifestDownload("host-a", "proj-a", "agent-one", BotId, CancellationToken.None, includeSso: true);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
        content.Content.Should().Contain("SSO requested but server is not configured. Set TeamsSso__AadAppId and TeamsSso__Resource on the proxy, then retry.");
        content.Content.Should().Contain("name=\"includeSso\" type=\"checkbox\" value=\"true\" checked");
    }

    [Fact]
    public async Task POST_with_includeSso_true_and_server_config_returns_manifest_with_full_SSO_block()
    {
        var controller = MakeController(new CatalogHandler("agent-one"), SsoConfig());

        var result = await controller.AgentManifestDownload("host-a", "proj-a", "agent-one", BotId, CancellationToken.None, includeSso: true);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        var manifest = ReadManifest(file.FileContents);
        manifest["webApplicationInfo"]!["id"]!.ToString().Should().Be(SsoAppId);
        manifest["webApplicationInfo"]!["resource"]!.ToString().Should().Be(SsoResource);
        ((JArray)manifest["validDomains"]!).Select(d => d.ToString()).Should().BeEquivalentTo("token.botframework.com", "*.botframework.com");
        ((JArray)manifest["permissions"]!).Select(p => p.ToString()).Should().BeEquivalentTo("identity", "messageTeamMembers");
    }

    [Fact]
    public async Task Programmatic_POST_without_includeSso_uses_server_sso_default_for_back_compat()
    {
        var controller = MakeController(new CatalogHandler("agent-one"), SsoConfig());

        var result = await controller.AgentManifestDownload("host-a", "proj-a", "agent-one", BotId, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        var manifest = ReadManifest(file.FileContents);
        manifest["webApplicationInfo"]!["id"]!.ToString().Should().Be(SsoAppId);
    }

    private static ManifestController MakeController(CatalogHandler handler, params KeyValuePair<string, string?>[] configValues)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(TestServices.WebRootPath());

        return new ManifestController(
            TestServices.AgentService(handler),
            new HandlerHttpClientFactory(handler),
            TestServices.Config(configValues),
            env.Object,
            NullLogger<ManifestController>.Instance);
    }

    private static KeyValuePair<string, string?>[] SsoConfig() =>
    [
        new("TeamsSso:AadAppId", SsoAppId),
        new("TeamsSso:Resource", SsoResource)
    ];

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

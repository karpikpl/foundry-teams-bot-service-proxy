using AgentChat.Bots;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentChat.Tests;

public class ManifestBuilderTests
{
    private const string BotId = "12345678-1234-1234-1234-123456789012";

    [Fact]
    public void Build_throws_when_agent_name_missing()
    {
        var act = () => ManifestBuilder.Build("", "desc", BotId);
        act.Should().Throw<ArgumentException>().WithMessage("*agentName*");
    }

    [Fact]
    public void Build_throws_when_bot_id_missing()
    {
        var act = () => ManifestBuilder.Build("Agent", "desc", "  ");
        act.Should().Throw<ArgumentException>().WithMessage("*botId*");
    }

    [Fact]
    public void Build_produces_v1_17_manifest()
    {
        var m = ManifestBuilder.Build("Agent", "Description", BotId);
        m["$schema"]!.ToString().Should().Contain("v1.17");
        m["manifestVersion"]!.ToString().Should().Be("1.17");
        m["version"]!.ToString().Should().Be("1.0.0");
    }

    [Fact]
    public void Build_includes_botId_in_bots_array()
    {
        var m = ManifestBuilder.Build("Agent", "Desc", BotId);
        var bots = (JArray)m["bots"]!;
        bots.Should().HaveCount(1);
        bots[0]["botId"]!.ToString().Should().Be(BotId);
    }

    [Fact]
    public void Build_includes_all_three_scopes()
    {
        var m = ManifestBuilder.Build("Agent", "Desc", BotId);
        var scopes = ((JArray)m["bots"]![0]!["scopes"]!).Select(s => s.ToString()).ToArray();
        scopes.Should().BeEquivalentTo(new[] { "personal", "team", "groupChat" });
    }

    [Fact]
    public void Build_truncates_long_descriptions()
    {
        var longDesc = new string('x', 200);
        var m = ManifestBuilder.Build("Agent", longDesc, BotId);

        var shortDesc = m["description"]!["short"]!.ToString();
        shortDesc.Length.Should().BeLessOrEqualTo(ManifestBuilder.MaxShortDescChars);
        shortDesc.Should().EndWith("...");
    }

    [Fact]
    public void Build_truncates_long_full_description()
    {
        var longDesc = new string('y', 5000);
        var m = ManifestBuilder.Build("Agent", longDesc, BotId);

        var fullDesc = m["description"]!["full"]!.ToString();
        fullDesc.Length.Should().Be(ManifestBuilder.MaxFullDescChars);
    }

    [Fact]
    public void Build_truncates_long_short_name()
    {
        var longName = new string('A', 50);
        var m = ManifestBuilder.Build(longName, "Desc", BotId);

        m["name"]!["short"]!.ToString().Length.Should().Be(ManifestBuilder.MaxShortNameChars);
    }

    [Fact]
    public void Build_uses_default_short_desc_when_agent_description_empty()
    {
        var m = ManifestBuilder.Build("MyAgent", "", BotId);
        m["description"]!["short"]!.ToString().Should().Contain("MyAgent");
    }

    [Fact]
    public void Build_uses_provided_manifest_id_when_given()
    {
        var id = Guid.NewGuid();
        var m = ManifestBuilder.Build("Agent", "Desc", BotId, manifestId: id);
        m["id"]!.ToString().Should().Be(id.ToString());
    }

    [Fact]
    public void Build_generates_unique_random_id_when_not_provided()
    {
        var m1 = ManifestBuilder.Build("Agent", "Desc", BotId);
        var m2 = ManifestBuilder.Build("Agent", "Desc", BotId);
        m1["id"]!.ToString().Should().NotBe(m2["id"]!.ToString());
        Guid.TryParse(m1["id"]!.ToString(), out _).Should().BeTrue();
    }

    [Fact]
    public void Build_includes_command_list_with_help_and_reset()
    {
        var m = ManifestBuilder.Build("Agent", "Desc", BotId);
        var commands = (JArray)m["bots"]![0]!["commandLists"]![0]!["commands"]!;
        var titles = commands.Select(c => c["title"]!.ToString()).ToArray();
        titles.Should().Contain("/help");
        titles.Should().Contain("/reset");
        titles.Should().Contain("/agents");
        titles.Should().Contain("/agent");
        titles.Should().Contain("/tokens");
    }

    [Fact]
    public void Build_has_developer_block_with_required_urls()
    {
        var m = ManifestBuilder.Build("Agent", "Desc", BotId);
        var dev = m["developer"]!;
        dev["name"].Should().NotBeNull();
        dev["websiteUrl"].Should().NotBeNull();
        dev["privacyUrl"].Should().NotBeNull();
        dev["termsOfUseUrl"].Should().NotBeNull();
    }

    [Fact]
    public void Build_does_not_emit_webApplicationInfo_by_default()
    {
        var m = ManifestBuilder.Build("Agent", "Desc", BotId);
        m["webApplicationInfo"].Should().BeNull();
    }

    [Fact]
    public void Build_emits_webApplicationInfo_when_sso_app_id_provided()
    {
        var m = ManifestBuilder.Build("Agent", "Desc", BotId,
            ssoAadAppId: "00000000-0000-0000-0000-deadbeef0001",
            ssoResource: "api://my-bot-app/access_as_user");
        var wai = m["webApplicationInfo"]!;
        wai["id"]!.ToString().Should().Be("00000000-0000-0000-0000-deadbeef0001");
        wai["resource"]!.ToString().Should().Be("api://my-bot-app/access_as_user");
    }

    [Fact]
    public void Build_omits_webApplicationInfo_resource_when_only_app_id_given()
    {
        var m = ManifestBuilder.Build("Agent", "Desc", BotId, ssoAadAppId: "00000000-0000-0000-0000-deadbeef0001");
        var wai = m["webApplicationInfo"]!;
        wai["id"].Should().NotBeNull();
        wai["resource"].Should().BeNull();
    }

    [Fact]
    public void Build_does_not_emit_botEndpointPath_when_omitted_or_default()
    {
        var m1 = ManifestBuilder.Build("Agent", "Desc", BotId);
        m1["bots"]![0]!["x-foundryBotEndpointPath"].Should().BeNull();

        var m2 = ManifestBuilder.Build("Agent", "Desc", BotId, botEndpointPath: "/api/messages");
        m2["bots"]![0]!["x-foundryBotEndpointPath"].Should().BeNull();
    }

    [Fact]
    public void Build_embeds_url_routed_botEndpointPath_when_provided()
    {
        var m = ManifestBuilder.Build("Agent", "Desc", BotId, botEndpointPath: "/api/messages/aif-x/proj/Agent");
        m["bots"]![0]!["x-foundryBotEndpointPath"]!.ToString()
            .Should().Be("/api/messages/aif-x/proj/Agent");
    }

    [Theory]
    [InlineData("Docs Assistant",       "Docs_Assistant")]
    [InlineData("MCP Learn Agent POC",  "MCP_Learn_Agent_POC")]
    [InlineData("agent/with/slashes",   "agent_with_slashes")]
    [InlineData("___leading___",        "leading")]
    [InlineData("",                     "agent")]
    [InlineData("___",                  "agent")]
    [InlineData("hello!@#$%^&*()",      "hello")]
    [InlineData("foo-bar_baz",          "foo-bar_baz")]
    public void SanitizeForFilename_strips_unsafe_characters(string input, string expected)
    {
        ManifestBuilder.SanitizeForFilename(input).Should().Be(expected);
    }
}

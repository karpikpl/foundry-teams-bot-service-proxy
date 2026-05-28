using AgentChat.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentChat.Tests;

/// <summary>
/// AgentService now holds a list of descriptors (key, name, description,
/// per-agent endpoint URL) instead of provisioning agents itself. These tests
/// exercise URL composition, default descriptor set, and the config-override
/// path that lets the same App Service serve different customers' agent catalogs.
/// </summary>
public class AgentServiceTests
{
    private static AgentService MakeService(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Foundry:ProjectEndpoint"] = "https://aif-x.services.ai.azure.com/api/projects/proj-x"
        };
        if (overrides != null)
            foreach (var kv in overrides) settings[kv.Key] = kv.Value;

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new AgentService(NullLogger<AgentService>.Instance, cfg);
    }

    [Fact]
    public void Constructor_throws_when_project_endpoint_missing()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var act = () => new AgentService(NullLogger<AgentService>.Instance, cfg);
        act.Should().Throw<InvalidOperationException>().WithMessage("*ProjectEndpoint*");
    }

    [Fact]
    public void Default_catalog_has_three_descriptors()
    {
        var svc = MakeService();
        svc.Descriptors.Should().HaveCount(3);
        svc.Descriptors.Select(d => d.Key).Should().BeEquivalentTo(new[] { "docs", "code", "orchestrator" });
    }

    [Fact]
    public void Default_descriptors_compose_per_agent_endpoint_urls_from_project_endpoint()
    {
        var svc = MakeService();
        svc.Descriptors.Should().Contain(d =>
            d.Key == "docs" &&
            d.Endpoint == "https://aif-x.services.ai.azure.com/api/projects/proj-x/agents/docs-assistant/endpoint/protocols/openai/v1");
        svc.Descriptors.Should().Contain(d =>
            d.Key == "code" &&
            d.Endpoint == "https://aif-x.services.ai.azure.com/api/projects/proj-x/agents/code-helper/endpoint/protocols/openai/v1");
    }

    [Fact]
    public void Default_endpoint_is_first_descriptors_endpoint()
    {
        var svc = MakeService();
        svc.DefaultEndpoint.Should().Be(svc.Descriptors[0].Endpoint);
    }

    [Fact]
    public void Config_override_replaces_default_catalog()
    {
        var svc = MakeService(new()
        {
            ["Foundry:Agents:0:Key"]         = "claude",
            ["Foundry:Agents:0:Name"]        = "claude-static",
            ["Foundry:Agents:0:Description"] = "Claude Opus static agent",
            // No Endpoint override — composed from project endpoint + name.
            ["Foundry:Agents:1:Key"]         = "custom",
            ["Foundry:Agents:1:Name"]        = "custom-name",
            ["Foundry:Agents:1:Description"] = "Has explicit endpoint",
            ["Foundry:Agents:1:Endpoint"]    = "https://different.example.com/api/projects/other/agents/custom-name/endpoint/protocols/openai/v1"
        });

        svc.Descriptors.Should().HaveCount(2);
        svc.Descriptors[0].Name.Should().Be("claude-static");
        svc.Descriptors[0].Endpoint.Should().EndWith("/agents/claude-static/endpoint/protocols/openai/v1");
        svc.Descriptors[1].Endpoint.Should().StartWith("https://different.example.com");
    }

    [Fact]
    public void Config_override_uses_key_as_name_when_name_missing()
    {
        var svc = MakeService(new()
        {
            ["Foundry:Agents:0:Key"]         = "only-key",
            ["Foundry:Agents:0:Description"] = "no name configured"
        });

        svc.Descriptors[0].Name.Should().Be("only-key");
        svc.Descriptors[0].Endpoint.Should().EndWith("/agents/only-key/endpoint/protocols/openai/v1");
    }

    [Fact]
    public void GetByKey_returns_matching_descriptor()
    {
        var svc = MakeService();
        svc.GetByKey("code").Name.Should().Be("code-helper");
    }

    [Fact]
    public void GetByKey_falls_back_to_first_when_unknown()
    {
        var svc = MakeService();
        svc.GetByKey("does-not-exist").Should().BeSameAs(svc.Descriptors[0]);
    }

    [Fact]
    public void FindByEndpoint_matches_case_insensitively()
    {
        var svc = MakeService();
        var endpoint = svc.Descriptors[1].Endpoint.ToUpperInvariant();
        svc.FindByEndpoint(endpoint).Should().NotBeNull();
        svc.FindByEndpoint(endpoint)!.Key.Should().Be(svc.Descriptors[1].Key);
    }

    [Fact]
    public void FindKeyForEndpoint_returns_null_for_unknown_or_empty()
    {
        var svc = MakeService();
        svc.FindKeyForEndpoint(null).Should().BeNull();
        svc.FindKeyForEndpoint("").Should().BeNull();
        svc.FindKeyForEndpoint("https://unknown.example.com").Should().BeNull();
    }

    [Fact]
    public void Credential_is_exposed_so_client_cache_can_share_it()
    {
        var svc = MakeService();
        svc.Credential.Should().NotBeNull();
    }

    [Fact]
    public void Descriptors_have_non_empty_names_and_descriptions()
    {
        var svc = MakeService();
        foreach (var d in svc.Descriptors)
        {
            d.Name.Should().NotBeNullOrWhiteSpace();
            d.Description.Should().NotBeNullOrWhiteSpace();
            d.Endpoint.Should().NotBeNullOrWhiteSpace();
        }
    }
}

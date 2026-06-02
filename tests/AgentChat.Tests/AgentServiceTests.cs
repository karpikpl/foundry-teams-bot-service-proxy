using AgentChat.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentChat.Tests;

/// <summary>
/// AgentService is now a dynamic-discovery wrapper — it talks to Foundry's
/// project agents endpoint on demand and caches for a configurable TTL. The
/// hardcoded descriptor catalog is gone.
///
/// These tests verify what can be checked without HTTP: configuration
/// validation, credential exposure, default-endpoint composition. The actual
/// agent-listing logic is tested indirectly via end-to-end smoke against a
/// real Foundry; covering it here would require a substantial HttpClient mock.
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
        var httpFactory = new SimpleHttpClientFactory();
        return new AgentService(NullLogger<AgentService>.Instance, cfg, httpFactory);
    }

    [Fact]
    public void Constructor_throws_when_project_endpoint_missing()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var act = () => new AgentService(NullLogger<AgentService>.Instance, cfg, new SimpleHttpClientFactory());
        act.Should().Throw<InvalidOperationException>().WithMessage("*ProjectEndpoint*");
    }

    [Fact]
    public void ProjectEndpoint_is_exposed_for_per_turn_routing()
    {
        var svc = MakeService();
        svc.ProjectEndpoint.Should().Be("https://aif-x.services.ai.azure.com/api/projects/proj-x");
    }

    [Fact]
    public void ProjectEndpoint_trailing_slash_is_normalized_away()
    {
        var svc = MakeService(new()
        {
            ["Foundry:ProjectEndpoint"] = "https://aif-x.services.ai.azure.com/api/projects/proj-x/"
        });
        svc.ProjectEndpoint.Should().NotEndWith("/");
    }

    [Fact]
    public void DefaultEndpoint_returns_a_per_agent_url_even_before_first_discovery()
    {
        // Before the first /agents call we don't know the catalog yet — but
        // some call sites need a default endpoint string to fall back on
        // (e.g. /agent info before any picker action). The placeholder URL
        // gets replaced by the first discovered agent's endpoint once we
        // refresh.
        var svc = MakeService();
        svc.DefaultEndpoint.Should().StartWith("https://aif-x.services.ai.azure.com/api/projects/proj-x/agents/");
        svc.DefaultEndpoint.Should().Contain("/endpoint/protocols/openai/v1");
    }

    [Fact]
    public void Credential_is_exposed_so_other_services_can_share_it()
    {
        MakeService().Credential.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDescriptorsAsync_returns_empty_when_endpoint_unreachable_and_does_not_throw()
    {
        // The configured endpoint doesn't resolve; we expect graceful degradation:
        // an empty catalog, not an exception bubbling up through /agents.
        var svc = MakeService();
        var result = await svc.GetDescriptorsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FindByKeyAsync_returns_null_when_unknown()
    {
        var svc = MakeService();
        (await svc.FindByKeyAsync("does-not-exist")).Should().BeNull();
    }

    [Fact]
    public async Task FindKeyForEndpointAsync_returns_null_for_null_or_empty()
    {
        var svc = MakeService();
        (await svc.FindKeyForEndpointAsync(null)).Should().BeNull();
        (await svc.FindKeyForEndpointAsync("")).Should().BeNull();
    }

    [Fact]
    public async Task DefaultAsync_throws_when_no_agents_discovered()
    {
        var svc = MakeService();
        var act = async () => await svc.DefaultAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No active agents*");
    }

    // -------------------------- helpers --------------------------

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }
}

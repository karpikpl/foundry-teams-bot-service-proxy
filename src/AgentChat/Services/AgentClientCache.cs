using System.Collections.Concurrent;
using AgentChat.Foundry;

namespace AgentChat.Services;

/// <summary>
/// Per per-agent endpoint URL cache of <see cref="FoundryClient"/> instances.
/// With URL-routed multi-agent, a single App Service may be talking to many
/// agents (across many Foundry projects, even). The credential is shared.
/// </summary>
public class AgentClientCache
{
    private readonly AgentService _agents;
    private readonly ConcurrentDictionary<string, FoundryClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public AgentClientCache(AgentService agents)
    {
        _agents = agents;
    }

    public FoundryClient For(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
            throw new ArgumentException("endpoint is required", nameof(endpoint));

        return _clients.GetOrAdd(endpoint, ep => new FoundryClient(ep, _agents.Credential));
    }

    public FoundryClient Default => For(_agents.DefaultEndpoint);
}

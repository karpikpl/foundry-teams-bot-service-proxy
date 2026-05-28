using AgentChat.Foundry;
using Azure.Core;

namespace AgentChat.Services;

/// <summary>
/// Holds the catalogue of per-agent Foundry endpoints this app knows about,
/// plus a shared TokenCredential. No SDK clients, no per-startup provisioning —
/// agent definitions live in Foundry (provisioned by Terraform or the portal),
/// the app just talks to them via <see cref="FoundryClient"/>.
/// </summary>
public class AgentService
{
    private readonly ILogger<AgentService> _logger;
    private readonly TokenCredential _credential;
    private readonly string _defaultEndpoint;

    /// <summary>
    /// One agent the bot can route to. Identity = per-agent endpoint URL.
    /// </summary>
    /// <param name="Key">Short key used by <c>/agents</c> picker submits.</param>
    /// <param name="Name">Display name (also the Foundry agent name in the URL).</param>
    /// <param name="Description">User-facing one-liner.</param>
    /// <param name="Endpoint">Full per-agent endpoint URL (up to <c>/v1</c>).</param>
    public record AgentDescriptor(string Key, string Name, string Description, string Endpoint);

    public List<AgentDescriptor> Descriptors { get; }
    public TokenCredential Credential => _credential;
    public string DefaultEndpoint => _defaultEndpoint;

    public AgentService(ILogger<AgentService> logger, IConfiguration config)
    {
        _logger = logger;

        var projectEndpoint = config["Foundry:ProjectEndpoint"]
            ?? throw new InvalidOperationException("Foundry:ProjectEndpoint not configured");

        _credential = new Azure.Identity.DefaultAzureCredential(new Azure.Identity.DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = config["AZURE_CLIENT_ID"]
        });

        // Build a per-agent endpoint URL of the form:
        //   {project}/agents/{agentName}/endpoint/protocols/openai/v1
        // Project endpoint examples:
        //   https://aif-x.services.ai.azure.com/api/projects/proj-x
        var projTrimmed = projectEndpoint.TrimEnd('/');
        string AgentUrl(string agentName) =>
            $"{projTrimmed}/agents/{agentName}/endpoint/protocols/openai/v1";

        // Descriptors are static configuration; the agent objects themselves
        // are provisioned in Foundry (via TF). We just list what we know about.
        // The list can be overridden from config to support customer-specific
        // catalogs — see <c>Foundry:Agents</c> section, defaults below.
        Descriptors = new List<AgentDescriptor>();

        var configured = config.GetSection("Foundry:Agents").GetChildren().ToList();
        if (configured.Count > 0)
        {
            foreach (var c in configured)
            {
                var key  = c["Key"]  ?? throw new InvalidOperationException($"Foundry:Agents:{c.Key}:Key missing");
                var name = c["Name"] ?? key;
                var desc = c["Description"] ?? "";
                var url  = c["Endpoint"] ?? AgentUrl(name);
                Descriptors.Add(new AgentDescriptor(key, name, desc, url));
            }
        }
        else
        {
            Descriptors.AddRange(new[]
            {
                new AgentDescriptor("docs",         "docs-assistant", "Searches Microsoft Learn docs (MCP) and answers with citations.", AgentUrl("docs-assistant")),
                new AgentDescriptor("code",         "code-helper",    "Runs code with Code Interpreter and a local time/calc function tool.", AgentUrl("code-helper")),
                new AgentDescriptor("orchestrator", "orchestrator",   "General-purpose assistant; recommend a specialist via /agents.",     AgentUrl("orchestrator"))
            });
        }

        // The default endpoint the bot uses when no agent picker / URL routing
        // is in play. First descriptor wins.
        _defaultEndpoint = Descriptors[0].Endpoint;
    }

    public AgentDescriptor GetByKey(string key)
        => Descriptors.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))
           ?? Descriptors.First();

    public AgentDescriptor? FindByEndpoint(string endpoint)
        => Descriptors.FirstOrDefault(d => string.Equals(d.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));

    public string? FindKeyForEndpoint(string? endpoint)
        => string.IsNullOrEmpty(endpoint) ? null : FindByEndpoint(endpoint!)?.Key;
}

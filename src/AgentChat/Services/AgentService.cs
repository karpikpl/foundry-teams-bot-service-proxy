using System.Collections.Concurrent;
using AgentChat.Foundry;
using Azure.Core;

namespace AgentChat.Services;

/// <summary>
/// Per-request agent catalog provider.
///
/// AgentService no longer owns a single project — it caches a catalog per
/// Foundry project endpoint. Every public method accepts an optional
/// <c>projectEndpoint</c>; when omitted, the configured default
/// (<see cref="DefaultProjectEndpoint"/>) is used. URL-routed turns pass the
/// per-turn project endpoint resolved by
/// <see cref="Bots.TurnRouting.ProjectEndpoint"/>.
///
/// Caches are keyed by project endpoint with a shared TTL configured via
/// <c>Foundry:CatalogCacheSeconds</c> (default 300s).
/// </summary>
public class AgentService
{
    private readonly ILogger<AgentService> _logger;
    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _defaultProjectEndpoint;
    private readonly TimeSpan _cacheTtl;

    public record AgentDescriptor(string Key, string Name, string Description, string Endpoint);

    private sealed class CatalogEntry
    {
        public IReadOnlyList<AgentDescriptor> Descriptors { get; set; } = Array.Empty<AgentDescriptor>();
        public DateTime CachedAtUtc { get; set; } = DateTime.MinValue;
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, CatalogEntry> _byProject =
        new(StringComparer.OrdinalIgnoreCase);

    public TokenCredential Credential        => _credential;
    public string DefaultProjectEndpoint     => _defaultProjectEndpoint;

    public AgentService(ILogger<AgentService> logger, IConfiguration config, IHttpClientFactory httpFactory)
    {
        _logger      = logger;
        _httpFactory = httpFactory;

        var endpoint = config["Foundry:ProjectEndpoint"]
            ?? throw new InvalidOperationException("Foundry:ProjectEndpoint not configured");
        _defaultProjectEndpoint = endpoint.TrimEnd('/');

        var ttlSeconds = config.GetValue("Foundry:CatalogCacheSeconds", 300);
        _cacheTtl = TimeSpan.FromSeconds(Math.Max(0, ttlSeconds));

        _credential = new Azure.Identity.DefaultAzureCredential(new Azure.Identity.DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = config["AZURE_CLIENT_ID"]
        });
    }

    /// <summary>
    /// Resolve the project endpoint to use for a call: explicit argument if
    /// non-empty, else the configured default. Normalized (trailing slash stripped).
    /// </summary>
    public string ResolveProject(string? projectEndpoint)
        => string.IsNullOrEmpty(projectEndpoint)
            ? _defaultProjectEndpoint
            : projectEndpoint!.TrimEnd('/');

    /// <summary>
    /// Default per-agent endpoint, for callers that need a non-null URL before
    /// the catalog is fetched (e.g. <c>/agent</c> info on first turn). Returns
    /// a synthetic <c>/agents/default/...</c> URL — replaced by the real first
    /// agent's endpoint once <see cref="GetDescriptorsAsync"/> populates.
    /// </summary>
    public string DefaultEndpoint
    {
        get
        {
            if (_byProject.TryGetValue(_defaultProjectEndpoint, out var cached)
                && cached.Descriptors.Count > 0)
            {
                return cached.Descriptors[0].Endpoint;
            }
            return FoundryAgentsApi.ComposeAgentEndpoint(_defaultProjectEndpoint, "default");
        }
    }

    /// <summary>
    /// Return the agent catalog for a project. Uses the cached snapshot when
    /// fresh; refreshes on TTL expiry or when <paramref name="forceRefresh"/>
    /// is true. <paramref name="projectEndpoint"/> null = the configured default.
    /// </summary>
    public async Task<IReadOnlyList<AgentDescriptor>> GetDescriptorsAsync(
        string? projectEndpoint = null, bool forceRefresh = false, CancellationToken ct = default)
    {
        var project = ResolveProject(projectEndpoint);
        var entry   = _byProject.GetOrAdd(project, _ => new CatalogEntry());

        if (!forceRefresh
            && entry.Descriptors.Count > 0
            && DateTime.UtcNow - entry.CachedAtUtc < _cacheTtl)
        {
            return entry.Descriptors;
        }

        await entry.Lock.WaitAsync(ct);
        try
        {
            if (!forceRefresh
                && entry.Descriptors.Count > 0
                && DateTime.UtcNow - entry.CachedAtUtc < _cacheTtl)
            {
                return entry.Descriptors;
            }

            var http     = _httpFactory.CreateClient("foundry-agents");
            var agents   = await FoundryAgentsApi.ListAgentsAsync(http, project, _credential, ct);
            var snapshot = agents
                .Where(a => a.IsActive)
                .Select(a => new AgentDescriptor(
                    Key:         KeyFor(a.Name),
                    Name:        a.Name,
                    Description: string.IsNullOrWhiteSpace(a.Description)
                                ? (a.Model is null ? "Foundry agent" : $"Foundry agent ({a.Model})")
                                : a.Description,
                    Endpoint:    FoundryAgentsApi.ComposeAgentEndpoint(project, a.Name)))
                .ToList();

            entry.Descriptors = snapshot;
            entry.CachedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Refreshed agent catalog: {Count} agent(s) from {Endpoint}", snapshot.Count, project);
            return entry.Descriptors;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh agent catalog from {Endpoint}", project);
            // Stale-on-error: a previous snapshot (possibly empty) is better than
            // throwing the user out of a /agents command.
            return entry.Descriptors;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    public async Task<AgentDescriptor?> FindByKeyAsync(
        string key, string? projectEndpoint = null, CancellationToken ct = default)
    {
        var all = await GetDescriptorsAsync(projectEndpoint, ct: ct);
        return all.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AgentDescriptor?> FindByNameAsync(
        string name, string? projectEndpoint = null, CancellationToken ct = default)
    {
        var all = await GetDescriptorsAsync(projectEndpoint, ct: ct);
        return all.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AgentDescriptor> DefaultAsync(
        string? projectEndpoint = null, CancellationToken ct = default)
    {
        var all = await GetDescriptorsAsync(projectEndpoint, ct: ct);
        return all.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No active agents found in project {ResolveProject(projectEndpoint)}. Create one in Foundry first.");
    }

    public async Task<string?> FindKeyForEndpointAsync(
        string? agentEndpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(agentEndpoint)) return null;
        var project = FoundryAgentsApi.ProjectEndpointFor(agentEndpoint) ?? _defaultProjectEndpoint;
        var all = await GetDescriptorsAsync(project, ct: ct);
        return all.FirstOrDefault(d => string.Equals(d.Endpoint, agentEndpoint, StringComparison.OrdinalIgnoreCase))?.Key;
    }

    /// <summary>
    /// Derive a stable, lowercase, URL-safe key from an agent name. The picker
    /// uses this as the submit value; Cosmos stores it.
    /// </summary>
    private static string KeyFor(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var key = new string(chars).Trim('-');
        while (key.Contains("--")) key = key.Replace("--", "-");
        return string.IsNullOrEmpty(key) ? "unknown" : key;
    }
}

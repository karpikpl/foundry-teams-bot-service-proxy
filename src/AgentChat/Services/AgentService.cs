using AgentChat.Foundry;
using Azure.Core;

namespace AgentChat.Services;

/// <summary>
/// Owns the Foundry project endpoint, the shared <see cref="TokenCredential"/>,
/// and a 5-minute cached snapshot of the agents currently exposed by that
/// project. The picker, /agents command, manifest UI, and default-agent
/// selection all read from <see cref="GetDescriptorsAsync"/>.
///
/// Two HTTP-cheap operations gate everything:
///   - <see cref="FoundryAgentsApi.ListAgentsAsync"/> for the project
///   - One token acquisition via the supplied credential
///
/// Backward-compat: the historical <c>Foundry:ProjectEndpoint</c> setting is
/// kept; the agent catalog is no longer configured statically.
/// </summary>
public class AgentService
{
    private readonly ILogger<AgentService> _logger;
    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _projectEndpoint;
    private readonly TimeSpan _cacheTtl;

    /// <summary>
    /// One agent exposed to the bot. Identity = per-agent endpoint URL.
    /// </summary>
    /// <param name="Key">Short key used by /agents picker submits (auto-derived from name).</param>
    /// <param name="Name">Agent name as it lives in Foundry.</param>
    /// <param name="Description">User-facing description (from Foundry metadata).</param>
    /// <param name="Endpoint">Per-agent endpoint URL the bot drives.</param>
    public record AgentDescriptor(string Key, string Name, string Description, string Endpoint);

    // Snapshot cache. Refreshed lazily when expired or on /agents refresh.
    private IReadOnlyList<AgentDescriptor> _cached = Array.Empty<AgentDescriptor>();
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public TokenCredential Credential => _credential;
    public string ProjectEndpoint     => _projectEndpoint;
    public string DefaultEndpoint     => _cached.FirstOrDefault()?.Endpoint
                                        ?? FoundryAgentsApi.ComposeAgentEndpoint(_projectEndpoint, "default");

    public AgentService(ILogger<AgentService> logger, IConfiguration config, IHttpClientFactory httpFactory)
    {
        _logger      = logger;
        _httpFactory = httpFactory;

        var endpoint = config["Foundry:ProjectEndpoint"]
            ?? throw new InvalidOperationException("Foundry:ProjectEndpoint not configured");
        _projectEndpoint = endpoint.TrimEnd('/');

        var ttlSeconds = config.GetValue("Foundry:CatalogCacheSeconds", 300);
        _cacheTtl = TimeSpan.FromSeconds(Math.Max(0, ttlSeconds));

        _credential = new Azure.Identity.DefaultAzureCredential(new Azure.Identity.DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = config["AZURE_CLIENT_ID"]
        });
    }

    /// <summary>
    /// Return the current agent catalog (cached snapshot). Refreshes if the
    /// cache is stale; <paramref name="forceRefresh"/> bypasses the TTL.
    /// </summary>
    public async Task<IReadOnlyList<AgentDescriptor>> GetDescriptorsAsync(
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh
            && _cached.Count > 0
            && DateTime.UtcNow - _cachedAtUtc < _cacheTtl)
        {
            return _cached;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh
                && _cached.Count > 0
                && DateTime.UtcNow - _cachedAtUtc < _cacheTtl)
            {
                return _cached;
            }

            var http     = _httpFactory.CreateClient("foundry-agents");
            var agents   = await FoundryAgentsApi.ListAgentsAsync(http, _projectEndpoint, _credential, ct);
            var snapshot = agents
                .Where(a => a.IsActive)
                .Select(a => new AgentDescriptor(
                    Key:         KeyFor(a.Name),
                    Name:        a.Name,
                    Description: string.IsNullOrWhiteSpace(a.Description)
                                ? (a.Model is null ? "Foundry agent" : $"Foundry agent ({a.Model})")
                                : a.Description,
                    Endpoint:    FoundryAgentsApi.ComposeAgentEndpoint(_projectEndpoint, a.Name)))
                .ToList();

            _cached      = snapshot;
            _cachedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Refreshed agent catalog: {Count} agent(s) from {Endpoint}", snapshot.Count, _projectEndpoint);
            return _cached;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh agent catalog from {Endpoint}", _projectEndpoint);
            // Fall back to whatever we had cached (possibly empty) rather than throw —
            // a stale list is better than a broken /agents command.
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<AgentDescriptor?> FindByKeyAsync(string key, CancellationToken ct = default)
    {
        var all = await GetDescriptorsAsync(ct: ct);
        return all.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AgentDescriptor?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var all = await GetDescriptorsAsync(ct: ct);
        return all.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AgentDescriptor> DefaultAsync(CancellationToken ct = default)
    {
        var all = await GetDescriptorsAsync(ct: ct);
        return all.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No active agents found in project {_projectEndpoint}. Create one in Foundry first.");
    }

    public async Task<string?> FindKeyForEndpointAsync(string? endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(endpoint)) return null;
        var all = await GetDescriptorsAsync(ct: ct);
        return all.FirstOrDefault(d => string.Equals(d.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))?.Key;
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

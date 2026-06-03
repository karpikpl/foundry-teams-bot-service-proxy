using Microsoft.Bot.Builder;
using Newtonsoft.Json;

namespace AgentChat.Services;

/// <summary>
/// Persisted mapping of (foundryHost, project, agentName) → botId.
///
/// The Teams app manifest needs a botId to wire the user's chat to a specific
/// Bot Service registration. With URL-routed multi-agent, each agent needs
/// its own Bot Service (so it shows up as its own Teams app entry), and each
/// of those has a different MSA app id. This store lets an operator register
/// "agent X in project Y is served by bot id Z" so the manifest generator
/// embeds the right value.
///
/// Stored as Cosmos documents via Bot Framework's IStorage; key format:
///   <c>botreg/{base64url(foundryHost|project|agentName)}</c>
///
/// We also maintain an index doc at <c>botreg/_index</c> listing all known
/// composed keys. IStorage doesn't support list-all natively, so the index
/// is what powers the admin overview.
/// </summary>
public sealed class BotRegistrationStore
{
    private const string IndexKey = "botreg/_index";

    private readonly IStorage _storage;
    private readonly ILogger<BotRegistrationStore> _logger;
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    public BotRegistrationStore(IStorage storage, ILogger<BotRegistrationStore> logger)
    {
        _storage = storage;
        _logger  = logger;
    }

    public static string ComposeKey(string foundryHost, string project, string agentName)
        => $"{foundryHost}|{project}|{agentName}".ToLowerInvariant();

    private static string StorageKey(string composedKey)
    {
        // Cosmos doc ids can't contain '/', '\\', '?', '#'. The composed key
        // can contain '/' if foundryHost is a URL-encoded full project URL,
        // so be defensive.
        var safe = composedKey.Replace("/", "_").Replace("\\", "_").Replace("?", "_").Replace("#", "_");
        return $"botreg/{safe}";
    }

    public async Task<BotRegistration?> GetAsync(string foundryHost, string project, string agentName, CancellationToken ct = default)
    {
        var key = StorageKey(ComposeKey(foundryHost, project, agentName));
        var read = await _storage.ReadAsync(new[] { key }, ct);
        return read.TryGetValue(key, out var v) ? v as BotRegistration : null;
    }

    public async Task PutAsync(BotRegistration registration, CancellationToken ct = default)
    {
        registration.UpdatedUtc = DateTime.UtcNow;
        registration.ETag = "*";

        var composed = ComposeKey(registration.FoundryHost, registration.Project, registration.AgentName);
        var docKey   = StorageKey(composed);
        await _storage.WriteAsync(new Dictionary<string, object> { [docKey] = registration }, ct);

        await AddToIndexAsync(composed, ct);

        _logger.LogInformation(
            "Registered bot {BotId} for {FoundryHost}/{Project}/{AgentName}",
            registration.BotId, registration.FoundryHost, registration.Project, registration.AgentName);
    }

    public async Task DeleteAsync(string foundryHost, string project, string agentName, CancellationToken ct = default)
    {
        var composed = ComposeKey(foundryHost, project, agentName);
        await _storage.DeleteAsync(new[] { StorageKey(composed) }, ct);
        await RemoveFromIndexAsync(composed, ct);
    }

    public async Task<IReadOnlyList<BotRegistration>> ListAsync(CancellationToken ct = default)
    {
        var index = await LoadIndexAsync(ct);
        if (index.Keys.Count == 0) return Array.Empty<BotRegistration>();

        var docKeys = index.Keys.Select(StorageKey).ToArray();
        var read = await _storage.ReadAsync(docKeys, ct);
        return read.Values.OfType<BotRegistration>().OrderBy(r => r.FoundryHost).ThenBy(r => r.Project).ThenBy(r => r.AgentName).ToList();
    }

    // ---------- index helpers ----------

    private async Task<BotRegistrationIndex> LoadIndexAsync(CancellationToken ct)
    {
        var read = await _storage.ReadAsync(new[] { IndexKey }, ct);
        if (read.TryGetValue(IndexKey, out var v) && v is BotRegistrationIndex idx)
        {
            idx.ETag = "*";
            return idx;
        }
        return new BotRegistrationIndex { ETag = "*" };
    }

    private async Task AddToIndexAsync(string composedKey, CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            var idx = await LoadIndexAsync(ct);
            if (idx.Keys.Add(composedKey))
            {
                await _storage.WriteAsync(new Dictionary<string, object> { [IndexKey] = idx }, ct);
            }
        }
        finally { _indexLock.Release(); }
    }

    private async Task RemoveFromIndexAsync(string composedKey, CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            var idx = await LoadIndexAsync(ct);
            if (idx.Keys.Remove(composedKey))
            {
                await _storage.WriteAsync(new Dictionary<string, object> { [IndexKey] = idx }, ct);
            }
        }
        finally { _indexLock.Release(); }
    }
}

/// <summary>Index doc listing all registered (foundryHost,project,agentName) composed keys.</summary>
public sealed class BotRegistrationIndex : IStoreItem
{
    [JsonProperty("keys")]
    public HashSet<string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonProperty("eTag")]
    public string ETag { get; set; } = "*";
}

/// <summary>
/// One bot registration record. Lives in IStorage (Cosmos).
/// </summary>
public sealed class BotRegistration : IStoreItem
{
    [JsonProperty("foundryHost")]
    public string FoundryHost { get; set; } = "";

    [JsonProperty("project")]
    public string Project { get; set; } = "";

    [JsonProperty("agentName")]
    public string AgentName { get; set; } = "";

    /// <summary>Bot Service MSA app id (== UAMI client id when using user-assigned MI).</summary>
    [JsonProperty("botId")]
    public string BotId { get; set; } = "";

    /// <summary>Optional friendly name (e.g. "Docs Bot — Production"). Defaults to agentName when missing.</summary>
    [JsonProperty("displayName")]
    public string? DisplayName { get; set; }

    [JsonProperty("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonProperty("updatedUtc")]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>IStoreItem eTag for optimistic concurrency. Set to "*" before writes for last-writer-wins.</summary>
    [JsonProperty("eTag")]
    public string ETag { get; set; } = "*";
}

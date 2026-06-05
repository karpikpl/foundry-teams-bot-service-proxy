using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.Core;

namespace AgentChat.Foundry;

/// <summary>
/// Thin helper for the Foundry project-level "list agents" REST API.
///
/// The OpenAI SDK we use for the per-agent endpoint doesn't model the
/// project's <c>/agents</c> endpoint, so we call it ourselves with a
/// vanilla <see cref="HttpClient"/>. Same AAD scope as the rest of the bot
/// (<c>https://ai.azure.com/.default</c>).
/// </summary>
public static class FoundryAgentsApi
{
    private const string DefaultApiVersion = "2025-05-15-preview";
    private const string TokenScope        = "https://ai.azure.com/.default";

    public sealed record AgentSummary(
        string Name,
        string LatestVersion,
        string Description,
        string Status,
        string? Model)
    {
        public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// GET <c>{projectEndpoint}/agents?api-version=...</c>.
    /// projectEndpoint is the URL up to (but not including) <c>/agents</c>,
    /// e.g. <c>https://acct.services.ai.azure.com/api/projects/p</c>.
    /// </summary>
    public static async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        HttpClient http,
        string projectEndpoint,
        TokenCredential credential,
        CancellationToken ct = default)
    {
        var url = projectEndpoint.TrimEnd('/') + $"/agents?api-version={DefaultApiVersion}";

        // Honor the per-request OBO scope — when a user token is in scope we
        // call Foundry as that user. Without a scope we fall back to the
        // injected workload credential (UAMI). This is what lets us drop the
        // "Azure AI User" RBAC from the UAMI: every call from /admin and from
        // bot turns runs in a user scope, and only edge cases (unauthenticated
        // background refreshes) ever fall back to the credential.
        string token;
        var userToken = FoundryUserAuthScope.Current;
        if (!string.IsNullOrEmpty(userToken))
        {
            token = userToken!;
        }
        else
        {
            var tokenResult = await credential.GetTokenAsync(new TokenRequestContext(new[] { TokenScope }), ct);
            token = tokenResult.Token;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Foundry list-agents HTTP {(int)resp.StatusCode}: {Truncate(body, 400)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AgentSummary>();
        }

        var result = new List<AgentSummary>(data.GetArrayLength());
        foreach (var item in data.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(name)) continue;

            // Pull metadata from versions.latest (where Foundry currently exposes
            // description / status / model). Be defensive — schema may evolve.
            string version = "1", desc = "", status = "active", model = null!;
            if (item.TryGetProperty("versions", out var versions)
                && versions.ValueKind == JsonValueKind.Object
                && versions.TryGetProperty("latest", out var latest)
                && latest.ValueKind == JsonValueKind.Object)
            {
                if (latest.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    version = v.GetString() ?? "1";
                if (latest.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                    desc = d.GetString() ?? "";
                if (latest.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                    status = s.GetString() ?? "active";
                if (latest.TryGetProperty("definition", out var def) && def.ValueKind == JsonValueKind.Object
                    && def.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    model = m.GetString()!;
            }

            result.Add(new AgentSummary(name, version, desc, status, model));
        }

        return result;
    }

    /// <summary>
    /// Compose a Foundry project endpoint from route values.
    /// </summary>
    public static string ComposeProjectEndpoint(string foundryHost, string project)
    {
        if (foundryHost.StartsWith("https%3A", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(foundryHost).TrimEnd('/');
        }

        return $"https://{foundryHost}.services.ai.azure.com/api/projects/{project}";
    }

    /// <summary>
    /// Compose the per-agent endpoint URL the rest of the bot drives.
    /// </summary>
    public static string ComposeAgentEndpoint(string projectEndpoint, string agentName)
        => $"{projectEndpoint.TrimEnd('/')}/agents/{agentName}/endpoint/protocols/openai/v1";

    /// <summary>
    /// Inverse of <see cref="ComposeAgentEndpoint"/>: strip the per-agent suffix
    /// to recover the project endpoint. Returns null if the input doesn't look
    /// like a per-agent URL we composed.
    /// </summary>
    public static string? ProjectEndpointFor(string? perAgentEndpoint)
    {
        if (string.IsNullOrEmpty(perAgentEndpoint)) return null;
        const string Marker = "/agents/";
        var idx = perAgentEndpoint.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        // Sanity check: the suffix must be exactly
        // "/agents/{name}/endpoint/protocols/openai/v1".
        const string TailSuffix = "/endpoint/protocols/openai/v1";
        var tail = perAgentEndpoint.Substring(idx + Marker.Length);
        if (!tail.EndsWith(TailSuffix, StringComparison.OrdinalIgnoreCase))
            return null;
        var agentName = tail.Substring(0, tail.Length - TailSuffix.Length);
        if (string.IsNullOrWhiteSpace(agentName) || agentName.Contains('/'))
            return null;
        return perAgentEndpoint.Substring(0, idx);
    }

    private static string Truncate(string s, int max)
        => s.Length > max ? s.Substring(0, max) + "…" : s;
}

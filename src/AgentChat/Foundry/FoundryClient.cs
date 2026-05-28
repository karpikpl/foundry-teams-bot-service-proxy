using System.ClientModel.Primitives;
using Azure.Core;
using OpenAI;

namespace AgentChat.Foundry;

/// <summary>
/// Thin per-agent wrapper around the OpenAI .NET SDK, targeting a Foundry
/// per-agent endpoint URL of the form:
///   <c>https://{host}/api/projects/{project}/agents/{agent}/endpoint/protocols/openai/v1</c>
///
/// All we add over the bare SDK is:
///   1. <see cref="EntraIdAuthenticationPolicy"/> — fetches bearer tokens from
///      a <see cref="TokenCredential"/> (UMI / DefaultAzureCredential) with
///      scope <c>https://ai.azure.com/.default</c>.
///   2. <see cref="ApiVersionPolicy"/> — appends <c>?api-version=...</c> to
///      every request, since Foundry requires it.
///
/// One instance per per-agent endpoint URL. Cached in
/// <see cref="Services.AgentClientCache"/>.
/// </summary>
public sealed class FoundryClient
{
    private const string DefaultApiVersion = "2025-05-15-preview";
    private const string TokenScope        = "https://ai.azure.com/.default";

    public string Endpoint { get; }
    public OpenAIClient OpenAI { get; }

    public FoundryClient(string endpoint, TokenCredential credential, string apiVersion = DefaultApiVersion)
    {
        if (!endpoint.EndsWith("/")) endpoint += "/";
        Endpoint = endpoint;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint)
        };
        options.AddPolicy(new ApiVersionPolicy(apiVersion), PipelinePosition.PerCall);

        OpenAI = new OpenAIClient(new EntraIdAuthenticationPolicy(credential, TokenScope), options);
    }

    /// <summary>Adds <c>Authorization: Bearer &lt;token&gt;</c> using a cached AAD token.</summary>
    private sealed class EntraIdAuthenticationPolicy : AuthenticationPolicy
    {
        private readonly TokenCredential _credential;
        private readonly string[] _scopes;
        private AccessToken _cached;
        private readonly object _lock = new();

        public EntraIdAuthenticationPolicy(TokenCredential credential, string scope)
        {
            _credential = credential;
            _scopes     = new[] { scope };
        }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set("Authorization", "Bearer " + GetTokenSync());
            if (currentIndex < pipeline.Count - 1)
                pipeline[currentIndex + 1].Process(message, pipeline, currentIndex + 1);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set("Authorization", "Bearer " + await GetTokenAsync().ConfigureAwait(false));
            if (currentIndex < pipeline.Count - 1)
                await pipeline[currentIndex + 1].ProcessAsync(message, pipeline, currentIndex + 1).ConfigureAwait(false);
        }

        private string GetTokenSync()
        {
            lock (_lock)
            {
                if (_cached.Token is not null && _cached.ExpiresOn > DateTimeOffset.UtcNow.AddSeconds(60))
                    return _cached.Token;
            }
            var fetched = _credential.GetToken(new TokenRequestContext(_scopes), CancellationToken.None);
            lock (_lock) { _cached = fetched; }
            return fetched.Token;
        }

        private async ValueTask<string> GetTokenAsync()
        {
            lock (_lock)
            {
                if (_cached.Token is not null && _cached.ExpiresOn > DateTimeOffset.UtcNow.AddSeconds(60))
                    return _cached.Token;
            }
            var fetched = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), CancellationToken.None).ConfigureAwait(false);
            lock (_lock) { _cached = fetched; }
            return fetched.Token;
        }
    }

    /// <summary>Appends <c>?api-version=...</c> to every outgoing request.</summary>
    private sealed class ApiVersionPolicy : PipelinePolicy
    {
        private readonly string _apiVersion;
        public ApiVersionPolicy(string apiVersion) { _apiVersion = apiVersion; }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            InjectApiVersion(message);
            if (currentIndex < pipeline.Count - 1)
                pipeline[currentIndex + 1].Process(message, pipeline, currentIndex + 1);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            InjectApiVersion(message);
            if (currentIndex < pipeline.Count - 1)
                await pipeline[currentIndex + 1].ProcessAsync(message, pipeline, currentIndex + 1).ConfigureAwait(false);
        }

        private void InjectApiVersion(PipelineMessage message)
        {
            var uri = message.Request.Uri;
            if (uri is null) return;
            if (uri.Query.Contains("api-version=", StringComparison.OrdinalIgnoreCase)) return;
            var sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            message.Request.Uri = new Uri(uri.ToString() + sep + "api-version=" + _apiVersion);
        }
    }
}

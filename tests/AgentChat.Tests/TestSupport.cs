using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentChat.Services;

namespace AgentChat.Tests;

internal sealed class StaticTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new("test-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new(GetToken(requestContext, cancellationToken));
}

internal sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal static class TestServices
{
    public static AgentService AgentService(HttpMessageHandler handler, string defaultProjectEndpoint = "https://default-host.services.ai.azure.com/api/projects/default-project")
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:ProjectEndpoint"] = defaultProjectEndpoint,
            ["Foundry:CatalogCacheSeconds"] = "0"
        }).Build();

        return new AgentService(
            NullLogger<AgentService>.Instance,
            cfg,
            new HandlerHttpClientFactory(handler),
            new StaticTokenCredential());
    }

    public static IConfiguration Config(params KeyValuePair<string, string?>[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    public static string WebRootPath()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "AgentChat", "wwwroot")))
            dir = dir.Parent;
        return dir is null
            ? Path.GetFullPath("src/AgentChat/wwwroot")
            : Path.Combine(dir.FullName, "src", "AgentChat", "wwwroot");
    }
}

internal sealed class CatalogHandler : HttpMessageHandler
{
    private readonly IReadOnlyList<string> _agentNames;
    public List<string> RequestedProjects { get; } = new();

    public CatalogHandler(params string[] agentNames)
    {
        _agentNames = agentNames.Length == 0 ? Array.Empty<string>() : agentNames;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var marker = "/agents?";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        RequestedProjects.Add(idx < 0 ? url : url[..idx]);

        var data = string.Join(",", _agentNames.Select(name => $$"""
        {
          "name": "{{name}}",
          "versions": {
            "latest": {
              "version": "1",
              "description": "Description for {{name}}",
              "status": "active",
              "definition": { "model": "gpt-4o" }
            }
          }
        }
        """));

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"data\":[{data}]}}")
        });
    }
}

using AgentChat.Passthrough;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace AgentChat.Tests.Passthrough;

public class ActivityProtocolTransformerTests
{
    private static async Task<HttpRequestMessage> RunAsync(
        string project,
        string agent,
        string proxyPath = "/api/passthrough/myfoundry/myproject/myagent",
        string queryString = "?api-version=2025-11-15-preview",
        string method = "POST",
        string? authorization = "Bearer eyJ.test.jwt",
        string destinationPrefix = "https://myfoundry.services.ai.azure.com")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = proxyPath;
        ctx.Request.QueryString = new QueryString(queryString);
        ctx.Request.Headers["Host"] = "proxy.example.com";
        if (authorization is not null)
            ctx.Request.Headers["Authorization"] = authorization;
        ctx.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"hello\":\"world\"}"));
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = ctx.Request.Body.Length;

        var proxyRequest = new HttpRequestMessage(HttpMethod.Parse(method), "https://placeholder/");

        var transformer = new ActivityProtocolTransformer(project, agent);
        await transformer.TransformRequestAsync(ctx, proxyRequest, destinationPrefix, CancellationToken.None);
        return proxyRequest;
    }

    [Fact]
    public async Task RewritesUri_ToFoundryActivityProtocolEndpoint()
    {
        var req = await RunAsync("myproject", "myagent");
        req.RequestUri!.ToString().Should().Be(
            "https://myfoundry.services.ai.azure.com/api/projects/myproject/agents/myagent/endpoint/protocols/activityprotocol?api-version=2025-11-15-preview");
    }

    [Fact]
    public async Task PreservesQueryString()
    {
        var req = await RunAsync("p", "a", queryString: "?api-version=2025-11-15-preview&foo=bar");
        req.RequestUri!.Query.Should().Be("?api-version=2025-11-15-preview&foo=bar");
    }

    [Fact]
    public async Task WorksWithEmptyQueryString()
    {
        var req = await RunAsync("p", "a", queryString: string.Empty);
        req.RequestUri!.Query.Should().BeEmpty();
        req.RequestUri.AbsolutePath.Should().Be("/api/projects/p/agents/a/endpoint/protocols/activityprotocol");
    }

    [Fact]
    public async Task EscapesProjectAndAgent()
    {
        var req = await RunAsync("proj space", "agent/slash");
        req.RequestUri!.AbsolutePath.Should().Be(
            "/api/projects/proj%20space/agents/agent%2Fslash/endpoint/protocols/activityprotocol");
    }

    [Fact]
    public async Task ForwardsAuthorizationHeaderUnchanged()
    {
        var req = await RunAsync("p", "a", authorization: "Bearer abc.def.ghi");
        req.Headers.Authorization!.Scheme.Should().Be("Bearer");
        req.Headers.Authorization.Parameter.Should().Be("abc.def.ghi");
    }

    [Fact]
    public async Task ClearsHostHeader_SoHttpClientDerivesFromUri()
    {
        var req = await RunAsync("p", "a");
        req.Headers.Host.Should().BeNull();
    }
}

public class PassthroughEndpointsValidationTests
{
    [Theory]
    [InlineData("hack-foundry", true)]
    [InlineData("my123foundry", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("has/slash", false)]
    [InlineData("has.dot", false)]
    [InlineData("evil.com", false)]
    [InlineData("..", false)]
    public void IsSafeHostSegment_ValidatesCharacters(string value, bool expected)
    {
        PassthroughEndpoints.IsSafeHostSegment(value).Should().Be(expected);
    }

    [Fact]
    public void IsSafeHostSegment_RejectsOver63Chars()
    {
        PassthroughEndpoints.IsSafeHostSegment(new string('a', 64)).Should().BeFalse();
        PassthroughEndpoints.IsSafeHostSegment(new string('a', 63)).Should().BeTrue();
    }
}

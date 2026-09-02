using System.Diagnostics;
using System.Net;
using AgentChat.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentChat.Tests;

/// <summary>
/// Behavior tests for the log-only inbound source classifier. Contract:
///   - never rejects a request, regardless of source IP
///   - only classifies traffic on /api/messages* and /api/passthrough*
///   - hardcoded label table: Teams, AzureBotService-EastUS, else Unknown
///   - honors X-Forwarded-For first hop (ACA envoy)
///   - writes SourceLabel + ClientIp to Activity.Current for App Insights
/// </summary>
public class InboundSourceLoggingMiddlewareTests
{
    // Representative addresses inside each hardcoded CIDR.
    private const string TeamsIp = "52.113.10.20";              // in 52.112.0.0/14
    private const string TeamsIp2 = "52.123.5.5";               // in 52.122.0.0/15
    private const string BotServiceIp = "20.42.0.65";           // in 20.42.0.64/30
    private const string BotServiceIp2 = "40.71.12.245";        // in 40.71.12.244/30
    private const string RandomIp = "203.0.113.10";             // TEST-NET-3

    [Theory]
    [InlineData(TeamsIp, "Teams")]
    [InlineData(TeamsIp2, "Teams")]
    [InlineData(BotServiceIp, "AzureBotService-EastUS")]
    [InlineData(BotServiceIp2, "AzureBotService-EastUS")]
    [InlineData(RandomIp, "Unknown")]
    public async Task Classifies_known_ranges_and_passes_through(string ip, string expectedLabel)
    {
        var nextCalled = false;
        var m = MakeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = MakeContext("/api/messages/foundryA/proj1/agent1", remoteIp: ip);

        using var activity = new Activity("test").Start();

        await m.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        activity.GetTagItem("SourceLabel").Should().Be(expectedLabel);
        activity.GetTagItem("ClientIp").Should().Be(ip);
    }

    [Fact]
    public async Task Passthrough_path_is_also_classified()
    {
        var m = MakeMiddleware(_ => Task.CompletedTask);
        var ctx = MakeContext("/api/passthrough/foundryA/proj1/agent1", remoteIp: BotServiceIp);
        using var activity = new Activity("test").Start();

        await m.InvokeAsync(ctx);

        activity.GetTagItem("SourceLabel").Should().Be("AzureBotService-EastUS");
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/admin/agents")]
    [InlineData("/")]
    public async Task Non_gated_paths_are_not_classified(string path)
    {
        var nextCalled = false;
        var m = MakeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = MakeContext(path, remoteIp: BotServiceIp);
        using var activity = new Activity("test").Start();

        await m.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        activity.GetTagItem("SourceLabel").Should().BeNull();
        activity.GetTagItem("ClientIp").Should().BeNull();
    }

    [Fact]
    public async Task XForwardedFor_first_hop_wins_over_connection_ip()
    {
        var m = MakeMiddleware(_ => Task.CompletedTask);
        var ctx = MakeContext(
            "/api/messages/foundryA/proj1/agent1",
            remoteIp: "10.0.0.1",                             // envoy pod IP
            xForwardedFor: $"{TeamsIp}, 10.0.0.99");          // real client, then intermediary
        using var activity = new Activity("test").Start();

        await m.InvokeAsync(ctx);

        activity.GetTagItem("SourceLabel").Should().Be("Teams");
        activity.GetTagItem("ClientIp").Should().Be(TeamsIp);
    }

    [Fact]
    public async Task XForwardedFor_strips_port_suffix()
    {
        var m = MakeMiddleware(_ => Task.CompletedTask);
        var ctx = MakeContext(
            "/api/messages/foundryA/proj1/agent1",
            remoteIp: "10.0.0.1",
            xForwardedFor: $"{BotServiceIp}:443");
        using var activity = new Activity("test").Start();

        await m.InvokeAsync(ctx);

        activity.GetTagItem("SourceLabel").Should().Be("AzureBotService-EastUS");
        activity.GetTagItem("ClientIp").Should().Be(BotServiceIp);
    }

    [Fact]
    public async Task Missing_client_ip_is_labeled_unknown_and_passes_through()
    {
        var nextCalled = false;
        var m = MakeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = MakeContext("/api/messages/foundryA/proj1/agent1", remoteIp: null);
        using var activity = new Activity("test").Start();

        await m.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        activity.GetTagItem("SourceLabel").Should().Be("Unknown");
        activity.GetTagItem("ClientIp").Should().Be("unknown");
    }

    private static InboundSourceLoggingMiddleware MakeMiddleware(RequestDelegate next)
    {
        return new InboundSourceLoggingMiddleware(
            next,
            NullLogger<InboundSourceLoggingMiddleware>.Instance);
    }

    private static DefaultHttpContext MakeContext(string path, string? remoteIp, string? xForwardedFor = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = HttpMethods.Post;
        if (remoteIp is not null)
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }
        if (xForwardedFor is not null)
        {
            ctx.Request.Headers["X-Forwarded-For"] = xForwardedFor;
        }
        return ctx;
    }
}

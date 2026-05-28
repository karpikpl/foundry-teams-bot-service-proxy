using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AgentChat.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AgentChat.Tests;

/// <summary>
/// Tests the inbound JWT audience check. The middleware does NOT verify the
/// signature — that's CloudAdapter's job — so we use unsigned tokens here.
/// </summary>
public class BotServiceJwtMiddlewareTests
{
    private const string ExpectedAud = "dfdde025-5b67-4429-a986-dc32af044450";

    [Fact]
    public async Task Rejects_request_missing_authorization_header()
    {
        var ctx = MakeContext("/api/messages", authHeader: null);
        var m = MakeMiddleware(ExpectedAud);

        await m.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Rejects_request_with_non_bearer_authorization_header()
    {
        var ctx = MakeContext("/api/messages", authHeader: "Basic abc==");
        var m = MakeMiddleware(ExpectedAud);

        await m.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Rejects_malformed_jwt()
    {
        var ctx = MakeContext("/api/messages", authHeader: "Bearer not.a.jwt");
        var m = MakeMiddleware(ExpectedAud);

        await m.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Rejects_jwt_with_wrong_issuer()
    {
        var jwt = BuildToken(iss: "https://attacker.example.com", aud: ExpectedAud);
        var ctx = MakeContext("/api/messages", authHeader: "Bearer " + jwt);
        var m = MakeMiddleware(ExpectedAud);

        await m.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Rejects_jwt_with_wrong_audience()
    {
        var jwt = BuildToken(iss: "https://api.botframework.com", aud: "attacker-app-id");
        var ctx = MakeContext("/api/messages", authHeader: "Bearer " + jwt);
        var m = MakeMiddleware(ExpectedAud);

        await m.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Accepts_jwt_with_correct_issuer_and_audience()
    {
        var jwt = BuildToken(iss: "https://api.botframework.com", aud: ExpectedAud);
        var nextCalled = false;
        var ctx = MakeContext("/api/messages", authHeader: "Bearer " + jwt);
        var m = MakeMiddleware(ExpectedAud, next: c => { nextCalled = true; return Task.CompletedTask; });

        await m.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK); // default unchanged
    }

    [Fact]
    public async Task Audience_check_is_case_insensitive()
    {
        var jwt = BuildToken(iss: "https://api.botframework.com", aud: ExpectedAud.ToUpperInvariant());
        var nextCalled = false;
        var ctx = MakeContext("/api/messages", authHeader: "Bearer " + jwt);
        var m = MakeMiddleware(ExpectedAud, next: c => { nextCalled = true; return Task.CompletedTask; });

        await m.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Routed_path_is_also_protected()
    {
        var ctx = MakeContext("/api/messages/foundryA/proj1/agentX", authHeader: null);
        var m = MakeMiddleware(ExpectedAud);

        await m.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Non_messaging_path_passes_through_unchecked()
    {
        var nextCalled = false;
        var ctx = MakeContext("/admin/manifest", authHeader: null);
        var m = MakeMiddleware(ExpectedAud, next: c => { nextCalled = true; return Task.CompletedTask; });

        await m.InvokeAsync(ctx);

        nextCalled.Should().BeTrue("middleware only guards /api/messages — other endpoints have their own auth story");
    }

    [Fact]
    public async Task Disabled_when_expected_aud_not_configured()
    {
        var nextCalled = false;
        var ctx = MakeContext("/api/messages", authHeader: null);
        var m = MakeMiddleware(expectedAud: null, next: c => { nextCalled = true; return Task.CompletedTask; });

        await m.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    // --- helpers ---

    private static BotServiceJwtMiddleware MakeMiddleware(string? expectedAud, RequestDelegate? next = null)
    {
        var settings = new Dictionary<string, string?>();
        if (expectedAud != null) settings["BOTSERVICE_UAMI_CLIENTID"] = expectedAud;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new BotServiceJwtMiddleware(
            next ?? (_ => Task.CompletedTask),
            cfg,
            NullLogger<BotServiceJwtMiddleware>.Instance);
    }

    private static DefaultHttpContext MakeContext(string path, string? authHeader)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = "POST";
        if (authHeader != null) ctx.Request.Headers.Authorization = authHeader;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    /// <summary>
    /// Build an UNSIGNED JWT for testing. The middleware does not verify
    /// signatures (CloudAdapter does that downstream), so unsigned tokens
    /// are sufficient to exercise the issuer/audience checks.
    /// </summary>
    private static string BuildToken(string iss, string aud)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = new JwtSecurityToken(
            issuer: iss,
            audience: aud,
            claims: new[]
            {
                new Claim("serviceurl", "https://example.com/"),
            },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10));
        return handler.WriteToken(token);
    }
}

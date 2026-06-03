using System.Net;
using System.Text;
using AgentChat.Controllers;
using AgentChat.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentChat.Tests;

public class ChatTestControllerTests
{
    [Fact]
    public async Task CreateConversation_with_foundry_scope_looks_up_agent_in_composed_project_endpoint()
    {
        var handler = new CatalogHandler();
        var controller = MakeController(handler);

        var result = await controller.CreateConversation(
            new ChatTestController.CreateConvRequest("missing", "host-b", "proj-b"),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        handler.RequestedProjects.Should().ContainSingle()
            .Which.Should().Be("https://host-b.services.ai.azure.com/api/projects/proj-b");
    }

    [Fact]
    public async Task CreateConversation_without_foundry_scope_uses_default_project_endpoint()
    {
        var handler = new CatalogHandler();
        var controller = MakeController(handler);

        var result = await controller.CreateConversation(
            new ChatTestController.CreateConvRequest("missing"),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        handler.RequestedProjects.Should().ContainSingle()
            .Which.Should().Be("https://default-host.services.ai.azure.com/api/projects/default-project");
    }

    [Fact]
    public async Task DeleteConversation_with_foundry_scope_looks_up_agent_in_composed_project_endpoint()
    {
        var handler = new CatalogHandler();
        var controller = MakeController(handler);

        var result = await controller.DeleteConversation("conv-1", "missing", "host-c", "proj-c", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        handler.RequestedProjects.Should().ContainSingle()
            .Which.Should().Be("https://host-c.services.ai.azure.com/api/projects/proj-c");
    }

    [Fact]
    public async Task StreamMessage_with_foundry_scope_looks_up_agent_in_composed_project_endpoint()
    {
        var handler = new CatalogHandler();
        var controller = MakeController(handler, withHttpContext: true);

        await controller.StreamMessage(
            new ChatTestController.MessageRequest("missing", "conv-1", "hello", "host-d", "proj-d"),
            CancellationToken.None);

        handler.RequestedProjects.Should().ContainSingle()
            .Which.Should().Be("https://host-d.services.ai.azure.com/api/projects/proj-d");
        controller.Response.Body.Position = 0;
        using var reader = new StreamReader(controller.Response.Body, Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().Contain("agent 'missing' not found");
    }

    [Fact]
    public async Task CreateConversation_rejects_partial_foundry_scope()
    {
        var handler = new CatalogHandler();
        var controller = MakeController(handler);

        var result = await controller.CreateConversation(
            new ChatTestController.CreateConvRequest("missing", "host-only", null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        handler.RequestedProjects.Should().BeEmpty();
    }

    [Fact]
    public async Task Approval_resume_chains_with_previous_response_id_and_continues_to_final_output()
    {
        var catalog = new CatalogHandler("agent-a");
        var service = TestServices.AgentService(catalog);
        var foundry = new RecordingFoundryHandler();
        foundry.EnqueueSse(
            ResponseCreated("resp_approval"),
            "{\"type\":\"response.mcp_approval_requested\",\"approval_request_id\":\"mcpr_1\",\"server_label\":\"srv\",\"tool_name\":\"lookup\",\"tool_arguments\":\"{}\"}",
            ResponseCompleted("resp_approval"));
        foundry.EnqueueSse(
            ResponseCreated("resp_tool_result"),
            ResponseCompleted("resp_tool_result"));
        foundry.EnqueueSse(
            ResponseCreated("resp_final"),
            TextDelta("tool says ok"),
            ResponseCompleted("resp_final"));
        var controller = MakeController(catalog, withHttpContext: true, clientCache: foundry.ToClientCache(service), service: service);

        await controller.StreamMessage(new ChatTestController.MessageRequest("agent-a", "conv-approval", "needs tool"), CancellationToken.None);
        var first = await ReadResponseAsync(controller);
        first.Should().Contain("event: approval");

        controller = MakeController(catalog, withHttpContext: true, clientCache: foundry.ToClientCache(service), service: service);
        await controller.StreamMessage(new ChatTestController.MessageRequest(
            "agent-a",
            "conv-approval",
            null,
            Approval: new ChatTestController.ApprovalRequest("mcpr_1", true)), CancellationToken.None);
        var second = await ReadResponseAsync(controller);

        second.Should().Contain("tool says ok");
        var responseRequests = foundry.Requests.Where(r => r.Method == "POST" && r.Url.Contains("/responses")).ToList();
        responseRequests.Should().HaveCount(3);
        responseRequests[0].Body.Should().Contain("conversation");
        responseRequests[0].Body.Should().Contain("needs tool");
        responseRequests[1].Body.Should().Contain("previous_response_id");
        responseRequests[1].Body.Should().Contain("resp_approval");
        responseRequests[1].Body.Should().Contain("mcp_approval_response");
        responseRequests[2].Body.Should().Contain("previous_response_id");
        responseRequests[2].Body.Should().Contain("resp_tool_result");
    }

    [Fact]
    public async Task StreamMessage_emits_sse_error_when_foundry_stream_throws()
    {
        var catalog = new CatalogHandler("agent-a");
        var service = TestServices.AgentService(catalog);
        var foundry = new RecordingFoundryHandler();
        foundry.EnqueueJson(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"bad foundry request\"}}");
        var controller = MakeController(catalog, withHttpContext: true, clientCache: foundry.ToClientCache(service), service: service);

        await controller.StreamMessage(new ChatTestController.MessageRequest("agent-a", "conv-error", "boom"), CancellationToken.None);

        var sse = await ReadResponseAsync(controller);
        sse.Should().Contain("event: error");
        sse.Should().Contain("bad foundry request");
    }

    private static ChatTestController MakeController(
        CatalogHandler handler,
        bool withHttpContext = false,
        AgentClientCache? clientCache = null,
        AgentService? service = null)
    {
        service ??= TestServices.AgentService(handler);
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(TestServices.WebRootPath());
        var controller = new ChatTestController(
            service,
            clientCache ?? new AgentClientCache(service),
            env.Object,
            NullLogger<ChatTestController>.Instance);

        if (withHttpContext)
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Response.Body = new MemoryStream();
        }

        return controller;
    }

    private static async Task<string> ReadResponseAsync(ChatTestController controller)
    {
        controller.Response.Body.Position = 0;
        using var reader = new StreamReader(controller.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static string ResponseCreated(string id)
        => $"{{\"type\":\"response.created\",\"response\":{{\"id\":\"{id}\",\"object\":\"response\",\"created_at\":0,\"status\":\"in_progress\",\"output\":[]}}}}";

    private static string TextDelta(string text)
        => $"{{\"type\":\"response.output_text.delta\",\"delta\":\"{text}\",\"output_index\":0,\"content_index\":0,\"item_id\":\"msg_1\"}}";

    private static string ResponseCompleted(string id)
        => $"{{\"type\":\"response.completed\",\"response\":{{\"id\":\"{id}\",\"object\":\"response\",\"created_at\":0,\"status\":\"completed\",\"output\":[],\"usage\":{{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}}}}";
}

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

    private static ChatTestController MakeController(CatalogHandler handler, bool withHttpContext = false)
    {
        var service = TestServices.AgentService(handler);
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(TestServices.WebRootPath());
        var controller = new ChatTestController(
            service,
            new AgentClientCache(service),
            env.Object,
            NullLogger<ChatTestController>.Instance);

        if (withHttpContext)
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Response.Body = new MemoryStream();
        }

        return controller;
    }
}

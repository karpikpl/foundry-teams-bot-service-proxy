using FluentAssertions;
using Xunit;

namespace AgentChat.Tests;

public class StartupRegistrationTests
{
    [Fact]
    public void Program_does_not_register_background_agent_catalog_refresh()
    {
        var root = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "AgentChat", "Program.cs"));

        program.Should().NotContain("AddHostedService");
        program.Should().NotContain("BackgroundService");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FoundryTeamsBot.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}

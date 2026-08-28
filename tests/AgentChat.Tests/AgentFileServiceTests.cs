using AgentChat.Services;
using FluentAssertions;
using Xunit;

namespace AgentChat.Tests;

public class AgentFileServiceTests
{
    [Fact]
    public void TakePendingFiles_releases_expired_entry_capacity()
    {
        var files = new AgentFileService(TestServices.Config(
            KeyValuePair.Create<string, string?>("Files:MaxCachedBytes", "4"),
            KeyValuePair.Create<string, string?>("Files:DownloadLifetimeMinutes", "-1")));
        var input = new AgentInputFile("test.txt", "text/plain", BinaryData.FromBytes([1, 2, 3, 4]));

        files.StorePendingFiles("expired", [input]);
        files.TakePendingFiles("expired").Should().BeEmpty();

        var act = () => files.StorePendingFiles("next", [input]);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Concurrent_pending_file_writes_do_not_exceed_cache_limit()
    {
        var files = new AgentFileService(TestServices.Config(
            KeyValuePair.Create<string, string?>("Files:MaxCachedBytes", "10")));
        var input = new AgentInputFile("test.txt", "text/plain", BinaryData.FromBytes([1]));
        var successes = 0;

        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
        {
            try
            {
                files.StorePendingFiles($"conversation-{index}", [input]);
                Interlocked.Increment(ref successes);
            }
            catch (InvalidOperationException)
            {
            }
        })));

        successes.Should().Be(10);
    }

    [Fact]
    public void Empty_pending_file_set_replaces_prior_attachments()
    {
        var files = new AgentFileService(TestServices.Config());
        var input = new AgentInputFile("old.txt", "text/plain", BinaryData.FromString("old"));

        files.StorePendingFiles("conversation", [input]);
        files.StorePendingFiles("conversation", []);

        files.TakePendingFiles("conversation").Should().BeEmpty();
    }
}

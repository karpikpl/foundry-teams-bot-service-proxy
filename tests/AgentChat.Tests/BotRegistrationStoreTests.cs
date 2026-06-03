using System.Collections.Concurrent;
using AgentChat.Services;
using FluentAssertions;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentChat.Tests;

/// <summary>
/// BotRegistrationStore persists (foundryHost, project, agentName) → botId
/// mappings via Bot Framework's IStorage. These tests use an in-memory
/// IStorage fake to verify the CRUD contract + the index used for list-all.
/// </summary>
public class BotRegistrationStoreTests
{
    private static BotRegistrationStore Make(out InMemoryStorage storage)
    {
        storage = new InMemoryStorage();
        return new BotRegistrationStore(storage, NullLogger<BotRegistrationStore>.Instance);
    }

    [Fact]
    public async Task Get_returns_null_when_no_registration_exists()
    {
        var store = Make(out _);
        (await store.GetAsync("aif-x", "proj-x", "agent")).Should().BeNull();
    }

    [Fact]
    public async Task Put_then_Get_round_trips()
    {
        var store = Make(out _);
        await store.PutAsync(new BotRegistration
        {
            FoundryHost = "aif-x",
            Project     = "proj-x",
            AgentName   = "docs",
            BotId       = "00000000-0000-0000-0000-000000000001",
            DisplayName = "Docs bot"
        });

        var read = await store.GetAsync("aif-x", "proj-x", "docs");
        read.Should().NotBeNull();
        read!.BotId.Should().Be("00000000-0000-0000-0000-000000000001");
        read.DisplayName.Should().Be("Docs bot");
    }

    [Fact]
    public async Task Keys_are_case_insensitive()
    {
        var store = Make(out _);
        await store.PutAsync(new BotRegistration
        {
            FoundryHost = "AIF-X",
            Project     = "Proj-X",
            AgentName   = "Docs",
            BotId       = "00000000-0000-0000-0000-000000000001"
        });

        (await store.GetAsync("aif-x", "proj-x", "docs")).Should().NotBeNull();
        (await store.GetAsync("AIF-X", "PROJ-X", "DOCS")).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_removes_the_registration()
    {
        var store = Make(out _);
        var reg = new BotRegistration
        {
            FoundryHost = "aif-x",
            Project     = "proj-x",
            AgentName   = "docs",
            BotId       = "00000000-0000-0000-0000-000000000001"
        };
        await store.PutAsync(reg);
        await store.DeleteAsync("aif-x", "proj-x", "docs");
        (await store.GetAsync("aif-x", "proj-x", "docs")).Should().BeNull();
    }

    [Fact]
    public async Task List_returns_all_registrations_via_the_index()
    {
        var store = Make(out _);
        await store.PutAsync(new BotRegistration { FoundryHost = "a", Project = "p", AgentName = "x1", BotId = "00000000-0000-0000-0000-000000000001" });
        await store.PutAsync(new BotRegistration { FoundryHost = "a", Project = "p", AgentName = "x2", BotId = "00000000-0000-0000-0000-000000000002" });
        await store.PutAsync(new BotRegistration { FoundryHost = "b", Project = "q", AgentName = "x3", BotId = "00000000-0000-0000-0000-000000000003" });

        var all = await store.ListAsync();
        all.Should().HaveCount(3);
        all.Select(r => r.AgentName).Should().BeEquivalentTo(new[] { "x1", "x2", "x3" });
    }

    [Fact]
    public async Task List_returns_empty_when_no_registrations_exist()
    {
        var store = Make(out _);
        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_unknown_key_is_safe_no_op()
    {
        var store = Make(out _);
        var act = async () => await store.DeleteAsync("aif-x", "proj-x", "docs");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Put_updates_an_existing_registration_without_duplicating_in_index()
    {
        var store = Make(out _);
        await store.PutAsync(new BotRegistration { FoundryHost = "a", Project = "p", AgentName = "x", BotId = "00000000-0000-0000-0000-000000000001" });
        await store.PutAsync(new BotRegistration { FoundryHost = "a", Project = "p", AgentName = "x", BotId = "00000000-0000-0000-0000-000000000002", DisplayName = "Updated" });

        var all = await store.ListAsync();
        all.Should().HaveCount(1);
        all[0].BotId.Should().Be("00000000-0000-0000-0000-000000000002");
        all[0].DisplayName.Should().Be("Updated");
    }

    // -------------------------- in-memory IStorage fake --------------------------

    private sealed class InMemoryStorage : IStorage
    {
        private readonly ConcurrentDictionary<string, (object value, string eTag)> _store = new();

        public Task DeleteAsync(string[] keys, CancellationToken ct = default)
        {
            foreach (var k in keys) _store.TryRemove(k, out _);
            return Task.CompletedTask;
        }

        public Task<IDictionary<string, object>> ReadAsync(string[] keys, CancellationToken ct = default)
        {
            IDictionary<string, object> result = new Dictionary<string, object>();
            foreach (var k in keys)
            {
                if (_store.TryGetValue(k, out var entry))
                {
                    if (entry.value is IStoreItem item) item.ETag = entry.eTag;
                    result[k] = entry.value;
                }
            }
            return Task.FromResult(result);
        }

        public Task WriteAsync(IDictionary<string, object> changes, CancellationToken ct = default)
        {
            foreach (var kv in changes)
            {
                var newETag = Guid.NewGuid().ToString("N");
                if (kv.Value is IStoreItem item)
                {
                    if (_store.TryGetValue(kv.Key, out var existing)
                        && item.ETag != "*"
                        && item.ETag != existing.eTag)
                    {
                        throw new InvalidOperationException(
                            $"412 PreconditionFailed for key '{kv.Key}': expected eTag '{existing.eTag}' but got '{item.ETag}'.");
                    }
                    item.ETag = newETag;
                }
                _store[kv.Key] = (kv.Value, newETag);
            }
            return Task.CompletedTask;
        }
    }
}

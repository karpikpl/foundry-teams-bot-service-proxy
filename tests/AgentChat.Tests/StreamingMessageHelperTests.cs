using AgentChat.Bots;
using FluentAssertions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentChat.Tests;

public class StreamingMessageHelperTests
{
    private static ITurnContext MakeContext(string channelId, string convType, List<IActivity> sent)
    {
        var inbound = new Activity
        {
            Type = ActivityTypes.Message,
            ChannelId = channelId,
            Conversation = new ConversationAccount { Id = "conv-1", ConversationType = convType },
            From = new ChannelAccount("u1"),
            Recipient = new ChannelAccount("b1")
        };
        return new TurnContext(new RecordingAdapter(sent), inbound);
    }

    private static ITurnContext Teams(List<IActivity> sent) => MakeContext("msteams", "personal", sent);

    [Fact]
    public void Enabled_only_in_teams_personal_chat()
    {
        var sent = new List<IActivity>();
        new StreamingMessageHelper(MakeContext("msteams", "personal", sent)).Enabled.Should().BeTrue();
        new StreamingMessageHelper(MakeContext("msteams", "channel",  sent)).Enabled.Should().BeFalse();
        new StreamingMessageHelper(MakeContext("msteams", "groupChat", sent)).Enabled.Should().BeFalse();
        new StreamingMessageHelper(MakeContext("webchat", "personal", sent)).Enabled.Should().BeFalse();
        new StreamingMessageHelper(MakeContext("directline", "personal", sent)).Enabled.Should().BeFalse();
    }

    [Fact]
    public void IsOpen_starts_false()
    {
        var sent = new List<IActivity>();
        new StreamingMessageHelper(Teams(sent)).IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task IsOpen_becomes_true_after_first_send_and_false_after_finalize()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.SendInformativeAsync("hi", default);
        s.IsOpen.Should().BeTrue();

        await s.FinalizeAsync(default);
        s.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Finalize_with_buffer_in_non_streaming_channel_sends_single_message()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(MakeContext("webchat", "personal", sent));

        s.AppendDelta("hello ");
        s.AppendDelta("world");
        await s.FinalizeAsync(default);

        sent.Should().ContainSingle();
        sent[0].Type.Should().Be(ActivityTypes.Message);
        ((Activity)sent[0]).Text.Should().Be("hello world");
    }

    [Fact]
    public async Task Finalize_without_buffer_is_noop_when_no_stream_open()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.FinalizeAsync(default);
        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Finalize_after_informative_with_empty_buffer_still_closes_stream()
    {
        // Even with no text accumulated, an opened stream MUST be closed
        // with a final activity — otherwise Teams shows a stuck loader for ~2 min.
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.SendInformativeAsync("Calling tools...", default);
        await s.FinalizeAsync(default);

        sent.Should().HaveCount(2);
        var finalEntity = ((Activity)sent[1]).Entities!
            .Single(e => e.Type == "streaminfo")
            .Properties;
        finalEntity!["streamType"]!.ToString().Should().Be("final");
    }

    [Fact]
    public async Task Sequence_numbers_are_strictly_increasing_on_subsequent_sends()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.SendInformativeAsync("step 1", default);
        await s.SendInformativeAsync("step 2", default);
        await s.SendInformativeAsync("step 3", default);

        var seqs = sent.Select(a => (int)((Activity)a).Entities!
            .Single(e => e.Type == "streaminfo")
            .Properties!["streamSequence"]!).ToList();

        seqs.Should().Equal(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Final_activity_does_not_carry_streamSequence()
    {
        // Per Teams streaming spec: streamSequence MUST be absent on the final message.
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.SendInformativeAsync("Thinking", default);
        s.AppendDelta("answer");
        await s.FinalizeAsync(default);

        var finalEntity = ((Activity)sent.Last()).Entities!
            .Single(e => e.Type == "streaminfo");
        finalEntity.Properties!.ContainsKey("streamSequence").Should().BeFalse();
        finalEntity.Properties!["streamType"]!.ToString().Should().Be("final");
    }

    [Fact]
    public async Task Reopening_after_finalize_resets_sequence_to_one()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.SendInformativeAsync("A1", default);
        await s.SendInformativeAsync("A2", default);
        await s.FinalizeAsync(default);

        sent.Clear();

        await s.SendInformativeAsync("B1", default);
        var seq = (int)((Activity)sent[0]).Entities!
            .Single(e => e.Type == "streaminfo").Properties!["streamSequence"]!;
        seq.Should().Be(1, "the sequence counter must reset after Finalize so the next stream starts fresh");
    }

    [Fact]
    public async Task MaybeFlush_skips_when_under_throttle_threshold()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.SendInformativeAsync("init", default);  // opens stream
        sent.Clear();

        s.AppendDelta("a");
        await s.MaybeFlushAsync(default);  // too soon — should be skipped
        s.AppendDelta("b");
        await s.MaybeFlushAsync(default);  // still too soon

        sent.Should().BeEmpty(because: "throttle limits to 1 update / ~1.5s");
    }

    [Fact]
    public async Task ForceFlush_sends_immediately_regardless_of_throttle()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.SendInformativeAsync("init", default);
        sent.Clear();

        s.AppendDelta("a");
        await s.ForceFlushAsync(default);
        sent.Should().HaveCount(1);
        ((Activity)sent[0]).Text.Should().Be("a");
    }

    [Fact]
    public async Task ForceFlush_with_empty_buffer_is_noop()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));
        await s.SendInformativeAsync("init", default);
        sent.Clear();

        await s.ForceFlushAsync(default);
        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Streaming_disabled_helpers_are_all_noops_in_non_teams()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(MakeContext("webchat", "personal", sent));

        await s.SendInformativeAsync("hi", default);
        await s.MaybeFlushAsync(default);
        await s.ForceFlushAsync(default);
        s.IsOpen.Should().BeFalse();
        sent.Should().BeEmpty(because: "no streaming on Web Chat");
    }

    [Fact]
    public async Task Informative_open_then_finalize_sends_initial_then_final_for_teams()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.SendInformativeAsync("Thinking...", default);
        s.AppendDelta("ans");
        await s.FinalizeAsync(default);

        sent.Should().HaveCount(2);
        sent[0].Type.Should().Be(ActivityTypes.Typing);
        sent[1].Type.Should().Be(ActivityTypes.Message);
        ((Activity)sent[1]).Text.Should().Be("ans");
    }

    [Fact]
    public async Task First_chunk_omits_streamId_subsequent_carry_returned_id()
    {
        var sent = new List<IActivity>();
        var s = new StreamingMessageHelper(Teams(sent));

        await s.SendInformativeAsync("first", default);
        await s.SendInformativeAsync("second", default);

        var first = ((Activity)sent[0]).Entities!.Single(e => e.Type == "streaminfo").Properties;
        var second = ((Activity)sent[1]).Entities!.Single(e => e.Type == "streaminfo").Properties;

        first!.ContainsKey("streamId").Should().BeFalse(
            "the first send must NOT include a streamId — Teams returns the id in the response");
        second!.ContainsKey("streamId").Should().BeTrue(
            "subsequent sends must include the streamId from the first response");
    }

    private sealed class RecordingAdapter : BotAdapter
    {
        private readonly List<IActivity> _sent;
        public RecordingAdapter(List<IActivity> sink) { _sent = sink; }

        public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, Activity[] activities, CancellationToken cancellationToken)
        {
            _sent.AddRange(activities);
            return Task.FromResult(activities.Select((_, i) =>
                new ResourceResponse($"id-{_sent.Count - activities.Length + i}")).ToArray());
        }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext t, Activity a, CancellationToken c)
            => Task.FromResult(new ResourceResponse(a.Id ?? "id"));

        public override Task DeleteActivityAsync(ITurnContext t, ConversationReference r, CancellationToken c) => Task.CompletedTask;
    }
}


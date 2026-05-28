using System.Text;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Newtonsoft.Json.Linq;

namespace AgentChat.Bots;

/// <summary>
/// Teams streaming response helper.
/// https://learn.microsoft.com/en-us/microsoftteams/platform/bots/streaming-ux
///
/// CRITICAL invariant: once a stream is opened (any informative or streaming
/// activity sent), it MUST be closed with a final activity. Teams supports
/// only one concurrent stream per chat, so leaving a stream dangling:
///   - Shows the user a stuck "Calling tools..." bar for ~2 minutes
///   - Causes "Something went wrong" errors when other activities (cards,
///     regular messages) are sent on the same conversation
///
/// Use IsOpen to check before sending non-stream activities and call
/// FinalizeAsync() to gracefully close the stream first.
/// </summary>
public class StreamingMessageHelper
{
    private const int MinIntervalMs = 1500;

    private readonly ITurnContext _ctx;
    private readonly bool _enabled;

    private string? _streamId;
    private int _sequence;
    private DateTime _lastSent = DateTime.MinValue;
    private readonly StringBuilder _buffer = new();
    private string? _lastStatus;     // last informative text — used as placeholder if final has no body text
    private bool _isOpen;            // true if we've sent at least one chunk

    public StreamingMessageHelper(ITurnContext ctx)
    {
        _ctx = ctx;
        _enabled = ctx.Activity.ChannelId == "msteams"
                && string.Equals(ctx.Activity.Conversation?.ConversationType, "personal", StringComparison.OrdinalIgnoreCase);
    }

    public bool Enabled  => _enabled;
    public bool IsOpen   => _isOpen;
    public bool HasContent => _buffer.Length > 0;

    public void AppendDelta(string delta) => _buffer.Append(delta);

    /// <summary>Send an informative status update (e.g. "Searching docs...").</summary>
    public Task SendInformativeAsync(string text, CancellationToken ct)
    {
        if (!_enabled) return Task.CompletedTask;
        _lastStatus = text;
        return SendChunkAsync(ActivityTypes.Typing, text, "informative", ct);
    }

    /// <summary>Send the buffered text if enough time has passed.</summary>
    public Task MaybeFlushAsync(CancellationToken ct)
    {
        if (!_enabled || _buffer.Length == 0) return Task.CompletedTask;
        if ((DateTime.UtcNow - _lastSent).TotalMilliseconds < MinIntervalMs) return Task.CompletedTask;
        return SendChunkAsync(ActivityTypes.Typing, _buffer.ToString(), "streaming", ct);
    }

    public Task ForceFlushAsync(CancellationToken ct)
    {
        if (!_enabled || _buffer.Length == 0) return Task.CompletedTask;
        return SendChunkAsync(ActivityTypes.Typing, _buffer.ToString(), "streaming", ct);
    }

    /// <summary>
    /// ALWAYS call this before sending any non-streaming activity, and at end of run.
    /// Closes an open stream with a final message. Idempotent / no-op if no stream open.
    /// </summary>
    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (!_enabled || !_isOpen)
        {
            // No active stream. If we have buffered text but never opened a stream
            // (e.g. fallback channel), send as a plain message.
            if (_buffer.Length > 0)
            {
                await _ctx.SendActivityAsync(MessageFactory.Text(_buffer.ToString()), ct);
                _buffer.Clear();
            }
            return;
        }

        // Stream is open — MUST send final to close it. Teams requires non-empty text.
        var finalText = _buffer.Length > 0
            ? _buffer.ToString()
            : (_lastStatus ?? " ");

        var act = MessageFactory.Text(finalText);
        act.Type = ActivityTypes.Message;

        var props = new JObject { ["streamType"] = "final" };
        if (_streamId is not null) props["streamId"] = _streamId;

        var entity = new Entity("streaminfo");
        entity.Properties = props;
        act.Entities = new List<Entity> { entity };

        await _ctx.SendActivityAsync(act, ct);

        // Reset so further activity creates a brand-new stream.
        _isOpen     = false;
        _streamId   = null;
        _sequence   = 0;
        _lastSent   = DateTime.MinValue;
        _lastStatus = null;
        _buffer.Clear();
    }

    private async Task SendChunkAsync(string activityType, string text, string streamType, CancellationToken ct)
    {
        _sequence++;

        var act = MessageFactory.Text(text);
        act.Type = activityType;

        var props = new JObject
        {
            ["streamType"]     = streamType,
            ["streamSequence"] = _sequence
        };
        if (_streamId is not null) props["streamId"] = _streamId;

        var entity = new Entity("streaminfo");
        entity.Properties = props;
        act.Entities = new List<Entity> { entity };

        var response = await _ctx.SendActivityAsync(act, ct);

        if (_streamId is null && !string.IsNullOrEmpty(response?.Id))
        {
            _streamId = response.Id;
        }
        _isOpen   = true;
        _lastSent = DateTime.UtcNow;
    }
}




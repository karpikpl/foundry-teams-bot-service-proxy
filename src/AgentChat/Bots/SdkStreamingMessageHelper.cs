using System.Text;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;

namespace AgentChat.Bots;

/// <summary>
/// SDK-backed replacement for <see cref="StreamingMessageHelper"/>.
///
/// Delegates all Teams streaming plumbing (streamId management, streamSequence,
/// throttle, "close-on-final" invariant, informative vs streaming activity
/// shape) to the M365 Agents SDK's <see cref="IStreamingResponse"/> on
/// <see cref="ITurnContext.StreamingResponse"/>. Retains our public surface
/// so <see cref="FoundryBot"/> callsites don't change semantics.
///
/// Behavior deltas vs the legacy helper:
///   - Throttle is set to 1000ms via <c>StreamingResponse.Interval</c> to match
///     the Teams platform spec exactly (legacy hand-rolled helper used 1500ms
///     to be conservative — the SDK enforces the minimum internally).
///   - <see cref="AppendDelta"/> passes deltas directly to
///     <c>QueueTextChunk</c>; the SDK batches + throttles internally so
///     <see cref="MaybeFlushAsync"/> and <see cref="ForceFlushAsync"/> become
///     no-ops. Buffered text is still tracked locally for the "no stream
///     opened, fall back to plain message" case in <see cref="FinalizeAsync"/>.
///   - Heartbeat pulses stay our concern (the SDK doesn't have a "still
///     thinking…" concept). We call <c>QueueInformativeUpdateAsync</c> on
///     each pulse; the SDK routes it correctly.
///
/// Same critical invariant as the legacy helper: once a stream is opened, it
/// MUST be closed via <see cref="FinalizeAsync"/> before sending any
/// non-stream activity, or Teams shows a stuck "Calling tools…" bar for ~2
/// minutes and rejects subsequent activities with "Something went wrong".
/// </summary>
public sealed class SdkStreamingMessageHelper
{
    private const int InitialThrottleMs = 1000;             // Teams spec: 1 req/sec/stream
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(4);

    private static readonly string[] FallbackHeartbeatStatuses =
    {
        "Thinking…",
        "Working on it…",
        "Still thinking…",
        "Hang tight…",
        "Almost there…",
    };

    private readonly ITurnContext _ctx;
    private readonly bool _enabled;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // Locally-tracked buffered text — only used for the "no stream ever
    // opened, need a plain-message fallback" branch of FinalizeAsync. When a
    // stream IS opened we forward deltas to the SDK immediately and rely on
    // its internal buffering + throttle.
    private readonly StringBuilder _fallbackBuffer = new();
    private string? _lastStatus;
    private bool _textStreamingStarted;

    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private string? _heartbeatStatus;
    private int _heartbeatRotation;

    public SdkStreamingMessageHelper(ITurnContext ctx)
    {
        _ctx = ctx;
        _enabled = ctx.Activity.ChannelId == "msteams"
                && string.Equals(ctx.Activity.Conversation?.ConversationType, "personal", StringComparison.OrdinalIgnoreCase);
        if (_enabled)
        {
            // SDK is created lazily by TurnContext on first access.
            _ctx.StreamingResponse.Interval = InitialThrottleMs;
        }
    }

    public bool Enabled    => _enabled;
    public bool IsOpen     => _enabled && _ctx.StreamingResponse.IsStreamStarted();
    public bool HasContent => _fallbackBuffer.Length > 0;

    public void AppendDelta(string delta)
    {
        _fallbackBuffer.Append(delta);
        if (_enabled)
        {
            _ctx.StreamingResponse.QueueTextChunk(delta);
            _textStreamingStarted = true;
        }
    }

    public async Task SendInformativeAsync(string text, CancellationToken ct)
    {
        if (!_enabled) return;
        _lastStatus = text;
        _heartbeatStatus = text;
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ctx.StreamingResponse.QueueInformativeUpdateAsync(text, ct).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    /// <summary>
    /// No-op — the SDK's <c>StreamingResponse</c> owns throttling. Kept on the
    /// public surface so <see cref="FoundryBot"/> callsites don't need to
    /// branch on which helper they're using.
    /// </summary>
    public Task MaybeFlushAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>No-op — see <see cref="MaybeFlushAsync"/>.</summary>
    public Task ForceFlushAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Start a best-effort heartbeat that emits informative pulses via the SDK
    /// while text deltas haven't started flowing. Matches the legacy helper's
    /// semantics: pulses suppress themselves once <see cref="AppendDelta"/>
    /// has been called; they resume across multi-hop tool calls until
    /// <see cref="StopHeartbeatAsync"/>.
    /// </summary>
    public void StartHeartbeat(string? initialStatus = null, TimeSpan? interval = null)
    {
        if (!_enabled || _heartbeatTask is not null) return;
        // A non-null initialStatus becomes a sticky override; pass null to let
        // the loop rotate through FallbackHeartbeatStatuses starting with
        // "Thinking…". Callers that want variety must pass null here.
        _heartbeatStatus = string.IsNullOrWhiteSpace(initialStatus) ? null : initialStatus;
        var period = interval ?? DefaultHeartbeatInterval;
        _heartbeatCts = new CancellationTokenSource();
        var token = _heartbeatCts.Token;
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(period, token));
    }

    /// <summary>
    /// Set (or clear) the sticky informative-bar text used by the heartbeat
    /// loop. Pass a non-null string to pin the bar to a live tool status
    /// (e.g., "Calling get_weather…"). Pass <c>null</c> or whitespace to
    /// CLEAR the override so the loop resumes rotating through
    /// <see cref="FallbackHeartbeatStatuses"/>.
    /// </summary>
    public void SetHeartbeatStatus(string? status)
    {
        if (!_enabled) return;
        _heartbeatStatus = string.IsNullOrWhiteSpace(status) ? null : status;
    }

    public async Task StopHeartbeatAsync()
    {
        var cts = _heartbeatCts;
        var task = _heartbeatTask;
        _heartbeatCts = null;
        _heartbeatTask = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* ignore */ }
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); } catch { /* best-effort */ }
        }
        cts.Dispose();
    }

    private async Task HeartbeatLoopAsync(TimeSpan period, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(period, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                if (_textStreamingStarted) continue;

                try { await _sendGate.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try
                {
                    if (token.IsCancellationRequested) return;
                    if (_textStreamingStarted) continue;

                    var text = _heartbeatStatus
                        ?? FallbackHeartbeatStatuses[_heartbeatRotation++ % FallbackHeartbeatStatuses.Length];
                    _lastStatus = text;
                    await _ctx.StreamingResponse
                        .QueueInformativeUpdateAsync(text, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    // Heartbeat is best-effort; never let a transient send
                    // error tear down the turn.
                }
                finally
                {
                    try { _sendGate.Release(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* swallow */ }
    }

    /// <summary>
    /// ALWAYS call before sending any non-streaming activity, and at end of run.
    /// Closes an open stream via <c>EndStreamAsync</c>. Idempotent.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="attachments">
    /// Optional attachments (e.g. the collapsible "Reasoning" card) to include
    /// on the final activity. Per Teams streaming-UX rules, attachments are
    /// only permitted on the final chunk — never mid-stream. Applied via
    /// <see cref="IStreamingResponse.FinalMessage"/>.
    /// </param>
    public async Task FinalizeAsync(CancellationToken ct, IList<Attachment>? attachments = null)
    {
        if (!_enabled || !_ctx.StreamingResponse.IsStreamStarted())
        {
            // No active stream. If we have buffered text but never opened one
            // (e.g. fallback channel), send as a plain message.
            if (_fallbackBuffer.Length > 0 || (attachments is { Count: > 0 }))
            {
                var fallback = _fallbackBuffer.Length > 0
                    ? MessageFactory.Text(_fallbackBuffer.ToString())
                    : MessageFactory.Text(" ");
                if (attachments is { Count: > 0 })
                {
                    foreach (var a in attachments) fallback.Attachments.Add(a);
                }
                await _ctx.SendActivityAsync(fallback, ct);
                _fallbackBuffer.Clear();
            }
            return;
        }

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_ctx.StreamingResponse.IsStreamStarted()) return;

            if (attachments is { Count: > 0 })
            {
                var final = _ctx.StreamingResponse.FinalMessage
                            ?? MessageFactory.Text(_fallbackBuffer.Length > 0 ? _fallbackBuffer.ToString() : (_lastStatus ?? " "));
                foreach (var a in attachments) final.Attachments.Add(a);
                _ctx.StreamingResponse.FinalMessage = final;
            }

            await _ctx.StreamingResponse.EndStreamAsync(ct).ConfigureAwait(false);

            // Reset local state so a subsequent stream (multi-hop tool call)
            // can reopen cleanly. Note: the SDK's IStreamingResponse itself
            // is per-turn; TurnContext creates a fresh one on next access.
            _fallbackBuffer.Clear();
            _lastStatus = null;
            _textStreamingStarted = false;
        }
        finally { _sendGate.Release(); }
    }
}

using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Subscribes to <see cref="SessionStore.Lifecycle"/>, builds a
///     <see cref="SessionEcho"/> off the finalizing signal's aggregate,
///     writes it through <see cref="ISessionEchoStore"/>, then raises
///     <see cref="SessionFinalizedAckSignal"/> so the store's two-phase
///     eviction can complete.
/// </summary>
/// <remarks>
///     <para>
///         One atom, one write per session end. Serialises through the same
///         write-behind channel pattern the FOSS codebase uses everywhere
///         (bounded channel + single-reader drainer — see
///         <c>SessionPersistenceAtom</c>). Under back-pressure the shed
///         happens at the SessionStore level (lower-priority evictions
///         still emit finalizing but the deadline is short); this atom
///         simply drops on-channel-full.
///     </para>
///     <para>
///         Lazy-boots via <c>AddOnInitSignal&lt;SessionEchoAtom&gt;</c>
///         against <see cref="SessionStoreOptions.InitSignal"/>. That means
///         hosts that never receive a session upsert also never construct
///         the atom (zero cost). Once booted, the atom stays subscribed for
///         the process lifetime.
///     </para>
///     <para>
///         Ack strategy: even a failed persist raises an ack. The alternative
///         (holding the aggregate in memory until write succeeds) turns a
///         transient archive failure into a memory leak. The finalizing
///         signal's Reason is authoritative for "session ended"; whether the
///         echo landed in the archive is orthogonal to whether the store
///         can free the slot.
///     </para>
/// </remarks>
public sealed class SessionEchoAtom : IDisposable
{
    // Same shape as the write-behind channels elsewhere in the codebase
    // (SessionPersistenceAtom, ThreatIntelEnrichmentQueue). Bounded +
    // DropOldest so a mis-tuned archive can't back-pressure the finalizing
    // hot path.
    private const int WriteQueueCapacity = 512;
    private const string AtomName = "SessionEchoAtom";

    private readonly SessionStore _sessionStore;
    private readonly ISessionEchoStore _echoStore;
    private readonly ILogger<SessionEchoAtom>? _logger;
    private readonly Action<SignalEvent<SessionFinalizingSignal>> _onFinalizing;
    private readonly System.Threading.Channels.Channel<SessionFinalizingSignal> _writeQueue;
    private readonly CancellationTokenSource _drainerCts;
    private readonly Task _drainerTask;
    private int _disposed;

    public SessionEchoAtom(
        SessionStore sessionStore,
        ISessionEchoStore echoStore,
        ILogger<SessionEchoAtom>? logger = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _echoStore = echoStore ?? throw new ArgumentNullException(nameof(echoStore));
        _logger = logger;

        _writeQueue = System.Threading.Channels.Channel.CreateBounded<SessionFinalizingSignal>(
            new System.Threading.Channels.BoundedChannelOptions(WriteQueueCapacity)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        _drainerCts = new CancellationTokenSource();
        _drainerTask = Task.Run(() => DrainAsync(_drainerCts.Token));

        _onFinalizing = OnFinalizing;
        _sessionStore.Lifecycle.TypedSignalRaised += _onFinalizing;
    }

    private void OnFinalizing(SignalEvent<SessionFinalizingSignal> evt)
    {
        if (_disposed != 0) return;
        // Non-blocking. DropOldest guarantees TryWrite returns true; the
        // condition check is defensive against a future config change.
        _writeQueue.Writer.TryWrite(evt.Payload);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var signal in _writeQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await ProcessAsync(signal, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Dispose. Flush what's already queued — a session finalizing
            // right before shutdown should still get its echo attempt.
            while (_writeQueue.Reader.TryRead(out var signal))
            {
                await ProcessAsync(signal, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(SessionFinalizingSignal signal, CancellationToken ct)
    {
        try
        {
            var echo = SessionEcho.From(signal, DateTimeOffset.UtcNow);
            await _echoStore.AddEchoAsync(echo, ct).ConfigureAwait(false);
            _logger?.LogDebug(
                "SessionEchoAtom wrote echo: fp={FingerprintId} site={SiteId} reason={Reason} samples={SampleCount}",
                echo.FingerprintId, echo.SiteId, echo.FinalizeReason, echo.SampleCount);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "SessionEchoAtom archive write failed: fp={FingerprintId} site={SiteId} — ack will still fire so the aggregate is not pinned",
                signal.FingerprintId, signal.SiteId);
        }

        // Always ack — even on write failure. Otherwise a transient archive
        // outage turns into a memory leak on SessionStore.
        _sessionStore.Finalizations.Raise(
            signal: SessionFinalizedAckSignal.Key.Name,
            payload: new SessionFinalizedAckSignal
            {
                FingerprintId = signal.FingerprintId,
                SiteId = signal.SiteId,
                AckedBy = AtomName,
            },
            key: signal.FingerprintId);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _sessionStore.Lifecycle.TypedSignalRaised -= _onFinalizing; }
        catch { /* store already torn down */ }
        _writeQueue.Writer.TryComplete();
        try { _drainerCts.Cancel(); }
        catch { /* already cancelled */ }
        try { _drainerTask.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* drainer cancellation is expected */ }
        _drainerCts.Dispose();
    }
}
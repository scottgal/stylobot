using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration.Sessions;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     Pins the two-phase eviction protocol on <see cref="SessionStore"/>:
///     the cleanup loop raises <see cref="SessionFinalizingSignal"/>,
///     waits for the configured number of
///     <see cref="SessionFinalizedAckSignal"/> acks up to
///     <see cref="SessionStoreOptions.FinalizeDeadline"/>, then evicts
///     regardless. The aggregate stays in the partition through Phase 2 so
///     late subscribers still get a valid <c>Aggregate</c> reference on the
///     finalizing signal.
/// </summary>
public class SessionStoreLifecycleTests
{
    private const string SiteId = "site-1";

    private static SessionStore NewStore(
        TimeSpan ttl,
        TimeSpan finalizeDeadline,
        int expectedAcks = 1,
        bool emitFinalizingSignal = true,
        TimeSpan? cleanupInterval = null)
    {
        var options = Options.Create(new SessionStoreOptions
        {
            Ttl = ttl,
            MinTtlUnderPressure = ttl, // fixed TTL for deterministic tests
            MaxLifetime = TimeSpan.FromHours(1),
            CleanupInterval = cleanupInterval ?? TimeSpan.FromMilliseconds(50),
            FinalizeDeadline = finalizeDeadline,
            MinFinalizeDeadlineUnderPressure = finalizeDeadline,
            ExpectedAckCount = expectedAcks,
            EmitFinalizingSignal = emitFinalizingSignal,
            AckPollInterval = TimeSpan.FromMilliseconds(5),
        });
        return new SessionStore(options, NullLogger<SessionStore>.Instance);
    }

    private static SessionSample NewSample(string fp, double botProb = 0.8) => new()
    {
        FingerprintId = fp,
        SiteId = SiteId,
        Timestamp = DateTimeOffset.UtcNow.AddSeconds(-30), // old enough to age out at short TTL
        BotProbability = botProb,
        Confidence = 0.6,
        StatusCode = 200,
        FromUpstream = true,
        Honeypot = false,
    };

    [Fact]
    public async Task Cleanup_raises_finalizing_signal_when_aggregate_ages_out()
    {
        using var store = NewStore(
            ttl: TimeSpan.FromMilliseconds(10),
            finalizeDeadline: TimeSpan.FromSeconds(1),
            expectedAcks: 1,
            cleanupInterval: TimeSpan.FromMilliseconds(20));

        var received = new List<SessionFinalizingSignal>();
        store.Lifecycle.TypedSignalRaised += evt => { lock (received) received.Add(evt.Payload); };

        store.Upsert(NewSample("fp-ttl"));

        // Ack immediately so the store completes fast without hitting the deadline.
        store.Lifecycle.TypedSignalRaised += evt =>
        {
            store.Finalizations.Raise(
                SessionFinalizedAckSignal.Key.Name,
                new SessionFinalizedAckSignal
                {
                    FingerprintId = evt.Payload.FingerprintId,
                    SiteId = evt.Payload.SiteId,
                    AckedBy = "test-ack",
                },
                key: evt.Payload.FingerprintId);
        };

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (received.Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);

        received.Should().ContainSingle();
        received[0].FingerprintId.Should().Be("fp-ttl");
        received[0].Reason.Should().Be(SessionFinalizeReason.Ttl);
        received[0].Aggregate.FingerprintId.Should().Be("fp-ttl",
            "the finalizing signal must carry the full aggregate — that's the whole reason " +
            "eviction goes two-phase (subscribers need the payload before it disappears)");
    }

    [Fact]
    public async Task Fast_ack_evicts_before_deadline()
    {
        using var store = NewStore(
            ttl: TimeSpan.FromMilliseconds(10),
            finalizeDeadline: TimeSpan.FromSeconds(5), // long deadline; ack should trigger fast path
            expectedAcks: 1,
            cleanupInterval: TimeSpan.FromMilliseconds(20));

        var start = DateTimeOffset.UtcNow;
        var evictedAt = DateTimeOffset.MinValue;

        store.Lifecycle.TypedSignalRaised += evt =>
        {
            store.Finalizations.Raise(
                SessionFinalizedAckSignal.Key.Name,
                new SessionFinalizedAckSignal
                {
                    FingerprintId = evt.Payload.FingerprintId,
                    SiteId = evt.Payload.SiteId,
                    AckedBy = "fast-ack",
                },
                key: evt.Payload.FingerprintId);
        };

        store.Upsert(NewSample("fp-fast"));

        var timeout = DateTimeOffset.UtcNow.AddSeconds(3);
        while (store.TryGet(SiteId, "fp-fast") is not null && DateTimeOffset.UtcNow < timeout)
            await Task.Delay(20);
        evictedAt = DateTimeOffset.UtcNow;

        store.TryGet(SiteId, "fp-fast").Should().BeNull("fast ack should have triggered eviction");
        (evictedAt - start).Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the eviction must not have waited the full 5-second deadline when ack came quickly");
    }

    [Fact]
    public async Task Deadline_expires_and_still_evicts_when_no_ack()
    {
        using var store = NewStore(
            ttl: TimeSpan.FromMilliseconds(10),
            finalizeDeadline: TimeSpan.FromMilliseconds(150),
            expectedAcks: 1, // require an ack that will never come
            cleanupInterval: TimeSpan.FromMilliseconds(20));

        store.Upsert(NewSample("fp-orphan"));

        // Do NOT wire an ack — the deadline is the only escape.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (store.TryGet(SiteId, "fp-orphan") is not null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);

        store.TryGet(SiteId, "fp-orphan").Should().BeNull(
            "the finalize deadline must evict the aggregate even without an ack — otherwise " +
            "a slow or missing subscriber would pin memory indefinitely");
    }

    [Fact]
    public async Task No_finalize_path_evicts_immediately_when_signal_disabled()
    {
        using var store = NewStore(
            ttl: TimeSpan.FromMilliseconds(10),
            finalizeDeadline: TimeSpan.FromSeconds(30), // huge deadline — must be skipped
            expectedAcks: 1,
            emitFinalizingSignal: false,
            cleanupInterval: TimeSpan.FromMilliseconds(20));

        var raised = 0;
        store.Lifecycle.TypedSignalRaised += _ => Interlocked.Increment(ref raised);

        store.Upsert(NewSample("fp-nofinalize"));

        var start = DateTimeOffset.UtcNow;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (store.TryGet(SiteId, "fp-nofinalize") is not null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        var elapsed = DateTimeOffset.UtcNow - start;

        store.TryGet(SiteId, "fp-nofinalize").Should().BeNull(
            "EmitFinalizingSignal=false should short-circuit to immediate eviction");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "immediate eviction should not wait on any deadline");
        raised.Should().Be(0,
            "no finalizing signal should be raised when EmitFinalizingSignal=false");
    }
}

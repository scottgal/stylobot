using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration.Sessions;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     Pins <see cref="SessionEchoAtom"/>: subscribes to the finalizing
///     signal, writes an echo via <see cref="ISessionEchoStore"/>, raises
///     the ack so the store's two-phase eviction can complete. Also pins
///     the failure-still-acks contract — a failed archive write does not
///     pin the session aggregate in memory.
/// </summary>
public class SessionEchoAtomTests
{
    private const string SiteId = "site-1";
    private const string FingerprintId = "fp-echo";

    private static SessionStore NewStore(TimeSpan? ttl = null, TimeSpan? deadline = null)
    {
        var options = Options.Create(new SessionStoreOptions
        {
            Ttl = ttl ?? TimeSpan.FromMilliseconds(10),
            MinTtlUnderPressure = ttl ?? TimeSpan.FromMilliseconds(10),
            MaxLifetime = TimeSpan.FromHours(1),
            CleanupInterval = TimeSpan.FromMilliseconds(20),
            FinalizeDeadline = deadline ?? TimeSpan.FromSeconds(1),
            MinFinalizeDeadlineUnderPressure = deadline ?? TimeSpan.FromSeconds(1),
            ExpectedAckCount = 1,
            EmitFinalizingSignal = true,
            AckPollInterval = TimeSpan.FromMilliseconds(5),
        });
        return new SessionStore(options, NullLogger<SessionStore>.Instance);
    }

    private static SessionSample NewSample(string fp = FingerprintId, bool honeypot = false)
        => new()
        {
            FingerprintId = fp,
            SiteId = SiteId,
            Timestamp = DateTimeOffset.UtcNow.AddSeconds(-30),
            BotProbability = 0.72,
            Confidence = 0.6,
            StatusCode = 404,
            FromUpstream = true,
            Honeypot = honeypot,
        };

    private sealed class CapturingEchoStore : ISessionEchoStore
    {
        private readonly List<SessionEcho> _writes = new();
        public IReadOnlyList<SessionEcho> Writes { get { lock (_writes) return _writes.ToArray(); } }
        public Task AddEchoAsync(SessionEcho echo, CancellationToken cancellationToken = default)
        {
            lock (_writes) _writes.Add(echo);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingEchoStore : ISessionEchoStore
    {
        public int Attempts { get; private set; }
        public Task AddEchoAsync(SessionEcho echo, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("simulated archive outage");
        }
    }

    [Fact]
    public async Task Echo_written_and_ack_raised_on_finalize()
    {
        using var store = NewStore();
        var echoStore = new CapturingEchoStore();
        using var atom = new SessionEchoAtom(store, echoStore, NullLogger<SessionEchoAtom>.Instance);

        store.Upsert(NewSample());

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (echoStore.Writes.Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);

        echoStore.Writes.Should().ContainSingle(
            "the echo atom must write exactly one row per finalized session");
        var echo = echoStore.Writes[0];
        echo.FingerprintId.Should().Be(FingerprintId);
        echo.SiteId.Should().Be(SiteId);
        echo.SampleCount.Should().Be(1);
        echo.FinalizeReason.Should().Be(SessionFinalizeReason.Ttl);

        // Aggregate should be evicted (ack quorum met).
        var evictDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (store.TryGet(SiteId, FingerprintId) is not null && DateTimeOffset.UtcNow < evictDeadline)
            await Task.Delay(20);
        store.TryGet(SiteId, FingerprintId).Should().BeNull(
            "the atom's ack should have released the pending-eviction latch");
    }

    [Fact]
    public async Task Failed_write_still_acks_so_aggregate_is_not_pinned()
    {
        using var store = NewStore(
            ttl: TimeSpan.FromMilliseconds(10),
            // Short deadline: if the atom did NOT ack on failure, the store
            // would wait this long then evict anyway — but we want to prove
            // it's the ack landing, not the deadline.
            deadline: TimeSpan.FromSeconds(10));
        var echoStore = new FailingEchoStore();
        using var atom = new SessionEchoAtom(store, echoStore, NullLogger<SessionEchoAtom>.Instance);

        store.Upsert(NewSample());

        // Eviction should complete WELL before the 10s deadline — the atom
        // acks on failure to avoid pinning memory during an archive outage.
        var start = DateTimeOffset.UtcNow;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (store.TryGet(SiteId, FingerprintId) is not null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        var elapsed = DateTimeOffset.UtcNow - start;

        store.TryGet(SiteId, FingerprintId).Should().BeNull(
            "aggregate must be evicted despite the archive write failure");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            "eviction must complete via the ack path, not the 10-second deadline fallback");
        echoStore.Attempts.Should().Be(1,
            "exactly one write was attempted before the ack fired");
    }

    [Fact]
    public async Task Echo_carries_honeypot_hit_count()
    {
        using var store = NewStore();
        var echoStore = new CapturingEchoStore();
        using var atom = new SessionEchoAtom(store, echoStore, NullLogger<SessionEchoAtom>.Instance);

        store.Upsert(NewSample(honeypot: true));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (echoStore.Writes.Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);

        echoStore.Writes.Should().ContainSingle().Which.HoneypotHits.Should().Be(1,
            "honeypot hits must survive the projection — this is one of the fields " +
            "operators specifically look at when auditing the session accounting log");
    }
}

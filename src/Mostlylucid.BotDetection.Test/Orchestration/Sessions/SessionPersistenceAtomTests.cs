using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Sessions;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     Pins <see cref="SessionPersistenceAtom"/>: the subscriber that
///     completes the persistence loop by writing shifted aggregates through
///     <see cref="IFingerprintStore.RecordVerdictAsync"/>. Wraps a light
///     capture-list fake store; no real DB touched.
/// </summary>
public class SessionPersistenceAtomTests
{
    private const string FingerprintId = "fp-shifted";
    private const string SiteId = "site-1";

    private static SessionAtom NewSessionAtom(IFingerprintReader? reader = null)
    {
        var storeOptions = Options.Create(new SessionStoreOptions
        {
            CleanupInterval = TimeSpan.FromHours(1),
        });
        var store = new SessionStore(storeOptions, NullLogger<SessionStore>.Instance);
        var atomOptions = Options.Create(new SessionAtomOptions());
        return new SessionAtom(store, atomOptions, fingerprintReader: reader);
    }

    private static SessionPersistenceSignal NewShift(
        SessionShiftReason reason = SessionShiftReason.Honeypot,
        double meanBotProbability = 0.7)
    {
        var aggregate = new SessionAggregate
        {
            FingerprintId = FingerprintId,
            SiteId = SiteId,
            FirstSample = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastSample = DateTimeOffset.UtcNow,
            SampleCount = 3,
            MeanBotProbability = meanBotProbability,
            MaxBotProbability = meanBotProbability,
            LatestConfidence = 0.6,
            HoneypotHits = 0,
            UpstreamStatusCounts = new Dictionary<int, int>(),
            RetentionPriority = 0.5,
        };
        return new SessionPersistenceSignal
        {
            FingerprintId = FingerprintId,
            SiteId = SiteId,
            Reason = reason,
            ProbabilityDelta = 0.4,
            Aggregate = aggregate,
        };
    }

    private static void Raise(SessionAtom atom, SessionPersistenceSignal signal)
    {
        // Push the signal onto the atom's Persistence sink so subscribers
        // fire the same way they would in production.
        atom.Persistence.Raise(SessionPersistenceSignal.SignalName, signal, key: signal.FingerprintId);
    }

    private static async Task WaitForBackground(SessionPersistenceAtom persistence)
    {
        // SessionPersistenceAtom serialises writes through a single-reader
        // drainer channel; FlushAsync spin-waits until every enqueued shift
        // has been written. Replaces the earlier "yield 10 times with a 20ms
        // delay" heuristic, which failed under cold thread-pool warm-up
        // (Multiple_shifts_produce_multiple_writes race).
        await persistence.FlushAsync(TimeSpan.FromSeconds(5));
    }

    // ── Store present ────────────────────────────────────────────────

    [Fact]
    public async Task Writes_through_to_fingerprint_store_on_shift()
    {
        var store = new CapturingFingerprintStore();
        using var atom = NewSessionAtom();
        using var persistence = new SessionPersistenceAtom(atom, store, NullLogger<SessionPersistenceAtom>.Instance);

        Raise(atom, NewShift(meanBotProbability: 0.72));
        await WaitForBackground(persistence);

        store.Writes.Should().ContainSingle();
        store.Writes[0].FingerprintId.Should().Be(FingerprintId);
        store.Writes[0].BotProbability.Should().Be(0.72);
    }

    [Fact]
    public async Task Multiple_shifts_produce_multiple_writes()
    {
        var store = new CapturingFingerprintStore();
        using var atom = NewSessionAtom();
        using var persistence = new SessionPersistenceAtom(atom, store, NullLogger<SessionPersistenceAtom>.Instance);

        Raise(atom, NewShift(meanBotProbability: 0.5));
        Raise(atom, NewShift(meanBotProbability: 0.6));
        Raise(atom, NewShift(meanBotProbability: 0.7));
        await WaitForBackground(persistence);

        store.Writes.Should().HaveCount(3);
        store.Writes.Select(w => w.BotProbability).Should().Equal(0.5, 0.6, 0.7);
    }

    [Fact]
    public async Task Writes_derived_risk_band_alongside_probability()
    {
        var store = new CapturingFingerprintStore();
        using var atom = NewSessionAtom();
        using var persistence = new SessionPersistenceAtom(atom, store, NullLogger<SessionPersistenceAtom>.Instance);

        Raise(atom, NewShift(meanBotProbability: 0.96));
        await WaitForBackground(persistence);

        store.Writes.Should().ContainSingle().Which.RiskBand.Should().Be(RiskBand.VeryHigh.ToString(),
            "p >= 0.95 maps to VeryHigh -- matches the orchestrator's mapping");
    }

    // ── Store absent ─────────────────────────────────────────────────

    [Fact]
    public async Task No_op_when_no_fingerprint_store_registered()
    {
        using var atom = NewSessionAtom();
        // No store passed -- the atom must not throw when a shift raises.
        using var persistence = new SessionPersistenceAtom(atom, fingerprintStore: null,
            logger: NullLogger<SessionPersistenceAtom>.Instance);

        Raise(atom, NewShift());
        await WaitForBackground(persistence);

        // Nothing to assert positively -- absence of exception is the test.
        // Prove the atom is functional afterwards with a store swap-in-like
        // dispose call:
        persistence.Dispose();
    }

    // ── Lifecycle ────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_stops_further_writes()
    {
        var store = new CapturingFingerprintStore();
        using var atom = NewSessionAtom();
        var persistence = new SessionPersistenceAtom(atom, store, NullLogger<SessionPersistenceAtom>.Instance);

        Raise(atom, NewShift(meanBotProbability: 0.5));
        await WaitForBackground(persistence);
        store.Writes.Should().ContainSingle();

        persistence.Dispose();
        Raise(atom, NewShift(meanBotProbability: 0.9));
        await WaitForBackground(persistence);

        store.Writes.Should().ContainSingle(
            "disposed persistence atom must not enqueue further writes");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private sealed record RecordedWrite(string FingerprintId, double BotProbability, string? RiskBand);

    private sealed class CapturingFingerprintStore : NullFingerprintStore, IFingerprintStore
    {
        private readonly List<RecordedWrite> _writes = new();
        public IReadOnlyList<RecordedWrite> Writes
        {
            get { lock (_writes) return _writes.ToArray(); }
        }

        Task IFingerprintStore.RecordVerdictAsync(
            string fingerprintId, double botProbability, string? riskBand, CancellationToken ct,
            string? botType)
        {
            lock (_writes)
                _writes.Add(new RecordedWrite(fingerprintId, botProbability, riskBand));
            return Task.CompletedTask;
        }
    }
}

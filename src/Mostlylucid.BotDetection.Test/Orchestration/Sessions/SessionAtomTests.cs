using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Orchestration.Sessions;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     Pins <see cref="SessionAtom"/>'s four shift-detection rules. Each
///     rule maps to one <see cref="SessionShiftReason"/>; when a rule
///     fires, the atom raises a <see cref="SessionPersistenceSignal"/> on
///     its persistence sink. Tests drive the atom via
///     <see cref="SessionAtom.EvaluateAsync"/> directly so they never race
///     with the background subscription.
/// </summary>
public class SessionAtomTests
{
    private const string FingerprintId = "fp-under-test";
    private const string SiteId = "site-1";

    private static SessionStore NewStore()
    {
        var opts = new SessionStoreOptions { CleanupInterval = TimeSpan.FromHours(1) };
        return new SessionStore(Options.Create(opts), NullLogger<SessionStore>.Instance);
    }

    private static SessionAtom NewAtom(IFingerprintReader? reader = null, int minSamples = 3)
    {
        var atomOptions = Options.Create(new SessionAtomOptions
        {
            MinSamplesToPersist = minSamples,
            ProbabilityShiftDelta = 0.15,
        });
        return new SessionAtom(
            NewStore(),
            atomOptions,
            fingerprintReader: reader,
            logger: NullLogger<SessionAtom>.Instance);
    }

    private static SessionAggregate NewAggregate(
        int sampleCount = 3,
        double mean = 0.5,
        double confidence = 0.5,
        int honeypotHits = 0,
        string? dominantClientType = null)
        => new()
        {
            FingerprintId = FingerprintId,
            SiteId = SiteId,
            FirstSample = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastSample = DateTimeOffset.UtcNow,
            SampleCount = sampleCount,
            MeanBotProbability = mean,
            MaxBotProbability = mean,
            LatestConfidence = confidence,
            HoneypotHits = honeypotHits,
            UpstreamStatusCounts = new Dictionary<int, int>(),
            DominantClientType = dominantClientType,
            RetentionPriority = 0.5,
        };

    private static Fingerprint NewFingerprint(
        double cachedBotProbability,
        string inferredClientType = "Browser")
        => new()
        {
            FingerprintId = FingerprintId,
            Centroid = Array.Empty<float>(),
            CentroidMaturity = 5,
            Weights = Array.Empty<float>(),
            MemberCount = 1,
            ObservationCount = 10,
            CorrectionCount = 0,
            FirstSeen = DateTime.UtcNow.AddHours(-1),
            LastSeen = DateTime.UtcNow,
            Quality = 0.9,
            InferredClientType = inferredClientType,
            InferredTypeConfidence = 0.8,
            InferredTypeChangedAt = DateTime.UtcNow,
            CachedBotProbability = cachedBotProbability,
        };

    // ── Honeypot rule ────────────────────────────────────────────────

    [Fact]
    public async Task Emits_honeypot_shift_when_every_sample_is_honeypot()
    {
        using var atom = NewAtom();
        var received = CaptureShifts(atom);
        var aggregate = NewAggregate(sampleCount: 1, honeypotHits: 1);

        await atom.EvaluateAsync(aggregate, CancellationToken.None);

        received.Should().ContainSingle().Which.Reason.Should().Be(SessionShiftReason.Honeypot);
    }

    [Fact]
    public async Task Does_not_re_emit_honeypot_after_non_honeypot_sample_lands()
    {
        // If subsequent samples are non-honeypot, the honeypot rule stops
        // firing on every raise (SampleCount != HoneypotHits). Otherwise a
        // long-lived session with one honeypot hit early on would spam
        // persistence.
        using var atom = NewAtom();
        var received = CaptureShifts(atom);
        var aggregate = NewAggregate(sampleCount: 5, honeypotHits: 1);

        await atom.EvaluateAsync(aggregate, CancellationToken.None);

        received.Should().BeEmpty();
    }

    // ── NewFingerprint rule ──────────────────────────────────────────

    [Fact]
    public async Task Emits_new_fingerprint_shift_when_reader_returns_null()
    {
        var reader = new FakeReader(null);
        using var atom = NewAtom(reader, minSamples: 3);
        var received = CaptureShifts(atom);
        var aggregate = NewAggregate(sampleCount: 3, mean: 0.6);

        await atom.EvaluateAsync(aggregate, CancellationToken.None);

        received.Should().ContainSingle().Which.Reason.Should().Be(SessionShiftReason.NewFingerprint);
    }

    [Fact]
    public async Task Does_not_emit_new_fingerprint_shift_below_min_samples()
    {
        var reader = new FakeReader(null);
        using var atom = NewAtom(reader, minSamples: 3);
        var received = CaptureShifts(atom);
        var aggregate = NewAggregate(sampleCount: 2);

        await atom.EvaluateAsync(aggregate, CancellationToken.None);

        received.Should().BeEmpty("MinSamplesToPersist gate must hold for new-fingerprint rule");
    }

    // ── ProbabilityShift rule ────────────────────────────────────────

    [Fact]
    public async Task Emits_probability_shift_when_mean_crosses_persisted_band()
    {
        var reader = new FakeReader(NewFingerprint(cachedBotProbability: 0.3));
        using var atom = NewAtom(reader, minSamples: 3);
        var received = CaptureShifts(atom);
        var aggregate = NewAggregate(sampleCount: 5, mean: 0.75); // delta = 0.45 >> 0.15

        await atom.EvaluateAsync(aggregate, CancellationToken.None);

        var shift = received.Should().ContainSingle().Which;
        shift.Reason.Should().Be(SessionShiftReason.ProbabilityShift);
        shift.ProbabilityDelta.Should().BeApproximately(0.45, 0.01);
    }

    [Fact]
    public async Task Does_not_emit_probability_shift_within_delta()
    {
        var reader = new FakeReader(NewFingerprint(cachedBotProbability: 0.5));
        using var atom = NewAtom(reader, minSamples: 3);
        var received = CaptureShifts(atom);
        var aggregate = NewAggregate(sampleCount: 5, mean: 0.55); // delta = 0.05 < 0.15

        await atom.EvaluateAsync(aggregate, CancellationToken.None);

        received.Should().BeEmpty();
    }

    // ── ClientTypeShift rule ─────────────────────────────────────────

    [Fact]
    public async Task Emits_client_type_shift_when_aggregate_disagrees_with_persisted()
    {
        var reader = new FakeReader(NewFingerprint(cachedBotProbability: 0.5, inferredClientType: "Browser"));
        using var atom = NewAtom(reader, minSamples: 3);
        var received = CaptureShifts(atom);
        // Mean sits inside the probability band so ProbabilityShift does
        // not fire; ClientTypeShift is what we want to isolate.
        var aggregate = NewAggregate(sampleCount: 5, mean: 0.5, dominantClientType: "Scraper");

        await atom.EvaluateAsync(aggregate, CancellationToken.None);

        received.Should().ContainSingle().Which.Reason.Should().Be(SessionShiftReason.ClientTypeShift);
    }

    [Fact]
    public async Task Does_not_emit_client_type_shift_when_types_match_case_insensitively()
    {
        var reader = new FakeReader(NewFingerprint(cachedBotProbability: 0.5, inferredClientType: "browser"));
        using var atom = NewAtom(reader, minSamples: 3);
        var received = CaptureShifts(atom);
        var aggregate = NewAggregate(sampleCount: 5, mean: 0.5, dominantClientType: "Browser");

        await atom.EvaluateAsync(aggregate, CancellationToken.None);

        received.Should().BeEmpty();
    }

    // ── Log-only path when reader is absent ──────────────────────────

    [Fact]
    public async Task Does_not_emit_when_no_reader_and_no_honeypot()
    {
        using var atom = NewAtom(reader: null);
        var received = CaptureShifts(atom);
        var aggregate = NewAggregate(sampleCount: 5, mean: 0.9);

        await atom.EvaluateAsync(aggregate, CancellationToken.None);

        received.Should().BeEmpty(
            "no reader means we cannot compare against persisted state; log-only path");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static List<SessionPersistenceSignal> CaptureShifts(SessionAtom atom)
    {
        var received = new List<SessionPersistenceSignal>();
        atom.Persistence.TypedSignalRaised += evt => received.Add(evt.Payload);
        return received;
    }

    /// <summary>
    ///     Extends the FOSS <see cref="NullFingerprintStore"/> so the test
    ///     only overrides <see cref="IFingerprintReader.GetFingerprintAsync"/>
    ///     while inheriting the no-op implementations of every other
    ///     interface method. Uses explicit interface implementation so
    ///     interface-typed dispatch (which is what <see cref="SessionAtom"/>
    ///     does) picks up the override even though the base's method is not
    ///     virtual.
    /// </summary>
    private sealed class FakeReader : NullFingerprintStore, IFingerprintReader
    {
        private readonly Fingerprint? _fingerprint;

        public FakeReader(Fingerprint? fingerprint) => _fingerprint = fingerprint;

        Task<Fingerprint?> IFingerprintReader.GetFingerprintAsync(string fingerprintId, CancellationToken ct)
            => Task.FromResult(_fingerprint);
    }
}
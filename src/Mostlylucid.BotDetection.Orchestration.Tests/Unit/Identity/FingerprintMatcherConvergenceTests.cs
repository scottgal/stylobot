using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Pins the squish-to-centroid contract: rotated bot identities (different
///     primary signatures, same survivor-dim profile) MUST collapse onto a single
///     fingerprint; NAT-shared humans (shared network dims, distinct identity dims)
///     MUST stay separate; L1->Pass2 corrections MUST repoint fingerprint_keys to
///     the Pass 2 winner; verified bots MUST converge on the canonical id even when
///     source IPs rotate; rotation-band matches MUST raise the rotation-candidate
///     signal.
///
///     See docs/architecture/fingerprint-match.md.
/// </summary>
public sealed class FingerprintMatcherConvergenceTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private SqliteFingerprintStore _store = null!;
    private IdentityProcessingCoordinator _coordinator = null!;
    private CancellationTokenSource _coordCts = null!;
    private FingerprintMatchContributor _matcher = null!;
    private IdentityVectorLayout _layout = null!;
    private IdentityArchetypeRegistry _archetypes = null!;
    private IdentityGlobalWeightsCache _globalWeights = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-fp-conv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Engine = new IdentityEngineOptions { PreferSqliteVec = false },
                Coordinator = new IdentityCoordinatorOptions
                {
                    // Tight bounds for tests: 1 worker keeps Pass 2 dispatch
                    // deterministic, short coalesce window avoids cross-test
                    // bleed-through, generous queue depth so per-test
                    // sequences never shed.
                    WorkerCount = 1,
                    MaxQueueDepth = 32,
                    MaxQueuedPerFingerprint = 8,
                    CoalesceWindowMs = 1
                }
            }
        });

        _layout = IdentityVectorLayout.DefaultV1();
        _store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, _layout);
        await _store.EnsureInitialisedAsync();

        var index = new BruteForceIdentityAnchorIndex(_store);
        _archetypes = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance,
            new IdentityVectorEncoder(_layout));
        // Deliberately leave the registry empty. FindNearest returns null and
        // the matcher seeds new centroids from the input vector unblended,
        // which keeps cosine math predictable across the test fixtures.

        _globalWeights = new IdentityGlobalWeightsCache(
            NullLogger<IdentityGlobalWeightsCache>.Instance, _store, options);
        // No StartAsync: Current stays null and Compose returns per-fp weights
        // unchanged. Tests assert on cosine math driven by per-fp seed weights only.

        _coordinator = new IdentityProcessingCoordinator(
            NullLogger<IdentityProcessingCoordinator>.Instance, options);
        _coordCts = new CancellationTokenSource();
        _ = _coordinator.StartAsync(_coordCts.Token);

        _matcher = new FingerprintMatchContributor(
            NullLogger<FingerprintMatchContributor>.Instance,
            _store,
            index,
            _archetypes,
            _globalWeights,
            _coordinator,
            new IdentityVectorEncoder(_layout),
            options);
    }

    public async Task DisposeAsync()
    {
        _coordCts.Cancel();
        await _coordinator.StopAsync(CancellationToken.None);
        _coordinator.Dispose();
        _coordCts.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RotatedBot_DifferentPrimarySigsSameVector_CollapsesToSingleFingerprint()
    {
        // The bot-stitch contract. Five rotated primary signatures, one shared
        // identity-vector shape. The matcher should allocate on the first call
        // and Pass-2-match every subsequent rotation back onto the same centroid.
        var vector = MakeUnitVector(_layout.Dimension, seed: 7);
        var fingerprintIds = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            var sig = $"rotated-sig-{i:D2}";
            var fpId = await RunMatcherAsync(vector, sig);
            fingerprintIds.Add(fpId);
        }

        Assert.Equal(5, fingerprintIds.Count);
        var distinct = fingerprintIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.True(distinct == 1,
            $"Rotated bot did not converge: {distinct} distinct fingerprints across 5 rotations " +
            $"({string.Join(", ", fingerprintIds.Distinct())}). The Pass 2 KNN + MergeThreshold " +
            "absorption path is failing to stitch rotations.");

        var canonicalId = fingerprintIds[0];
        var obsCount = await _store.GetUnabsorbedObservationCountAsync(canonicalId);
        // The first call allocates the fingerprint via Pass 2 (no observation row
        // written -- only the fingerprint row + key). The remaining four calls
        // Pass-2-match the existing centroid and each call RecordObservationAsync.
        Assert.Equal(4, obsCount);
    }

    [Fact]
    public async Task NattedHumans_SharedNetworkDimsDifferentIdentityDims_StayDistinct()
    {
        // The human-non-collapse contract. Three vectors that share the network.*
        // bucket (so a NAT-style overlap is encoded) but project orthogonally on
        // the locale + header + tool buckets. Each must allocate its own
        // fingerprint -- cosine across the orthogonal identity slots dominates
        // the small shared-network contribution and drops below LooseThreshold.
        var fingerprintIds = new List<string>();
        for (var actor = 0; actor < 3; actor++)
        {
            var vector = MakeNatVector(actor);
            var fpId = await RunMatcherAsync(vector, primarySig: $"nat-actor-{actor:D2}");
            fingerprintIds.Add(fpId);
        }

        var distinct = fingerprintIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.True(distinct == 3,
            $"NAT-shared humans collapsed: only {distinct} distinct fingerprints across 3 actors. " +
            "Cosine over shared network dims is dominating the orthogonal identity dims.");
    }

    [Fact]
    public async Task L1FailedConfirm_Pass2WinnerRepointsKey()
    {
        // The correction contract. Pre-seed two fingerprints with orthogonal
        // centroids and a fingerprint_keys row that maps a primary signature to
        // the WRONG one. Drive the matcher with a vector close to the right one;
        // L1 confirm against the wrong candidate fails, Pass 2 KNN finds the
        // right one above MergeThreshold, UpsertKeyAsync repoints the key.
        var vectorA = MakeOrthogonalVector(_layout.Dimension, hotSlotStart: 0, hotSlotCount: 8, seed: 11);
        // The default v1 layout has ~98 dims; keep both windows comfortably within
        // the dimension so we get genuine orthogonal supports rather than an
        // out-of-range no-op zero vector.
        var vectorB = MakeOrthogonalVector(_layout.Dimension, hotSlotStart: 40, hotSlotCount: 8, seed: 13);

        var fpA = MakeMatureFingerprint("fp-A", vectorA);
        var fpB = MakeMatureFingerprint("fp-B", vectorB);
        await _store.InsertFingerprintAsync(fpA, primarySignature: "seed-A");
        await _store.InsertFingerprintAsync(fpB, primarySignature: "seed-B");

        // Point sig-X at fp-A even though the incoming traffic looks like fp-B.
        const string sigX = "sig-X";
        await _store.UpsertKeyAsync(sigX, "fp-A");

        var fpIdReturned = await RunMatcherAsync(vectorB, primarySig: sigX);

        Assert.Equal("fp-B", fpIdReturned);
        var keyAfter = await _store.LookupFingerprintIdAsync(sigX);
        Assert.Equal("fp-B", keyAfter);
    }

    [Fact]
    public async Task VerifiedBot_SameUaNameDifferentPrimarySigs_ConvergesToCanonicalId()
    {
        // Pass 0 deterministic-id convergence. Five requests carrying the
        // GPTBot identity from different rotated signatures all collapse to a
        // single canonical fingerprint -- no centroid search, no threshold;
        // the canonical id is SHA256(verifiedbot:gptbot::ok)[..16].
        var vector = MakeUnitVector(_layout.Dimension, seed: 23);
        var fingerprintIds = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            var sig = $"gptbot-sig-{i:D2}";
            var extraSignals = new Dictionary<string, object>
            {
                [SignalKeys.UserAgentBotName] = "GPTBot",
                [SignalKeys.UserAgent] = "Mozilla/5.0 (compatible; GPTBot/1.0; +https://openai.com/gptbot)"
            };
            var fpId = await RunMatcherAsync(vector, sig, extraSignals);
            fingerprintIds.Add(fpId);
        }

        var distinct = fingerprintIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.True(distinct == 1,
            $"Verified-bot convergence broke: {distinct} fingerprints for 5 GPTBot requests. " +
            "TryConvergeOnNamedBotAsync should produce a deterministic canonical id.");
    }

    [Fact]
    public async Task RotationCandidateBand_VectorBetweenLooseAndMerge_SignalsCandidate()
    {
        // The rotation-band contract. Pre-seed a fingerprint with centroid V;
        // drive the matcher with a vector that mixes V with an orthogonal
        // direction so cosine lands strictly in [LooseThreshold=0.75,
        // MergeThreshold=0.92). The matcher should attach to the existing
        // fingerprint, record the observation, and emit IdentityRotationCandidate=true.
        var vectorBase = MakeOrthogonalVector(_layout.Dimension, hotSlotStart: 0, hotSlotCount: 8, seed: 31);
        var fp = MakeMatureFingerprint("fp-base", vectorBase);
        await _store.InsertFingerprintAsync(fp, primarySignature: "seed-base");

        // Blend base with an orthogonal direction so cosine ≈ 0.85, which sits
        // squarely in the rotation band. Hot windows must be within the layout
        // dimension (~98 dims for v1) or the second vector collapses to zero and
        // the blend becomes a scaled copy of the base (cosine = 1.0).
        var orthogonal = MakeOrthogonalVector(_layout.Dimension, hotSlotStart: 40, hotSlotCount: 8, seed: 37);
        var drift = BlendUnit(vectorBase, orthogonal, alpha: 0.85);

        var signalDict = new ConcurrentDictionary<string, object>();
        var fpId = await RunMatcherAsync(drift, primarySig: "drift-sig", extraSignals: null, signalSink: signalDict);

        Assert.Equal("fp-base", fpId);
        Assert.True(signalDict.TryGetValue(SignalKeys.IdentityRotationCandidate, out var flagObj),
            "Rotation-band match did not emit identity.rotation_candidate signal.");
        Assert.True(flagObj is bool b && b,
            $"identity.rotation_candidate should be true; was {flagObj}.");
    }

    // ===== Helpers =====

    /// <summary>
    ///     Drives the matcher's ContributeAsync end-to-end with a single vector
    ///     and primary signature. Returns the identity.fingerprint_id signal
    ///     written to the blackboard.
    /// </summary>
    private async Task<string> RunMatcherAsync(
        float[] vector,
        string primarySig,
        IReadOnlyDictionary<string, object>? extraSignals = null,
        ConcurrentDictionary<string, object>? signalSink = null)
    {
        var signals = signalSink ?? new ConcurrentDictionary<string, object>();
        signals[SignalKeys.PrimarySignature] = primarySig;
        signals[SignalKeys.IdentityVector] = vector;
        if (extraSignals is not null)
            foreach (var kv in extraSignals)
                signals[kv.Key] = kv.Value;

        var state = new BlackboardState
        {
            HttpContext = new DefaultHttpContext(),
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };

        await _matcher.ContributeAsync(state);
        Assert.True(signals.TryGetValue(SignalKeys.IdentityFingerprintId, out var fpObj),
            "Matcher did not emit identity.fingerprint_id.");
        return (string)fpObj!;
    }

    /// <summary>
    ///     Builds a unit-norm vector deterministic in <paramref name="seed"/>.
    ///     Brute-force cosine assumes L2-normalised inputs; the matcher's seed
    ///     centroid is the input vector itself when no archetype matched.
    /// </summary>
    private static float[] MakeUnitVector(int dim, int seed)
    {
        var rng = new Random(seed);
        var v = new float[dim];
        for (var i = 0; i < dim; i++) v[i] = (float)(rng.NextDouble() - 0.5);
        Normalize(v);
        return v;
    }

    /// <summary>
    ///     Vector with one shared chunk and one actor-specific orthogonal chunk.
    ///     Models a NAT scenario: every actor shares <c>network.*</c> values but
    ///     their identity dims project on disjoint hash buckets.
    /// </summary>
    private static float[] MakeNatVector(int actorIndex)
    {
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var v = new float[dim];
        // Shared network.* footprint -- first 12 dims (network slot block).
        for (var i = 0; i < 12; i++) v[i] = 0.5f;
        // Actor-specific identity-dim footprint -- a disjoint 8-dim window per
        // actor, well outside the network block. Different actors hit different
        // windows so the dot product across identity slots is zero.
        var hot = 20 + actorIndex * 16;
        for (var i = hot; i < hot + 8 && i < dim; i++) v[i] = 1.0f;
        Normalize(v);
        return v;
    }

    /// <summary>
    ///     Unit vector whose mass is concentrated on a single disjoint window of
    ///     <paramref name="hotSlotCount"/> dimensions starting at
    ///     <paramref name="hotSlotStart"/>. Two such vectors built on disjoint
    ///     windows are orthogonal (cosine = 0).
    /// </summary>
    private static float[] MakeOrthogonalVector(int dim, int hotSlotStart, int hotSlotCount, int seed)
    {
        var rng = new Random(seed);
        var v = new float[dim];
        for (var i = hotSlotStart; i < hotSlotStart + hotSlotCount && i < dim; i++)
            v[i] = 0.1f + (float)rng.NextDouble();
        Normalize(v);
        return v;
    }

    /// <summary>
    ///     Returns a unit vector that is a convex blend of <paramref name="a"/>
    ///     and <paramref name="b"/> such that cosine(result, a) ≈ alpha when a
    ///     and b are unit-orthogonal. Used to land cosine inside the rotation
    ///     band ([LooseThreshold, MergeThreshold)).
    /// </summary>
    private static float[] BlendUnit(float[] a, float[] b, double alpha)
    {
        if (a.Length != b.Length) throw new ArgumentException("Length mismatch");
        var beta = Math.Sqrt(Math.Max(0, 1 - alpha * alpha));
        var v = new float[a.Length];
        for (var i = 0; i < a.Length; i++)
            v[i] = (float)(alpha * a[i] + beta * b[i]);
        Normalize(v);
        return v;
    }

    private static void Normalize(float[] v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += v[i] * v[i];
        var norm = (float)Math.Sqrt(sum);
        if (norm <= 0) return;
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }

    /// <summary>
    ///     A fingerprint pre-seeded with a centroid, maturity > 1, uniform
    ///     weights, and a non-null cached verdict — enough for the matcher's
    ///     L1 confirm and Pass 2 candidate-load paths to operate against it.
    /// </summary>
    private static Fingerprint MakeMatureFingerprint(string id, float[] centroid)
    {
        var weights = new float[centroid.Length];
        Array.Fill(weights, 1.0f);
        var now = DateTime.UtcNow;
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = (float[])centroid.Clone(),
            CentroidMaturity = 5,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 5,
            CorrectionCount = 0,
            FirstSeen = now.AddHours(-1),
            LastSeen = now,
            Quality = 0.9,
            ArchetypeOrigin = null,
            InferredClientType = "test",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            CachedBotProbability = 0.5,
            CachedRiskBand = "Medium",
            CachedScoreUpdatedAt = now,
            DisplayName = id,
            DisplayNameUpdatedAt = now
        };
    }
}

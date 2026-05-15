using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Foundation Match-step contributor that runs the two-pass fingerprint match — Pass 1 point
///     lookup, Pass 2 vector cosine via <see cref="IIdentityAnchorIndex"/> — and writes the
///     resulting identity.* signals so every downstream consumer sees the same fingerprint id.
///
///     Initial implementation: allocation + match outcome signals. Per-fingerprint weight learning,
///     stability learning, drift verification, archetype seeding, and cached score updates land in
///     subsequent slices. The store schema and signal contract are stable; later slices add
///     producers, not new shapes.
///
///     Dormant unless <c>BotDetectionOptions.Identity.Enabled</c> is true.
///
///     See docs/architecture/fingerprint-match.md.
/// </summary>
public sealed class FingerprintMatchContributor : ContributingDetectorBase, IFoundationContributor
{
    private readonly ILogger<FingerprintMatchContributor> _logger;
    private readonly SqliteFingerprintStore _store;
    private readonly IIdentityAnchorIndex _index;
    private readonly IdentityArchetypeRegistry _archetypes;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;

    public FingerprintMatchContributor(
        ILogger<FingerprintMatchContributor> logger,
        SqliteFingerprintStore store,
        IIdentityAnchorIndex index,
        IdentityArchetypeRegistry archetypes,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _index = index;
        _archetypes = archetypes;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;
    }

    public override string Name => "FingerprintMatch";
    public override int Priority => 6; // After IdentityVector (5)
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();
    public override bool IsEnabled => _enabled;

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var primarySig = state.GetSignal<string>(SignalKeys.PrimarySignature);
        var vector = state.Signals.TryGetValue(SignalKeys.IdentityVector, out var vecObj) ? vecObj as float[] : null;
        if (string.IsNullOrEmpty(primarySig) || vector is null)
            return Array.Empty<DetectionContribution>();

        await _store.EnsureInitialisedAsync(cancellationToken);

        // Pass 1: point lookup
        var l1FingerprintId = await _store.LookupFingerprintIdAsync(primarySig, cancellationToken);
        Fingerprint? l1Candidate = l1FingerprintId is not null
            ? await _store.GetFingerprintAsync(l1FingerprintId, cancellationToken)
            : null;

        if (l1Candidate is not null)
        {
            // Quick confirm: weighted cosine against the candidate's centroid using its own weights.
            // (Global weights compose multiplicatively; until calibration runs, global is all-1.0.)
            var confirmScore = BruteForceIdentityAnchorIndex.WeightedCosine(
                vector, l1Candidate.Centroid, l1Candidate.Weights);
            if (confirmScore >= _options.Match.MergeThreshold)
            {
                EmitConfirmedSignals(state, l1Candidate, confirmScore, primarySig);
                await _store.RecordObservationAsync(l1Candidate.FingerprintId, vector, cancellationToken);
                return Array.Empty<DetectionContribution>();
            }
            // L1 confirm failed; fall through to Pass 2.
            state.WriteSignal(SignalKeys.IdentityFingerprintL1, l1FingerprintId!);
        }

        // Pass 2: vector search
        var candidates = await _index.SearchAsync(vector, _options.Match.TopK, cancellationToken);

        Fingerprint? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            var fp = await _store.GetFingerprintAsync(c.FingerprintId, cancellationToken);
            if (fp is null) continue;
            var centroidScore = BruteForceIdentityAnchorIndex.WeightedCosine(vector, fp.Centroid, fp.Weights);
            var score = Math.Max(centroidScore, c.BestObsScore);
            if (score > bestScore)
            {
                bestScore = score;
                best = fp;
            }
        }

        if (best is not null && bestScore >= _options.Match.MergeThreshold)
        {
            // Match. If L1 had a different candidate, this is a correction: record the
            // differentiator, update Pass 2's weights toward dims that distinguished it from L1,
            // re-key fingerprint_keys to point at Pass 2's winner.
            var isCorrection = l1Candidate is not null
                && !string.Equals(l1Candidate.FingerprintId, best.FingerprintId, StringComparison.OrdinalIgnoreCase);
            EmitConfirmedSignals(state, best, bestScore, primarySig, isCorrection: isCorrection);
            await _store.RecordObservationAsync(best.FingerprintId, vector, cancellationToken);

            if (isCorrection)
            {
                var diff = IdentityWeightMath.ComputeDifferentiator(vector, l1Candidate!.Centroid, best.Centroid);
                IdentityWeightMath.ApplyCorrection(best.Weights, diff, _options.Weights.CorrectionLearningRate);
                IdentityWeightMath.RenormaliseAndClamp(best.Weights, _options.Weights.MinWeight, _options.Weights.MaxWeight);
                await _store.RecordCorrectionAsync(
                    state.RequestId, primarySig,
                    pass1FingerprintId: l1Candidate.FingerprintId,
                    pass2FingerprintId: best.FingerprintId,
                    differentiator: diff,
                    updatedPass2Weights: best.Weights,
                    cancellationToken);
                await _store.UpsertKeyAsync(primarySig, best.FingerprintId, cancellationToken);
            }
            else if (l1FingerprintId is null)
            {
                await _store.UpsertKeyAsync(primarySig, best.FingerprintId, cancellationToken);
            }
            return Array.Empty<DetectionContribution>();
        }

        if (best is not null && bestScore >= _options.Match.LooseThreshold)
        {
            // Rotation candidate band: assign to the candidate, observe-and-drift, signal it.
            EmitConfirmedSignals(state, best, bestScore, primarySig, rotationCandidate: true);
            await _store.RecordObservationAsync(best.FingerprintId, vector, cancellationToken);
            return Array.Empty<DetectionContribution>();
        }

        // No plausible existing identity: allocate a new fingerprint, seeded from the nearest
        // archetype. The archetype's centroid blends with the observation (mostly the obs);
        // its dimension_mask becomes the per-fingerprint weight vector.
        var newId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var dim = vector.Length;

        var nearestArchetype = _archetypes.FindNearest(vector);
        float[] seedCentroid;
        float[] seedWeights;
        string? archetypeOrigin = null;
        string inferredType = "unknown";
        double inferredConfidence = 0.0;

        // Per-dim identity weight prior: session.* dims (path_entropy, method_pattern,
        // request_rate, session_age, etc.) are inherently per-request noisy and can't carry
        // identity at allocation time. Until stability learning proves otherwise for THIS
        // fingerprint, weight them low so they don't dominate cosine. Identity-rich dims
        // (network, locale, header bag, transport) start at 1.0.
        seedWeights = new float[dim];
        for (var i = 0; i < dim; i++)
            seedWeights[i] = 1.0f;
        foreach (var slot in _store.Layout.Slots)
        {
            if (!slot.Name.StartsWith("session.", StringComparison.OrdinalIgnoreCase)) continue;
            for (var i = slot.Offset; i < slot.Offset + slot.Width; i++)
                seedWeights[i] = (float)_options.Weights.MinWeight;
        }

        if (nearestArchetype is not null)
        {
            // Light blend: 70% observation, 30% archetype prior.
            var arch = nearestArchetype.Archetype;
            seedCentroid = new float[dim];
            for (var i = 0; i < dim; i++)
                seedCentroid[i] = vector[i] * 0.7f + arch.Centroid[i] * 0.3f;

            // Add the archetype mask on top of the prior — dims the archetype asserts get
            // additional weight beyond uniform.
            for (var i = 0; i < dim; i++)
                seedWeights[i] += arch.DimensionMask[i];

            archetypeOrigin = arch.ArchetypeId;
            inferredType = arch.ArchetypeId;
            inferredConfidence = nearestArchetype.Score;
        }
        else
        {
            seedCentroid = (float[])vector.Clone();
        }

        IdentityWeightMath.RenormaliseAndClamp(
            seedWeights, _options.Weights.MinWeight, _options.Weights.MaxWeight);

        var newFp = new Fingerprint
        {
            FingerprintId = newId,
            Centroid = seedCentroid,
            CentroidMaturity = 1,
            Weights = seedWeights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = state.Signals.TryGetValue(SignalKeys.IdentityVectorQuality, out var qObj) && qObj is double q ? q : 0.0,
            ArchetypeOrigin = archetypeOrigin,
            InferredClientType = inferredType,
            InferredTypeConfidence = inferredConfidence,
            InferredTypeChangedAt = now,
            CachedBotProbability = 0.0,
            CachedRiskBand = null,
            CachedScoreUpdatedAt = null
        };
        await _store.InsertFingerprintAsync(newFp, primarySig, cancellationToken);

        state.WriteSignal(SignalKeys.IdentityFingerprintId, newId);
        state.WriteSignal(SignalKeys.IdentityIsNewFingerprint, true);
        state.WriteSignal(SignalKeys.IdentityMatchScore, 0.0);
        state.WriteSignal(SignalKeys.IdentityClientType, newFp.InferredClientType);
        state.WriteSignal(SignalKeys.IdentityClientTypeConfidence, newFp.InferredTypeConfidence);
        if (archetypeOrigin is not null)
            state.WriteSignal(SignalKeys.IdentityClientTypeOrigin, archetypeOrigin);

        return Array.Empty<DetectionContribution>();
    }

    private static void EmitConfirmedSignals(
        BlackboardState state,
        Fingerprint matched,
        double matchScore,
        string primarySignature,
        bool isCorrection = false,
        bool rotationCandidate = false)
    {
        state.WriteSignal(SignalKeys.IdentityFingerprintId, matched.FingerprintId);
        state.WriteSignal(SignalKeys.IdentityMatchScore, matchScore);
        state.WriteSignal(SignalKeys.IdentityIsNewFingerprint, false);
        state.WriteSignal(SignalKeys.IdentityIsCorrection, isCorrection);
        state.WriteSignal(SignalKeys.IdentityRotationCandidate, rotationCandidate);
        state.WriteSignal(SignalKeys.IdentityClientType, matched.InferredClientType);
        state.WriteSignal(SignalKeys.IdentityClientTypeConfidence, matched.InferredTypeConfidence);
        if (matched.ArchetypeOrigin is not null)
            state.WriteSignal(SignalKeys.IdentityClientTypeOrigin, matched.ArchetypeOrigin);
        state.WriteSignal(SignalKeys.IdentityCachedBotProbability, matched.CachedBotProbability);
        if (matched.CachedRiskBand is not null)
            state.WriteSignal(SignalKeys.IdentityCachedRiskBand, matched.CachedRiskBand);
    }
}

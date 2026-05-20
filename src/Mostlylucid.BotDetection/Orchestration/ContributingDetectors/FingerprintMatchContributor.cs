using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Foundation Match-step contributor that runs the two-pass fingerprint match — Pass 1 point
///     lookup, Pass 2 vector cosine via <see cref="IIdentityAnchorIndex"/> — and writes the
///     resulting identity.* signals so every downstream consumer sees the same fingerprint id.
///     Also persists per-fingerprint display names, weight learning, archetype seeding, and
///     drift-gated re-computes.
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
    private readonly IdentityGlobalWeightsCache _globalWeights;
    private readonly IdentityProcessingCoordinator _coordinator;
    private readonly IdentityVectorEncoder _encoder;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;

    public FingerprintMatchContributor(
        ILogger<FingerprintMatchContributor> logger,
        SqliteFingerprintStore store,
        IIdentityAnchorIndex index,
        IdentityArchetypeRegistry archetypes,
        IdentityGlobalWeightsCache globalWeights,
        IdentityProcessingCoordinator coordinator,
        IdentityVectorEncoder encoder,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _index = index;
        _archetypes = archetypes;
        _globalWeights = globalWeights;
        _coordinator = coordinator;
        _encoder = encoder;
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
        // Belt-and-braces: seed a deterministic fallback fp id immediately so the
        // "every request emits identity.fingerprint_id" contract holds even if the
        // core path silently returns through an as-yet-undiscovered branch or the
        // store I/O races with a concurrent /reset-identity (BDF rig integration
        // scenario). The success path's EmitConfirmedSignals or RunPass2InternalAsync
        // write overwrites this with the real id; failures and silent exits leave
        // the fallback in place so downstream joins never lose the row.
        SeedFallbackFingerprintId(state, primarySig);
        try
        {
            return await ContributeCoreAsync(state, primarySig, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Pipeline-level cancellation: let it propagate so the orchestrator records
            // the timeout normally; the seed id stays.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "FingerprintMatch threw mid-flight; fallback id remains: {Message}", ex.Message);
            throw;
        }
    }

    private async Task<IReadOnlyList<DetectionContribution>> ContributeCoreAsync(
        BlackboardState state,
        string? primarySig,
        CancellationToken cancellationToken)
    {
        var vector = state.Signals.TryGetValue(SignalKeys.IdentityVector, out var vecObj) ? vecObj as float[] : null;
        if (vector is null)
        {
            // Priority alone is not a sequencing barrier - the orchestrator runs all
            // ready detectors in a wave in parallel. IdentityVectorContributor (5) and
            // this contributor (6) both have empty trigger conditions, so they race in
            // Wave 0. Adding a trigger here would defer us past early-exit gates and
            // leave high-confidence-bot first requests with no fingerprint id; instead
            // self-compute the vector via the shared encoder and publish only if no one
            // beat us to it (TryAdd via TryGetValue gate so the race-winner's overwrite
            // doesn't waste a second Encode in the common path).
            vector = _encoder.Encode(IdentityVectorContributor.ComposeRawValues(state));
            if (!state.Signals.ContainsKey(SignalKeys.IdentityVector))
                state.WriteSignal(SignalKeys.IdentityVector, vector);
        }

        // PrimarySignature can be empty for header-sparse requests (curl with only
        // User-Agent and no Accept header, etc.) - SignatureContributor's
        // MultiFactorSignatureService skips the WriteSignal when the result is empty.
        // Without it the matcher would exit silently and the request would never get
        // an identity.fingerprint_id, breaking the BDF stability assertion. Derive a
        // stable fallback key from IP+UA so allocation + L1 lookup still work; the
        // key is opaque (used purely as the store's primary index), not a security
        // signature.
        if (string.IsNullOrEmpty(primarySig))
        {
            var ua = state.UserAgent;
            var ip = state.GetSignal<string>(SignalKeys.ClientIp) ?? string.Empty;
            if (string.IsNullOrEmpty(ua) && string.IsNullOrEmpty(ip))
                return Array.Empty<DetectionContribution>();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"fp-fallback:{ip}|{ua}"));
            primarySig = Convert.ToHexString(bytes);
            state.WriteSignal(SignalKeys.PrimarySignature, primarySig);
        }

        await _store.EnsureInitialisedAsync(cancellationToken);

        // Pass 1: point lookup
        var l1FingerprintId = await _store.LookupFingerprintIdAsync(primarySig, cancellationToken);
        Fingerprint? l1Candidate = l1FingerprintId is not null
            ? await _store.GetFingerprintAsync(l1FingerprintId, cancellationToken)
            : null;

        if (l1Candidate is not null)
        {
            // Quick confirm: weighted cosine against the candidate's centroid using its own
            // weights composed multiplicatively with the calibrated global weights (when loaded).
            var confirmComposed = _globalWeights.Compose(l1Candidate.Weights);
            var confirmScore = BruteForceIdentityAnchorIndex.WeightedCosine(
                vector, l1Candidate.Centroid, confirmComposed);
            if (confirmScore >= _options.Match.MergeThreshold)
            {
                EmitConfirmedSignals(state, vector, l1Candidate, confirmScore, primarySig);
                await _store.RecordObservationAsync(l1Candidate.FingerprintId, vector, cancellationToken);
                // Clean L1 confirm = non-ambiguity event; pulls EWMA toward 0.
                await BumpAmbiguityAsync(state, l1Candidate.FingerprintId, isAmbiguous: false, cancellationToken);
                return Array.Empty<DetectionContribution>();
            }
            // L1 confirm failed; fall through to Pass 2 — record this as an ambiguity event
            // even before Pass 2 runs (the confirm-fail itself is a boundary indicator).
            state.WriteSignal(SignalKeys.IdentityFingerprintL1, l1FingerprintId!);
            await BumpAmbiguityAsync(state, l1Candidate.FingerprintId, isAmbiguous: true, cancellationToken);
        }

        // Coordinator gating only when we have an L1 candidate to fall back to on shed.
        // First-time identities (no L1 binding) run Pass 2 inline — coalescing them would
        // emit no identity signals and we'd rather accept the duplicate-fp risk on
        // concurrent first-allocations (the loser becomes an orphan; no signal gap).
        if (l1Candidate is not null)
        {
            var (_, dispatchOutcome) = await _coordinator.RunAsync<bool>(
                fingerprintId: l1FingerprintId!,
                kind: IdentitySlowPathKind.Pass2Match,
                riskScore: l1Candidate.CachedBotProbability,
                operation: ct => RunPass2InternalAsync(state, vector, primarySig, l1Candidate, l1FingerprintId, ct),
                ct: cancellationToken);

            if (dispatchOutcome == SlowPathDispatchOutcome.Executed)
                return Array.Empty<DetectionContribution>();

            // Shed: fall back to the L1 candidate's identity verdict.
            state.WriteSignal(SignalKeys.IdentitySlowPathShed, dispatchOutcome.ToString());
            EmitConfirmedSignals(state, vector, l1Candidate, matchScore: 0.0, primarySig);
            return Array.Empty<DetectionContribution>();
        }

        // No L1: run inline. Concurrent allocations may produce duplicate fps (loser becomes
        // an orphan, fingerprint_keys upsert resolves to one of them), but every request
        // still gets identity signals.
        await RunPass2InternalAsync(state, vector, primarySig, null, null, cancellationToken);
        return Array.Empty<DetectionContribution>();
    }

    /// <summary>
    ///     The Pass 2 + match decision + write path, extracted so the slow-path coordinator
    ///     can serialise it per fingerprint and shed under pressure. Returns true when a
    ///     verdict was written; the dispatch outcome from the coordinator is the caller's
    ///     authoritative signal of whether this ran.
    /// </summary>
    private async Task<bool> RunPass2InternalAsync(
        BlackboardState state,
        float[] vector,
        string primarySig,
        Fingerprint? l1Candidate,
        string? l1FingerprintId,
        CancellationToken cancellationToken)
    {
        var candidates = await _index.SearchAsync(vector, _options.Match.TopK, cancellationToken);

        Fingerprint? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            var fp = await _store.GetFingerprintAsync(c.FingerprintId, cancellationToken);
            if (fp is null) continue;
            var composed = _globalWeights.Compose(fp.Weights);
            var centroidScore = BruteForceIdentityAnchorIndex.WeightedCosine(vector, fp.Centroid, composed);
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
            EmitConfirmedSignals(state, vector, best, bestScore, primarySig, isCorrection: isCorrection);
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
            // Correction = ambiguity event for the Pass 2 winner; clean Pass 2 match (no
            // L1 disagreement) is also recorded as an ambiguity event because reaching
            // Pass 2 at all means L1 didn't suffice.
            await BumpAmbiguityAsync(state, best.FingerprintId, isAmbiguous: true, cancellationToken);
            return true;
        }

        if (best is not null && bestScore >= _options.Match.LooseThreshold)
        {
            // Rotation candidate band: assign to the candidate, observe-and-drift, signal it.
            EmitConfirmedSignals(state, vector, best, bestScore, primarySig, rotationCandidate: true);
            await _store.RecordObservationAsync(best.FingerprintId, vector, cancellationToken);
            // Rotation-band match = ambiguity event by definition.
            await BumpAmbiguityAsync(state, best.FingerprintId, isAmbiguous: true, cancellationToken);
            return true;
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
            // Three families of per-navigation volatile dims need MinWeight at
            // allocation so the L1-confirm cosine survives normal cross-page
            // header drift (otherwise the matcher allocates a fresh fingerprint
            // on every page transition):
            //
            //   session.*                       - request-rate, age, path entropy,
            //                                     referer host family. All per-page.
            //   hdr.header_order_hash           - flips when a Referer is added /
            //                                     Upgrade-Insecure-Requests drops on
            //                                     the second page of a Firefox session.
            //   hdr.sec_fetch_pattern           - "none" on initial load, "same-origin"
            //                                     for subsequent same-origin navigations.
            //                                     Browser-determined, not actor-volatile.
            //   hdr.upgrade_insecure_requests   - emitted only on top-level navigations,
            //                                     absent on subsequent same-origin reqs.
            //
            // Stability learning re-weights these as the fingerprint matures.
            var isVolatile = slot.Name.StartsWith("session.", StringComparison.OrdinalIgnoreCase)
                          || slot.Name.Equals("hdr.header_order_hash", StringComparison.OrdinalIgnoreCase)
                          || slot.Name.Equals("hdr.sec_fetch_pattern", StringComparison.OrdinalIgnoreCase)
                          || slot.Name.Equals("hdr.upgrade_insecure_requests", StringComparison.OrdinalIgnoreCase);
            if (!isVolatile) continue;
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

        // Write the identity signals first so the name composer can read the archetype name +
        // drift signals it depends on.
        state.WriteSignal(SignalKeys.IdentityFingerprintId, newId);
        state.WriteSignal(SignalKeys.IdentityIsNewFingerprint, true);
        state.WriteSignal(SignalKeys.IdentityMatchScore, 0.0);
        state.WriteSignal(SignalKeys.IdentityClientType, inferredType);
        state.WriteSignal(SignalKeys.IdentityClientTypeConfidence, inferredConfidence);
        if (archetypeOrigin is not null)
            state.WriteSignal(SignalKeys.IdentityClientTypeOrigin, archetypeOrigin);

        WriteArchetypeSignals(state, vector, nearestArchetype?.Archetype, nearestArchetype?.Score ?? 0.0);

        // Compose the display name from the now-populated signals. The cold-state
        // Priority 4 fallback uses the fingerprint id when even the UA contributor
        // is silent.
        var displayName = FingerprintNameComposer.Compose(
            state.Signals,
            fingerprintId: newId,
            userAgent: state.UserAgent,
            previousName: null);

        // Don't persist a Priority-4 fallback ("analysing" / "unknown xxx"): next request
        // would see the empty DisplayName, Path 2+3 in EmitDisplayNameSignal would
        // recompose (potentially picking up the UA family this time), and the dashboard
        // would settle on a real name. Persisting "analysing" would short-circuit Path 1
        // and lock the visible name to the fallback until significant drift fired.
        var persistedDisplayName = FingerprintNameComposer.IsFallback(displayName) ? "" : displayName;

        // Invariant: one display name = one fingerprint. If a different fingerprint already
        // owns this composed name, the new one MUST be distinguished -- and the discriminator
        // has to come from what's actually different (ASN, country, IP /16), never a hash.
        // Try progressively-less-specific modifiers; if all collide, last-resort short fp-id
        // prefix guarantees uniqueness (and signals an unexpected state we can grep for).
        if (!string.IsNullOrEmpty(persistedDisplayName))
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var collisions = await _store.CountByDisplayNameAsync(persistedDisplayName, cancellationToken);
                if (collisions == 0) break;

                var modifier = attempt < 3
                    ? FingerprintNameComposer.BuildDistinctiveModifier(state.Signals, attempt)
                    : null;
                modifier ??= newId[..Math.Min(8, newId.Length)];

                persistedDisplayName = $"{displayName} ({modifier})";
                displayName = persistedDisplayName;
            }
        }

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
            CachedScoreUpdatedAt = null,
            DisplayName = persistedDisplayName,
            DisplayNameUpdatedAt = now
        };
        await _store.InsertFingerprintAsync(newFp, primarySig, cancellationToken);

        state.WriteSignal(SignalKeys.IdentityDisplayName, displayName);

        // New-fingerprint allocation = ambiguity event by definition (no L1 baseline matched).
        await BumpAmbiguityAsync(state, newId, isAmbiguous: true, cancellationToken);
        return true;
    }

    /// <summary>
    ///     Seed a deterministic fingerprint id at function entry so the
    ///     "every request emits identity.fingerprint_id" contract is unbreakable.
    ///     The id is keyed on the primary signature when available (stable per
    ///     surface) and on the request id otherwise, so downstream dashboard joins
    ///     bucket correctly even on contributor failure or silent exit. Any
    ///     successful match path overwrites this with the real id.
    /// </summary>
    private static void SeedFallbackFingerprintId(BlackboardState state, string? primarySig)
    {
        if (state.Signals.ContainsKey(SignalKeys.IdentityFingerprintId)) return;
        var seed = string.IsNullOrEmpty(primarySig)
            ? (state.RequestId ?? Guid.NewGuid().ToString("N"))
            : primarySig;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("fp-fallback:" + seed));
        var fallbackId = Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
        state.WriteSignal(SignalKeys.IdentityFingerprintId, fallbackId);
    }

    private void EmitConfirmedSignals(
        BlackboardState state,
        float[] vector,
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

        var drift = WriteArchetypeSignals(
            state, vector,
            _archetypes.TryGetById(matched.InferredClientType),
            matched.InferredTypeConfidence);
        EmitDisplayNameSignal(state, vector, matched, drift);
    }

    /// <summary>
    ///     Writes the archetype display-name signal and (when global weights are available)
    ///     the per-slot top-drift signals. Returns the drift result so callers can use it
    ///     for the significant-drift gate (e.g. <see cref="EmitDisplayNameSignal"/>). No-op
    ///     and returns null when no archetype matched.
    ///
    ///     The <paramref name="matchScore"/> gates name + drift emission via
    ///     <c>Match.MinArchetypeMatchScore</c>: sparse/synthetic vectors that happen to be
    ///     "nearest" to some archetype by noise shouldn't be labelled as that archetype. The
    ///     archetype still passes through for fingerprint-centroid seeding (the caller decides
    ///     whether to use it), but the visible name signal stays absent below threshold —
    ///     the composer falls through to UA family + OS characterization instead.
    /// </summary>
    private DriftResult? WriteArchetypeSignals(
        BlackboardState state, float[] vector, IdentityArchetype? archetype, double matchScore)
    {
        if (archetype is null) return null;
        if (matchScore < _options.Match.MinArchetypeMatchScore) return null;

        state.WriteSignal(SignalKeys.IdentityArchetypeName, archetype.Name);
        if (!string.IsNullOrEmpty(archetype.Description))
            state.WriteSignal(SignalKeys.IdentityArchetypeDescription, archetype.Description);

        var weights = _globalWeights.Current
            ?? Enumerable.Repeat(1.0f, vector.Length).ToArray();
        var drift = IdentityWeightMath.TopDriftSlot(vector, archetype.Centroid, weights, _store.Layout);
        if (drift is not null && drift.Value.Score > _options.Match.DriftEpsilon)
        {
            state.WriteSignal(SignalKeys.IdentityDriftTopSlot, drift.Value.SlotName);
            state.WriteSignal(SignalKeys.IdentityDriftTopCategory, drift.Value.Category);
            state.WriteSignal(SignalKeys.IdentityDriftTopScore, drift.Value.Score);
        }
        return drift;
    }

    /// <summary>
    ///     Writes <see cref="SignalKeys.IdentityDisplayName"/> for a matched fingerprint.
    ///     Three paths:
    ///     <list type="number">
    ///         <item><c>matched.DisplayName</c> is non-empty: write the persisted name
    ///             directly. Most matches take this path — names are stable.</item>
    ///         <item><c>matched.DisplayName</c> is empty (row migrated from before the
    ///             column existed): compose from current signals + lazy-backfill persist
    ///             (fire-and-forget — don't block the request on the write).</item>
    ///         <item>Drift score exceeds <c>Match.SignificantDriftEpsilon</c> AND the
    ///             recomposed name differs from the persisted one: significant behavioural
    ///             drift, update the persisted name + write the new signal. Per-request
    ///             <c>DriftEpsilon</c> (0.05) gates the drift-label emission;
    ///             <c>SignificantDriftEpsilon</c> (0.20, 4x) gates the name update so float
    ///             noise doesn't churn names.</item>
    ///     </list>
    /// </summary>
    private void EmitDisplayNameSignal(
        BlackboardState state, float[] vector, Fingerprint matched,
        DriftResult? drift)
    {
        // Path 1: stable persisted name, no significant drift.
        if (!string.IsNullOrEmpty(matched.DisplayName)
            && (drift is null || drift.Value.Score <= _options.Match.SignificantDriftEpsilon))
        {
            state.WriteSignal(SignalKeys.IdentityDisplayName, matched.DisplayName);
            return;
        }

        // Path 2 + 3: compose a fresh name from current signals. Pass matched.DisplayName
        // as previousName for hysteresis (Compose returns previousName if the fresh result
        // would be a Priority-4 fallback - stops "Chrome" → "analysing" → "Chrome" churn
        // when signal presence varies request-to-request).
        var freshName = FingerprintNameComposer.Compose(
            state.Signals,
            fingerprintId: matched.FingerprintId,
            userAgent: state.UserAgent,
            previousName: string.IsNullOrEmpty(matched.DisplayName) ? null : matched.DisplayName);
        state.WriteSignal(SignalKeys.IdentityDisplayName, freshName);

        // Persist if: row had no display name AND we now have a real one (avoid persisting
        // fallbacks - see allocation path comment) OR significant drift produced a different
        // real name. Hysteresis already prevents fresh from being worse than matched, so
        // any string-difference here means an upgrade or a meaningful drift change.
        var freshIsFallback = FingerprintNameComposer.IsFallback(freshName);
        var shouldPersist = !freshIsFallback && (
            string.IsNullOrEmpty(matched.DisplayName)
            || (drift is not null && drift.Value.Score > _options.Match.SignificantDriftEpsilon
                && !string.Equals(freshName, matched.DisplayName, StringComparison.Ordinal)));
        if (shouldPersist)
        {
            // Fire-and-forget. Consistent with how other matcher writes (RecordObservationAsync
            // when called from EmitConfirmedSignals indirectly) avoid blocking the request path.
            _ = _store.UpdateDisplayNameAsync(matched.FingerprintId, freshName, DateTime.UtcNow, CancellationToken.None);
        }
    }

    /// <summary>
    ///     Bumps the per-fingerprint ambiguity-persistence EWMA and emits
    ///     <see cref="SignalKeys.IdentityAmbiguityPersistence"/>. When the post-bump value
    ///     crosses the configured threshold, also emits
    ///     <see cref="SignalKeys.IdentityAmbiguityProbing"/> as a positive bot signal —
    ///     the boundary-prober defence's diagnostic output. The bump SQL is atomic
    ///     (UPDATE ... RETURNING) so concurrent writers cannot lose updates.
    /// </summary>
    private async Task BumpAmbiguityAsync(
        BlackboardState state,
        string fingerprintId,
        bool isAmbiguous,
        CancellationToken ct)
    {
        try
        {
            var newValue = await _store.BumpAmbiguityPersistenceAsync(
                fingerprintId, isAmbiguous, _options.Drift.AmbiguityEwmaAlpha, ct);
            state.WriteSignal(SignalKeys.IdentityAmbiguityPersistence, newValue);
            if (newValue >= _options.Drift.AmbiguityProbingThreshold)
                state.WriteSignal(SignalKeys.IdentityAmbiguityProbing, true);
        }
        catch (Exception ex)
        {
            // The ambiguity meta-signal is best-effort — never let its failure break the
            // matcher's primary contract (emit identity signals).
            _logger.LogWarning(ex, "Ambiguity persistence bump failed for fingerprint {Id}", fingerprintId);
        }
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
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
    private readonly IFingerprintStore _store;
    private readonly IIdentityAnchorIndex _index;
    private readonly IdentityArchetypeRegistry _archetypes;
    private readonly IdentityGlobalWeightsCache _globalWeights;
    private readonly IdentityProcessingCoordinator _coordinator;
    private readonly IdentityVectorEncoder _encoder;
    private readonly IFingerprintBrowserModeStore _modeStore;
    private readonly IBrowserModeResolver _modeResolver;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;
    private readonly bool _modeAbsorbEnabled;

    public FingerprintMatchContributor(
        ILogger<FingerprintMatchContributor> logger,
        IFingerprintStore store,
        IIdentityAnchorIndex index,
        IdentityArchetypeRegistry archetypes,
        IdentityGlobalWeightsCache globalWeights,
        IdentityProcessingCoordinator coordinator,
        IdentityVectorEncoder encoder,
        IFingerprintBrowserModeStore modeStore,
        IBrowserModeResolver modeResolver,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _index = index;
        _archetypes = archetypes;
        _globalWeights = globalWeights;
        _coordinator = coordinator;
        _encoder = encoder;
        _modeStore = modeStore;
        _modeResolver = modeResolver;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;
        _modeAbsorbEnabled = _options.Enabled && _options.BrowserMode.Enabled;
    }

    public override string Name => "FingerprintMatch";
    // Priority 6 (after IdentityVector at 5). Earlier work moved this to
    // Priority 1 to fix the dashboard's "Calibrating" render for verdict-cached
    // visitors -- but doing so let the matcher's verdict signals (archetype kind,
    // cached score, client type) land before the bot-flagging detectors, which
    // for borderline-but-bot scenarios (e.g. "Chrome with missing browser
    // headers") biased the aggregate hard toward human (score 0.05 vs the
    // scenario-expected 0.6, see test-suites/bots/07-missing-browser-headers).
    // The dashboard issue needs to be fixed by splitting "allocate a fingerprint
    // early" from "emit verdict signals late" -- not by moving the whole
    // contributor. Tracked in MEMORY: project_bdf_replay_regression.
    public override int Priority => 6;
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

        // Pass 0: verified-bot convergence. When the UA carries a known bot identity, every
        // request with that identity belongs to the SAME conceptual fingerprint -- one
        // GPTBot fingerprint, one AmazonBot fingerprint, etc., regardless of which source IP
        // / signature the individual request landed on. Without this, every new IP from
        // Meta's pool spawns its own fingerprint and the dashboard renders eight rows for
        // the same Meta-ExternalAgent identity. The deterministic id is keyed on
        // (name, instance discriminator, spoof flag) so:
        //   - mastodon.social and mas.to (same UA, different +URL) stay distinct;
        //   - a spoofed AmazonBot routes to its own fingerprint, not the legit one.
        if (await TryConvergeOnNamedBotAsync(state, vector, primarySig, cancellationToken))
            return Array.Empty<DetectionContribution>();

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
                await EmitPostObservationSignalsAsync(state, l1Candidate.ObservationCount + 1, l1Candidate.CentroidMaturity, cancellationToken);
                await AbsorbIntoBrowserModeAsync(state, l1Candidate.FingerprintId, vector, cancellationToken);
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
            await AbsorbIntoBrowserModeAsync(state, l1Candidate.FingerprintId, vector, cancellationToken);
            return Array.Empty<DetectionContribution>();
        }

        // No L1: run inline. Concurrent allocations may produce duplicate fps (loser becomes
        // an orphan, fingerprint_keys upsert resolves to one of them), but every request
        // still gets identity signals.
        await RunPass2InternalAsync(state, vector, primarySig, null, null, cancellationToken);
        return Array.Empty<DetectionContribution>();
    }

    /// <summary>
    ///     When the User-Agent identifies a known bot (UA classifier set <c>ua.bot_name</c>),
    ///     converge every request from that bot identity onto a single fingerprint keyed off
    ///     the canonical name. Without this, every new source IP from a verified bot's pool
    ///     (Meta's crawlers, AWS Lambda, GPTBot, etc.) produces a fresh primary_signature, a
    ///     fresh L1 miss, and a fresh fingerprint allocation -- so the dashboard ends up with
    ///     N identical "Meta-ExternalAgent" rows for the same actual bot.
    ///
    ///     The deterministic id factors in <see cref="UserAgentDiscriminator"/> (so
    ///     mastodon.social and mas.to stay distinct) and the spoof flag (so a UA-claiming-
    ///     AmazonBot from a non-Amazon ASN gets its own fingerprint, never the real one).
    ///     Returns true when the fast path handled this request -- the caller must NOT then
    ///     run the vector-based passes.
    /// </summary>
    private async Task<bool> TryConvergeOnNamedBotAsync(
        BlackboardState state, float[] vector, string primarySig, CancellationToken ct)
    {
        var botName = state.GetSignal<string>(SignalKeys.UserAgentBotName);
        if (string.IsNullOrEmpty(botName) || botName == "unknown")
            return false;

        var rawUa = state.GetSignal<string>(SignalKeys.UserAgent) ?? state.UserAgent;
        var discriminator = UserAgentDiscriminator.ExtractDiscriminator(rawUa) ?? string.Empty;
        var spoofed = (state.GetSignal<bool?>(SignalKeys.VerifiedBotSpoofed) ?? false)
                      || (state.GetSignal<bool?>(SignalKeys.VerifiedBotRdnsMismatch) ?? false);

        // Deterministic id: collisions on (botName, discriminator, spoofed) converge by design.
        var canonical = $"verifiedbot:{botName.ToLowerInvariant()}:{discriminator.ToLowerInvariant()}:{(spoofed ? "spoof" : "ok")}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var idBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));
        var canonicalId = Convert.ToHexString(idBytes, 0, 16).ToLowerInvariant();

        var existing = await _store.GetFingerprintAsync(canonicalId, ct);
        if (existing is not null)
        {
            // Already seen this identity. Bind this primary_signature so future L1 lookups
            // skip even the SHA + GetFingerprint call; record the observation; emit signals.
            await _store.UpsertKeyAsync(primarySig, canonicalId, ct);
            await _store.RecordObservationAsync(canonicalId, vector, ct);
            await AbsorbIntoBrowserModeAsync(state, canonicalId, vector, ct);
            EmitConfirmedSignals(state, vector, existing, matchScore: 1.0, primarySig);
            await BumpAmbiguityAsync(state, canonicalId, isAmbiguous: false, ct);
            return true;
        }

        // First sighting. Allocate the deterministic-id fingerprint. Centroid = this vector
        // (subsequent observations EWMA it), weights uniform (we don't need behavioural
        // discrimination -- the UA name IS the identity).
        var dim = vector.Length;
        var seedWeights = new float[dim];
        for (var i = 0; i < dim; i++) seedWeights[i] = 1.0f;
        var displayName = string.IsNullOrEmpty(discriminator) ? botName : $"{botName} {discriminator}";
        if (spoofed) displayName += FingerprintNameComposer.SpoofedMarker;

        var now = DateTime.UtcNow;
        var fp = new Fingerprint
        {
            FingerprintId = canonicalId,
            Centroid = vector,
            CentroidMaturity = 1,
            Weights = seedWeights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = state.Signals.TryGetValue(SignalKeys.IdentityVectorQuality, out var qObj) && qObj is double q ? q : 1.0,
            ArchetypeOrigin = $"verifiedbot:{botName.ToLowerInvariant()}",
            InferredClientType = spoofed ? "suspicious" : "bot",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            CachedBotProbability = spoofed ? 0.95 : 0.85,
            CachedRiskBand = spoofed ? "VeryHigh" : "Medium",
            CachedScoreUpdatedAt = now,
            DisplayName = displayName,
            DisplayNameUpdatedAt = now,
            // Verifiedbot path has no archetype centroid to anchor against (the UA
            // name IS the identity); self-seed the root from the live vector so the
            // row has a non-null root from row 1. Drift starts at zero and grows if
            // the verified bot's behavioural shape ever moves.
            RootCentroid = (float[])vector.Clone(),
            RootCentroidAt = now,
            RootSource = $"verifiedbot:{botName.ToLowerInvariant()}",
        };
        await _store.InsertFingerprintAsync(fp, primarySig, ct);
        await AbsorbIntoBrowserModeAsync(state, canonicalId, vector, ct);
        EmitConfirmedSignals(state, vector, fp, matchScore: 1.0, primarySig);
        await BumpAmbiguityAsync(state, canonicalId, isAmbiguous: false, ct);
        return true;
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
            await EmitPostObservationSignalsAsync(state, best.ObservationCount + 1, best.CentroidMaturity, cancellationToken);
            await AbsorbIntoBrowserModeAsync(state, best.FingerprintId, vector, cancellationToken);

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
            await EmitPostObservationSignalsAsync(state, best.ObservationCount + 1, best.CentroidMaturity, cancellationToken);
            await AbsorbIntoBrowserModeAsync(state, best.FingerprintId, vector, cancellationToken);
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

        // FindNearest considers ALL archetypes (including mode-shaped ones like
        // chrome-xhr) because the matcher needs a per-mode prior to keep a real
        // Chrome XHR from drifting to googlebot at allocation. But the IDENTITY
        // surface (archetypeOrigin + display name + drift origin-vs-current
        // comparison) must consult the client-only view so "Chrome XHR" never
        // appears as the client identity and "Chrome Desktop -> Chrome XHR"
        // never appears as a drift banner. Per the composite-browser-mode-
        // fingerprints spec, mode shifts are orthogonal to identity drift.
        var nearestArchetype = _archetypes.FindNearest(vector);
        var nearestClient = nearestArchetype is { Archetype.IsMode: true }
            ? _archetypes.FindNearestClient(vector)
            : nearestArchetype;
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
            // Centroid blend uses the OVERALL nearest (may be a mode) so the
            // seed prior continues to anchor mode-shaped requests correctly.
            var arch = nearestArchetype.Archetype;
            seedCentroid = new float[dim];
            for (var i = 0; i < dim; i++)
                seedCentroid[i] = vector[i] * 0.7f + arch.Centroid[i] * 0.3f;

            // Add the archetype mask on top of the prior — dims the archetype asserts get
            // additional weight beyond uniform.
            for (var i = 0; i < dim; i++)
                seedWeights[i] += arch.DimensionMask[i];

            // archetypeOrigin + inferredType drive the dashboard's CLIENT identity
            // ("what is this visitor"). They MUST point at a client archetype, never
            // a mode archetype -- otherwise the row reads "Chrome XHR" instead of
            // "Chrome Desktop". When the overall nearest IS a mode, fall back to
            // the nearest client archetype (which the matcher already scored
            // above). The mode itself is captured separately by the browser-mode
            // resolver / classifier; nothing identity-shaped reads it.
            var identityArch = (nearestClient ?? nearestArchetype).Archetype;
            archetypeOrigin = identityArch.ArchetypeId;
            inferredType = identityArch.ArchetypeId;
            inferredConfidence = (nearestClient ?? nearestArchetype).Score;
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
        state.WriteSignal(SignalKeys.IdentityFingerprintFirstSeen, true);
        state.WriteSignal(SignalKeys.IdentityMatchScore, 0.0);
        state.WriteSignal(SignalKeys.IdentityClientType, inferredType);
        state.WriteSignal(SignalKeys.IdentityClientTypeConfidence, inferredConfidence);
        if (archetypeOrigin is not null)
            state.WriteSignal(SignalKeys.IdentityClientTypeOrigin, archetypeOrigin);

        // WriteArchetypeSignals emits IdentityArchetypeName which feeds the
        // dashboard's drift "Origin -> Current" banner. Pass the CLIENT-only
        // nearest so the banner compares client archetypes -- mode-shaped
        // archetypes never appear in the identity display surface.
        var displayArchetype = (nearestClient ?? nearestArchetype);
        WriteArchetypeSignals(state, vector, displayArchetype?.Archetype, displayArchetype?.Score ?? 0.0);

        // Compose the display name from the now-populated signals. Returns null when no
        // usable signal is available yet -- we leave the persisted name blank in that case
        // and the dashboard's render layer synthesises a descriptive label from the row's
        // current threat/behaviour signals. Avoids ever writing "analysing" anywhere.
        var displayName = FingerprintNameComposer.Compose(
            state.Signals,
            userAgent: state.UserAgent,
            previousName: null);

        // Invariant: one display name = one fingerprint. If a different fingerprint already
        // owns this composed name, the new one MUST be distinguished -- and the discriminator
        // has to come from what's actually different (ASN, country, IP /16), never a hash.
        // Try progressively-less-specific modifiers; if all collide, last-resort short fp-id
        // prefix guarantees uniqueness (and signals an unexpected state we can grep for).
        if (!string.IsNullOrEmpty(displayName))
        {
            var baseName = displayName;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var collisions = await _store.CountByDisplayNameAsync(displayName, cancellationToken);
                if (collisions == 0) break;

                var modifier = attempt < 3
                    ? FingerprintNameComposer.BuildDistinctiveModifier(state.Signals, attempt)
                    : null;
                modifier ??= newId[..Math.Min(8, newId.Length)];

                displayName = $"{baseName} ({modifier})";
            }
        }

        var persistedDisplayName = displayName ?? "";

        // root_centroid = the archetype's centroid when one matched (the cold-start
        // root for the adaptation loop; replaced later by BotClusterService snapshots).
        // No-archetype fallback: anchor on the seed itself so the row always has a
        // root -- "null at request time is a bug" is enforced from allocation.
        var rootCentroid = nearestArchetype is not null
            ? (float[])nearestArchetype.Archetype.Centroid.Clone()
            : (float[])seedCentroid.Clone();
        var rootSource = archetypeOrigin is not null
            ? $"archetype:{archetypeOrigin}"
            : "bootstrap";

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
            DisplayNameUpdatedAt = now,
            RootCentroid = rootCentroid,
            RootCentroidAt = now,
            RootSource = rootSource,
        };
        await _store.InsertFingerprintAsync(newFp, primarySig, cancellationToken);
        await AbsorbIntoBrowserModeAsync(state, newId, vector, cancellationToken);

        // Compose returns null when there's no usable signal; downstream stays unset and
        // the render layer synthesises a descriptive label from threat/behaviour signals.
        if (!string.IsNullOrEmpty(displayName))
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

    /// <summary>
    ///     Append-only mode observation log. The matcher inserts one row per
    ///     absorb (no read, no merge); the <c>FingerprintModeAbsorptionService</c>
    ///     drains unabsorbed rows on its tick, computes the batched EWMA against
    ///     the cached mode centroid, and writes one UPSERT per (fingerprint_id,
    ///     mode_id) tuple per tick. This mirrors the parent's append-only
    ///     observation pattern and closes the read-modify-write race the
    ///     previous direct-UPSERT absorb opened on concurrent HTTP/2 sub-resource
    ///     fetches for the same Chrome session.
    ///
    ///     Emits the diagnostic <c>identity.browser_mode_*</c> signals against
    ///     the cached mode row's current state so downstream consumers see the
    ///     mode mix without waiting for the drainer tick. The matched mode's
    ///     maturity will lag by up to one tick on hot fingerprints — that's
    ///     acceptable for the mix-deviation axis (step 6 reads the eventual,
    ///     post-drain state from the cached mode row).
    ///
    ///     Failures here are caught and logged — they must never break the
    ///     matcher's primary contract.
    /// </summary>
    private async Task AbsorbIntoBrowserModeAsync(
        BlackboardState state,
        string fingerprintId,
        float[] vector,
        CancellationToken cancellationToken)
    {
        if (!_modeAbsorbEnabled || string.IsNullOrEmpty(fingerprintId)) return;

        try
        {
            var modeId = ResolveBrowserModeId(state);
            await _modeStore.RecordModeObservationAsync(fingerprintId, modeId, vector, cancellationToken);

            // Diagnostic signals reflect the current cached state (before this
            // observation is folded by the drainer). unseen flips true the
            // first time the matcher records a mode that hasn't appeared on
            // the fingerprint yet; subsequent requests for the same mode within
            // a drain window also report the cache's last-known maturity.
            var cached = await _modeStore.GetModeAsync(fingerprintId, modeId, cancellationToken);
            state.WriteSignal(SignalKeys.IdentityBrowserMode, modeId);
            if (cached is null)
            {
                state.WriteSignal(SignalKeys.IdentityBrowserModeUnseen, true);
                state.WriteSignal(SignalKeys.IdentityBrowserModeMaturity, 0);
            }
            else
            {
                state.WriteSignal(SignalKeys.IdentityBrowserModeMaturity, cached.CentroidMaturity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BrowserMode observation record failed for fp={Fp}; primary match path unaffected", fingerprintId);
        }
    }

    /// <summary>
    ///     Resolve the request's browser mode id. Prefer the
    ///     <c>identity.browser_mode</c> signal written by
    ///     <see cref="BrowserModeClassifierContributor"/>; fall back to the
    ///     request-scoped <see cref="IBrowserModeResolver"/> when the signal
    ///     hasn't landed yet (BrowserModeClassifierContributor lost its wave,
    ///     or this contributor is the first thing to ask). Composite spec
    ///     step 5 made the resolver request-cached, so a fallback call here
    ///     hits the same <see cref="HttpContext.Items"/> entry the
    ///     classifier and endpoint policy already populated — single source
    ///     of truth, no recomputation, no race.
    /// </summary>
    private string ResolveBrowserModeId(BlackboardState state)
    {
        var signal = state.GetSignal<string>(SignalKeys.IdentityBrowserMode);
        if (!string.IsNullOrEmpty(signal)) return signal;
        return _modeResolver.Resolve(state.HttpContext);
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

        // Defensive: mode-shaped archetypes (chrome-xhr today) MUST NOT reach
        // the identity-display surface. The allocate path already filters via
        // FindNearestClient, but the matched path looks up archetype by stored
        // InferredClientType which on legacy fingerprints can still be a mode
        // id. Swap to the client-only nearest so the dashboard never reads
        // "Chrome XHR" in the identity slot.
        if (archetype.IsMode)
        {
            var clientNearest = _archetypes.FindNearestClient(vector);
            if (clientNearest is null
                || clientNearest.Score < _options.Match.MinArchetypeMatchScore)
                return null;
            archetype = clientNearest.Archetype;
            matchScore = clientNearest.Score;
        }

        state.WriteSignal(SignalKeys.IdentityArchetypeName, archetype.Name);
        state.WriteSignal(SignalKeys.IdentityArchetypeKind, archetype.ArchetypeKind);
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

        // Path 2 + 3: compose a fresh name from current signals. Pass matched.DisplayName as
        // previousName so Compose can keep the existing real name when current signals haven't
        // yet produced one (matcher runs before UserAgentContributor).
        var freshName = FingerprintNameComposer.Compose(
            state.Signals,
            userAgent: state.UserAgent,
            previousName: string.IsNullOrEmpty(matched.DisplayName) ? null : matched.DisplayName);

        // Emit the signal only when there's a real name. Downstream sees null/missing and the
        // render layer synthesises a descriptive label from the row's threat / behaviour.
        if (!string.IsNullOrEmpty(freshName))
            state.WriteSignal(SignalKeys.IdentityDisplayName, freshName);

        // Persist when: row had no name AND we now have one, OR significant drift produced a
        // different real name. Hysteresis already keeps the previous name when fresh is null,
        // so any string-difference means a real upgrade or drift change.
        var shouldPersist = !string.IsNullOrEmpty(freshName) && (
            string.IsNullOrEmpty(matched.DisplayName)
            || (drift is not null && drift.Value.Score > _options.Match.SignificantDriftEpsilon
                && !string.Equals(freshName, matched.DisplayName, StringComparison.Ordinal)));
        if (shouldPersist)
        {
            // Fire-and-forget. Consistent with other matcher writes that avoid blocking the
            // request path. freshName is non-null inside this branch (checked above).
            _ = _store.UpdateDisplayNameAsync(matched.FingerprintId, freshName!, DateTime.UtcNow, CancellationToken.None);
        }
    }

    /// <summary>
    ///     After a successful <see cref="IFingerprintStore.RecordObservationAsync"/> call,
    ///     emits the observation-count-crossed and (on matched paths) maturity-crossed signals.
    ///     <para>
    ///     Observation count crossed: fires when the new durable count exactly matches one of
    ///     <see cref="IdentityVectorOptions.NotifyOnCountCrossings"/>. The caller computes
    ///     <paramref name="postObservationCount"/> as <c>fp.ObservationCount + 1</c> so no
    ///     extra SQLite SELECT is required on the hot path.
    ///     </para>
    ///     <para>
    ///     Maturity crossed: fires on every request where the matched fingerprint's centroid_maturity
    ///     exactly equals <see cref="IdentityVectorOptions.AbsorptionMaturityThreshold"/>. Idempotence
    ///     under repeated emissions is the subscriber's responsibility.
    ///     </para>
    /// </summary>
    private Task EmitPostObservationSignalsAsync(
        BlackboardState state,
        long postObservationCount,
        int centroidMaturity,
        CancellationToken ct)
    {
        try
        {
            var crossings = _options.Vector.NotifyOnCountCrossings;
            if (crossings is { Length: > 0 })
            {
                foreach (var threshold in crossings)
                {
                    if (postObservationCount == threshold)
                    {
                        state.WriteSignal(SignalKeys.IdentityFingerprintObservationCountCrossed, threshold);
                        break;
                    }
                }
            }

            if (centroidMaturity == _options.Vector.AbsorptionMaturityThreshold)
            {
                state.WriteSignal(SignalKeys.IdentityFingerprintMaturityCrossed, true);
            }
        }
        catch (Exception ex)
        {
            // Post-observation signals are best-effort; never break the matcher's primary contract.
            _logger.LogWarning(ex, "Post-observation signal emission failed");
        }
        return Task.CompletedTask;
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

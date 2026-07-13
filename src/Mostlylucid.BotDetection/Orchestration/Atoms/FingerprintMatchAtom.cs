using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Foundation ConstrainerAtom that runs the two-pass fingerprint match —
///     Pass 1 point lookup, Pass 2 vector cosine via
///     <see cref="IIdentityAnchorIndex"/> — and raises the resulting
///     identity.* signals so every downstream consumer sees the same
///     fingerprint id. Also persists per-fingerprint display names, weight
///     learning, archetype seeding, and drift-gated re-computes.
///     Native <see cref="IDetectorAtom"/> replacement for
///     <c>FingerprintMatchContributor</c>.
///     Dormant unless <c>BotDetectionOptions.Identity.Enabled</c> is true.
///     See <c>docs/architecture/fingerprint-match.md</c>.
/// </summary>
public sealed class FingerprintMatchAtom : DetectorAtomBase
{
    private readonly ILogger<FingerprintMatchAtom> _logger;
    private readonly IFingerprintStore _store;
    private readonly IIdentityAnchorIndex _index;
    private readonly IdentityArchetypeRegistry _archetypes;
    private readonly IdentityGlobalWeightsCache _globalWeights;
    private readonly IdentityProcessingCoordinator _coordinator;
    private readonly IdentityVectorEncoder _encoder;
    private readonly IFingerprintBrowserModeStore _modeStore;
    private readonly IBrowserModeResolver _modeResolver;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;
    private readonly bool _modeAbsorbEnabled;

    public FingerprintMatchAtom(
        ILogger<FingerprintMatchAtom> logger,
        IFingerprintStore store,
        IIdentityAnchorIndex index,
        IdentityArchetypeRegistry archetypes,
        IdentityGlobalWeightsCache globalWeights,
        IdentityProcessingCoordinator coordinator,
        IdentityVectorEncoder encoder,
        IFingerprintBrowserModeStore modeStore,
        IBrowserModeResolver modeResolver,
        IHttpContextAccessor httpContextAccessor,
        IOptions<BotDetectionOptions> options)
        : base(name: "FingerprintMatch", category: "Identity")
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
        _httpContextAccessor = httpContextAccessor;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;
        _modeAbsorbEnabled = _options.Enabled && _options.BrowserMode.Enabled;
    }

    // Priority 6 (after IdentityVectorAtom at 5). See the original contributor
    // for why moving this earlier biased the aggregate against borderline bots.
    public override int Priority => 6;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();
    public override bool IsEnabled => _enabled;

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var primarySig = sink.ReadHint(SignalKeys.PrimarySignature);
        // Belt-and-braces: seed a deterministic fallback fp id immediately so
        // the "every request emits identity.fingerprint_id" contract holds
        // even if the core path returns through an as-yet-undiscovered branch
        // or the store I/O races with a concurrent /reset-identity.
        SeedFallbackFingerprintId(context, sink, sessionId, primarySig);
        try
        {
            await DetectCoreAsync(context, sink, sessionId, primarySig, ct).ConfigureAwait(false);
            return None();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "FingerprintMatch threw mid-flight; fallback id remains: {Message}", ex.Message);
            throw;
        }
    }

    private async Task DetectCoreAsync(
        HttpContext context,
        SignalSink sink,
        string sessionId,
        string? primarySig,
        CancellationToken ct)
    {
        var vector = IdentityVectorAtom.TryGetVector(context);
        if (vector is null)
        {
            // Race: IdentityVectorAtom (5) and FingerprintMatchAtom (6) both
            // run in Wave 0 with empty trigger sets, so they race. Self-compute
            // via the shared encoder when the vector atom's slot is empty and
            // publish only if no one beat us to it.
            var raw = IdentityVectorAtom.ComposeRawValues(context, sink);
            vector = _encoder.Encode(raw);
            if (!context.Items.ContainsKey(IdentityVectorAtom.VectorKey))
                context.Items[IdentityVectorAtom.VectorKey] = vector;
        }

        // PrimarySignature can be empty for header-sparse requests. Derive a
        // stable fallback key from IP+UA so allocation + L1 lookup still work;
        // the key is opaque (store index only), not a security signature.
        if (string.IsNullOrEmpty(primarySig))
        {
            var ua = context.Request.Headers.UserAgent.ToString();
            var ip = sink.ReadHint(SignalKeys.ClientIp) ?? string.Empty;
            if (string.IsNullOrEmpty(ua) && string.IsNullOrEmpty(ip)) return;
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"fp-fallback:{ip}|{ua}"));
            primarySig = Convert.ToHexString(bytes);
            sink.Raise($"{SignalKeys.PrimarySignature}:{primarySig}", sessionId);
        }

        await _store.EnsureInitialisedAsync(ct).ConfigureAwait(false);

        // Pass 0: verified-bot convergence.
        if (await TryConvergeOnNamedBotAsync(context, sink, sessionId, vector, primarySig, ct).ConfigureAwait(false))
            return;

        // Pass 1: point lookup.
        var l1FingerprintId = await _store.LookupFingerprintIdAsync(primarySig, ct).ConfigureAwait(false);
        Fingerprint? l1Candidate = l1FingerprintId is not null
            ? await _store.GetFingerprintAsync(l1FingerprintId, ct).ConfigureAwait(false)
            : null;

        if (l1Candidate is not null)
        {
            var confirmComposed = _globalWeights.Compose(l1Candidate.Weights);
            var confirmScore = BruteForceIdentityAnchorIndex.WeightedCosine(
                vector, l1Candidate.Centroid, confirmComposed);
            if (confirmScore >= _options.Match.MergeThreshold)
            {
                EmitConfirmedSignals(context, sink, sessionId, vector, l1Candidate, confirmScore, primarySig);
                await _store.RecordObservationAsync(
                    ResolveRequestScope(context), l1Candidate.FingerprintId, vector,
                    ResolveObservedUaFamily(context, sink), ct).ConfigureAwait(false);
                EmitPostObservationSignals(sink, sessionId, l1Candidate.ObservationCount + 1, l1Candidate.CentroidMaturity);
                // "wrote once and never again" fix: this clean-L1 hot path previously returned
                // without ever re-composing the display name, so a fingerprint named with a
                // fallback ("Unknown ...", "No User-Agent") at allocation -- when its UA wasn't
                // yet visible (the matcher at Priority 6 fires before UserAgentContributor at
                // Priority 10, and a first request can legitimately carry no UA) -- stayed stuck
                // forever, even once later requests carried a real UA. Re-compose to upgrade a
                // stuck fallback to the real UA-derived name. Gated on the currently-displayed
                // name being a fallback so an already-named fingerprint pays nothing here.
                if (FingerprintNameComposer.IsFallback(FingerprintNameResolver.Resolve(l1Candidate)))
                    EmitInducedNameSignal(context, sink, sessionId, vector, l1Candidate, drift: null);
                await AbsorbIntoBrowserModeAsync(context, sink, sessionId, l1Candidate.FingerprintId, vector, ct).ConfigureAwait(false);
                await BumpAmbiguityAsync(sink, sessionId, l1Candidate.FingerprintId, isAmbiguous: false, ct).ConfigureAwait(false);
                return;
            }
            sink.Raise($"{SignalKeys.IdentityFingerprintL1}:{l1FingerprintId}", sessionId);
            await BumpAmbiguityAsync(sink, sessionId, l1Candidate.FingerprintId, isAmbiguous: true, ct).ConfigureAwait(false);
        }

        if (l1Candidate is not null)
        {
            var (_, dispatchOutcome) = await _coordinator.RunAsync<bool>(
                fingerprintId: l1FingerprintId!,
                kind: IdentitySlowPathKind.Pass2Match,
                riskScore: l1Candidate.CachedBotProbability,
                operation: cts => RunPass2InternalAsync(context, sink, sessionId, vector, primarySig, l1Candidate, l1FingerprintId, cts),
                ct: ct).ConfigureAwait(false);

            if (dispatchOutcome == SlowPathDispatchOutcome.Executed) return;

            // Shed: fall back to the L1 candidate's identity verdict.
            sink.Raise($"{SignalKeys.IdentitySlowPathShed}:{dispatchOutcome}", sessionId);
            EmitConfirmedSignals(context, sink, sessionId, vector, l1Candidate, matchScore: 0.0, primarySig);
            await AbsorbIntoBrowserModeAsync(context, sink, sessionId, l1Candidate.FingerprintId, vector, ct).ConfigureAwait(false);
            return;
        }

        // No L1: run inline.
        await RunPass2InternalAsync(context, sink, sessionId, vector, primarySig, null, null, ct).ConfigureAwait(false);
    }

    private async Task<bool> TryConvergeOnNamedBotAsync(
        HttpContext context, SignalSink sink, string sessionId,
        float[] vector, string primarySig, CancellationToken ct)
    {
        var botName = sink.ReadHint(SignalKeys.UserAgentBotName);
        if (string.IsNullOrEmpty(botName) || botName == "unknown")
            return false;

        var rawUa = sink.ReadHint(SignalKeys.UserAgent) ?? context.Request.Headers.UserAgent.ToString();
        var discriminator = UserAgentDiscriminator.ExtractDiscriminator(rawUa) ?? string.Empty;
        var spoofed = sink.ReadBoolHint(SignalKeys.VerifiedBotSpoofed)
                      || sink.ReadBoolHint(SignalKeys.VerifiedBotRdnsMismatch);

        var canonical = $"verifiedbot:{botName.ToLowerInvariant()}:{discriminator.ToLowerInvariant()}:{(spoofed ? "spoof" : "ok")}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var idBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));
        var canonicalId = Convert.ToHexString(idBytes, 0, 16).ToLowerInvariant();

        var existing = await _store.GetFingerprintAsync(canonicalId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            await _store.UpsertKeyAsync(primarySig, canonicalId, ct).ConfigureAwait(false);
            await _store.RecordObservationAsync(
                ResolveRequestScope(context), canonicalId, vector,
                ResolveObservedUaFamily(context, sink), ct).ConfigureAwait(false);
            await AbsorbIntoBrowserModeAsync(context, sink, sessionId, canonicalId, vector, ct).ConfigureAwait(false);
            EmitConfirmedSignals(context, sink, sessionId, vector, existing, matchScore: 1.0, primarySig);
            await BumpAmbiguityAsync(sink, sessionId, canonicalId, isAmbiguous: false, ct).ConfigureAwait(false);
            return true;
        }

        // First sighting: allocate the deterministic-id fingerprint.
        var dim = vector.Length;
        var seedWeights = new float[dim];
        for (var i = 0; i < dim; i++) seedWeights[i] = 1.0f;
        var displayName = string.IsNullOrEmpty(discriminator) ? botName : $"{botName} {discriminator}";
        if (spoofed) displayName += FingerprintNameComposer.SpoofedMarker;

        var now = DateTime.UtcNow;
        var quality = sink.ReadDoubleHint(SignalKeys.IdentityVectorQuality, 1.0);
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
            Quality = quality,
            ArchetypeOrigin = $"verifiedbot:{botName.ToLowerInvariant()}",
            InferredClientType = spoofed ? "suspicious" : "bot",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            CachedBotProbability = spoofed ? 0.95 : 0.85,
            CachedRiskBand = spoofed ? "VeryHigh" : "Medium",
            CachedScoreUpdatedAt = now,
            InducedName = displayName,
            InducedNameUpdatedAt = now,
            RootCentroid = (float[])vector.Clone(),
            RootCentroidAt = now,
            RootSource = $"verifiedbot:{botName.ToLowerInvariant()}",
        };
        await _store.InsertFingerprintAsync(fp, primarySig, ct).ConfigureAwait(false);
        await AbsorbIntoBrowserModeAsync(context, sink, sessionId, canonicalId, vector, ct).ConfigureAwait(false);
        EmitConfirmedSignals(context, sink, sessionId, vector, fp, matchScore: 1.0, primarySig);
        await BumpAmbiguityAsync(sink, sessionId, canonicalId, isAmbiguous: false, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RunPass2InternalAsync(
        HttpContext context, SignalSink sink, string sessionId,
        float[] vector, string primarySig,
        Fingerprint? l1Candidate, string? l1FingerprintId,
        CancellationToken ct)
    {
        var candidates = await _index.SearchAsync(vector, _options.Match.TopK, ct).ConfigureAwait(false);

        Fingerprint? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            var fp = await _store.GetFingerprintAsync(c.FingerprintId, ct).ConfigureAwait(false);
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
            var isCorrection = l1Candidate is not null
                && !string.Equals(l1Candidate.FingerprintId, best.FingerprintId, StringComparison.OrdinalIgnoreCase);
            EmitConfirmedSignals(context, sink, sessionId, vector, best, bestScore, primarySig, isCorrection: isCorrection);
            await _store.RecordObservationAsync(
                ResolveRequestScope(context), best.FingerprintId, vector,
                ResolveObservedUaFamily(context, sink), ct).ConfigureAwait(false);
            EmitPostObservationSignals(sink, sessionId, best.ObservationCount + 1, best.CentroidMaturity);
            await AbsorbIntoBrowserModeAsync(context, sink, sessionId, best.FingerprintId, vector, ct).ConfigureAwait(false);

            if (isCorrection)
            {
                var diff = IdentityWeightMath.ComputeDifferentiator(vector, l1Candidate!.Centroid, best.Centroid);
                IdentityWeightMath.ApplyCorrection(best.Weights, diff, _options.Weights.CorrectionLearningRate);
                IdentityWeightMath.RenormaliseAndClamp(best.Weights, _options.Weights.MinWeight, _options.Weights.MaxWeight);
                await _store.RecordCorrectionAsync(
                    sessionId, primarySig,
                    pass1FingerprintId: l1Candidate.FingerprintId,
                    pass2FingerprintId: best.FingerprintId,
                    differentiator: diff,
                    updatedPass2Weights: best.Weights,
                    ct).ConfigureAwait(false);
                await _store.UpsertKeyAsync(primarySig, best.FingerprintId, ct).ConfigureAwait(false);
            }
            else if (l1FingerprintId is null)
            {
                await _store.UpsertKeyAsync(primarySig, best.FingerprintId, ct).ConfigureAwait(false);
            }
            await BumpAmbiguityAsync(sink, sessionId, best.FingerprintId, isAmbiguous: true, ct).ConfigureAwait(false);
            return true;
        }

        if (best is not null && bestScore >= _options.Match.LooseThreshold)
        {
            EmitConfirmedSignals(context, sink, sessionId, vector, best, bestScore, primarySig, rotationCandidate: true);
            await _store.RecordObservationAsync(
                ResolveRequestScope(context), best.FingerprintId, vector,
                ResolveObservedUaFamily(context, sink), ct).ConfigureAwait(false);
            EmitPostObservationSignals(sink, sessionId, best.ObservationCount + 1, best.CentroidMaturity);
            await AbsorbIntoBrowserModeAsync(context, sink, sessionId, best.FingerprintId, vector, ct).ConfigureAwait(false);
            await BumpAmbiguityAsync(sink, sessionId, best.FingerprintId, isAmbiguous: true, ct).ConfigureAwait(false);
            return true;
        }

        // No plausible existing identity: allocate a new fingerprint.
        var newId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var dim = vector.Length;

        var rawVector = IdentityVectorAtom.TryGetRawVector(context);
        var observedUaFamily = ResolveObservedUaFamily(context, sink);
        var nearestArchetype = rawVector is not null
            ? _archetypes.FindNearestRaw(rawVector, observedUaFamily)
            : _archetypes.FindNearest(vector, observedUaFamily);
        var nearestClient = nearestArchetype is { Archetype.IsMode: true }
            ? _archetypes.FindNearestClient(vector, observedUaFamily)
            : nearestArchetype;
        float[] seedCentroid;
        float[] seedWeights;
        string? archetypeOrigin = null;
        string inferredType = "unknown";
        double inferredConfidence = 0.0;

        seedWeights = new float[dim];
        for (var i = 0; i < dim; i++) seedWeights[i] = 1.0f;
        foreach (var slot in _store.Layout.Slots)
        {
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
            var arch = nearestArchetype.Archetype;
            seedCentroid = new float[dim];
            for (var i = 0; i < dim; i++)
                seedCentroid[i] = vector[i] * 0.7f + arch.Centroid[i] * 0.3f;

            for (var i = 0; i < dim; i++)
                seedWeights[i] += arch.DimensionMask[i];

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

        sink.Raise($"{SignalKeys.IdentityFingerprintId}:{newId}", sessionId);
        sink.Raise($"{SignalKeys.IdentityIsNewFingerprint}:true", sessionId);
        sink.Raise($"{SignalKeys.IdentityFingerprintFirstSeen}:true", sessionId);
        sink.Raise($"{SignalKeys.IdentityMatchScore}:0", sessionId);
        sink.Raise($"{SignalKeys.IdentityClientType}:{inferredType}", sessionId);
        sink.Raise($"{SignalKeys.IdentityClientTypeConfidence}:{inferredConfidence.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        if (archetypeOrigin is not null)
            sink.Raise($"{SignalKeys.IdentityClientTypeOrigin}:{archetypeOrigin}", sessionId);

        var displayArchetype = (nearestClient ?? nearestArchetype);
        WriteArchetypeSignals(sink, sessionId, vector, displayArchetype?.Archetype, displayArchetype?.Score ?? 0.0);

        var signalsSnapshot = SnapshotSinkSignals(sink);
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var displayName = FingerprintNameComposer.Compose(
            signalsSnapshot,
            userAgent: userAgent,
            previousName: null);

        if (!string.IsNullOrEmpty(displayName))
        {
            var baseName = displayName;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var collisions = await _store.CountByDisplayNameAsync(displayName, ct).ConfigureAwait(false);
                if (collisions == 0) break;

                var modifier = attempt < 3
                    ? FingerprintNameComposer.BuildDistinctiveModifier(signalsSnapshot, attempt)
                    : null;
                modifier ??= newId[..Math.Min(8, newId.Length)];

                displayName = $"{baseName} ({modifier})";
            }
        }

        var persistedDisplayName = displayName ?? "";
        var rootCentroid = nearestArchetype is not null
            ? (float[])nearestArchetype.Archetype.Centroid.Clone()
            : (float[])seedCentroid.Clone();
        var rootSource = archetypeOrigin is not null
            ? $"archetype:{archetypeOrigin}"
            : "bootstrap";

        var quality = sink.ReadDoubleHint(SignalKeys.IdentityVectorQuality, 0.0);
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
            Quality = quality,
            ArchetypeOrigin = archetypeOrigin,
            InferredClientType = inferredType,
            InferredTypeConfidence = inferredConfidence,
            InferredTypeChangedAt = now,
            CachedBotProbability = 0.0,
            CachedRiskBand = null,
            CachedScoreUpdatedAt = null,
            InducedName = persistedDisplayName,
            InducedNameUpdatedAt = now,
            RootCentroid = rootCentroid,
            RootCentroidAt = now,
            RootSource = rootSource,
        };
        await _store.InsertFingerprintAsync(newFp, primarySig, ct).ConfigureAwait(false);
        await AbsorbIntoBrowserModeAsync(context, sink, sessionId, newId, vector, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(displayName))
            sink.Raise($"{SignalKeys.IdentityDisplayName}:{displayName}", sessionId);

        await BumpAmbiguityAsync(sink, sessionId, newId, isAmbiguous: true, ct).ConfigureAwait(false);
        return true;
    }

    private static void SeedFallbackFingerprintId(
        HttpContext context, SignalSink sink, string sessionId, string? primarySig)
    {
        if (sink.ReadHint(SignalKeys.IdentityFingerprintId) is not null) return;
        var seed = string.IsNullOrEmpty(primarySig)
            ? (sessionId ?? Guid.NewGuid().ToString("N"))
            : primarySig;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("fp-fallback:" + seed));
        var fallbackId = Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
        sink.Raise($"{SignalKeys.IdentityFingerprintId}:{fallbackId}", sessionId);
    }

    private async Task AbsorbIntoBrowserModeAsync(
        HttpContext context, SignalSink sink, string sessionId,
        string fingerprintId, float[] vector, CancellationToken ct)
    {
        if (!_modeAbsorbEnabled || string.IsNullOrEmpty(fingerprintId)) return;

        try
        {
            var modeId = ResolveBrowserModeId(context, sink);
            await _modeStore.RecordModeObservationAsync(
                ResolveRequestScope(context), fingerprintId, modeId, vector,
                ResolveObservedUaFamily(context, sink), ct).ConfigureAwait(false);

            var cached = await _modeStore.GetModeAsync(fingerprintId, modeId, ct).ConfigureAwait(false);
            sink.Raise($"{SignalKeys.IdentityBrowserMode}:{modeId}", sessionId);
            if (cached is null)
            {
                sink.Raise($"{SignalKeys.IdentityBrowserModeUnseen}:true", sessionId);
                sink.Raise($"{SignalKeys.IdentityBrowserModeMaturity}:0", sessionId);
            }
            else
            {
                sink.Raise($"{SignalKeys.IdentityBrowserModeMaturity}:{cached.CentroidMaturity}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BrowserMode observation record failed for fp={Fp}; primary match path unaffected", fingerprintId);
        }
    }

    private string ResolveBrowserModeId(HttpContext context, SignalSink sink)
    {
        var signal = sink.ReadHint(SignalKeys.IdentityBrowserMode);
        if (!string.IsNullOrEmpty(signal)) return signal;
        return _modeResolver.Resolve(context);
    }

    private static RequestScope ResolveRequestScope(HttpContext context)
    {
        if (context.Items.TryGetValue(HttpContextItemKeys.RequestScope, out var cached)
            && cached is RequestScope existing)
            return existing;
        return RequestScope.Unknown;
    }

    private static string? ResolveObservedUaFamily(HttpContext context, SignalSink sink)
    {
        var family = sink.ReadHint(SignalKeys.UserAgentFamily);
        if (string.IsNullOrEmpty(family) || string.Equals(family, "Other", StringComparison.OrdinalIgnoreCase))
            family = sink.ReadHint(SignalKeys.UserAgentBotName);

        if (string.IsNullOrEmpty(family))
        {
            var ua = context.Request.Headers.UserAgent.ToString();
            if (!string.IsNullOrEmpty(ua))
            {
                var parsed = UserAgentParser.Parse(ua).Family;
                if (!string.IsNullOrEmpty(parsed) && !string.Equals(parsed, "Other", StringComparison.OrdinalIgnoreCase))
                    family = parsed;
            }
        }

        return string.IsNullOrEmpty(family) ? null : family;
    }

    private void EmitConfirmedSignals(
        HttpContext context, SignalSink sink, string sessionId,
        float[] vector, Fingerprint matched, double matchScore,
        string primarySignature, bool isCorrection = false, bool rotationCandidate = false)
    {
        sink.Raise($"{SignalKeys.IdentityFingerprintId}:{matched.FingerprintId}", sessionId);
        sink.Raise($"{SignalKeys.IdentityMatchScore}:{matchScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.IdentityIsNewFingerprint}:false", sessionId);
        sink.Raise($"{SignalKeys.IdentityIsCorrection}:{(isCorrection ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.IdentityRotationCandidate}:{(rotationCandidate ? "true" : "false")}", sessionId);
        if (matched.InferredClientType is not null)
            sink.Raise($"{SignalKeys.IdentityClientType}:{matched.InferredClientType}", sessionId);
        sink.Raise($"{SignalKeys.IdentityClientTypeConfidence}:{matched.InferredTypeConfidence.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        if (matched.ArchetypeOrigin is not null)
            sink.Raise($"{SignalKeys.IdentityClientTypeOrigin}:{matched.ArchetypeOrigin}", sessionId);
        sink.Raise($"{SignalKeys.IdentityCachedBotProbability}:{matched.CachedBotProbability.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        if (matched.CachedRiskBand is not null)
            sink.Raise($"{SignalKeys.IdentityCachedRiskBand}:{matched.CachedRiskBand}", sessionId);

        var drift = WriteArchetypeSignals(
            sink, sessionId, vector,
            matched.InferredClientType is not null ? _archetypes.TryGetById(matched.InferredClientType) : null,
            matched.InferredTypeConfidence);
        EmitInducedNameSignal(context, sink, sessionId, vector, matched, drift);
    }

    private DriftResult? WriteArchetypeSignals(
        SignalSink sink, string sessionId, float[] vector,
        IdentityArchetype? archetype, double matchScore)
    {
        if (archetype is null) return null;
        if (matchScore < _options.Match.MinArchetypeMatchScore) return null;

        if (archetype.IsMode)
        {
            var clientNearest = _archetypes.FindNearestClient(vector);
            if (clientNearest is null
                || clientNearest.Score < _options.Match.MinArchetypeMatchScore)
                return null;
            archetype = clientNearest.Archetype;
            matchScore = clientNearest.Score;
        }

        sink.Raise($"{SignalKeys.IdentityArchetypeName}:{archetype.Name}", sessionId);
        sink.Raise($"{SignalKeys.IdentityArchetypeKind}:{archetype.ArchetypeKind}", sessionId);
        if (!string.IsNullOrEmpty(archetype.Description))
            sink.Raise($"{SignalKeys.IdentityArchetypeDescription}:{archetype.Description}", sessionId);

        var weights = _globalWeights.Current
            ?? Enumerable.Repeat(1.0f, vector.Length).ToArray();
        var drift = IdentityWeightMath.TopDriftSlot(vector, archetype.Centroid, weights, _store.Layout);
        if (drift is not null && drift.Value.Score > _options.Match.DriftEpsilon)
        {
            sink.Raise($"{SignalKeys.IdentityDriftTopSlot}:{drift.Value.SlotName}", sessionId);
            sink.Raise($"{SignalKeys.IdentityDriftTopCategory}:{drift.Value.Category}", sessionId);
            sink.Raise($"{SignalKeys.IdentityDriftTopScore}:{drift.Value.Score.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        }
        return drift;
    }

    private void EmitInducedNameSignal(
        HttpContext context, SignalSink sink, string sessionId,
        float[] vector, Fingerprint matched, DriftResult? drift)
    {
        var previousDisplayed = FingerprintNameResolver.Resolve(matched);
        var signalsSnapshot = SnapshotSinkSignals(sink);
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var freshName = FingerprintNameComposer.Compose(
            signalsSnapshot,
            userAgent: userAgent,
            previousName: string.IsNullOrEmpty(previousDisplayed) ? null : previousDisplayed);

        if (!string.IsNullOrEmpty(freshName))
            sink.Raise($"{SignalKeys.IdentityDisplayName}:{freshName}", sessionId);

        var shouldPersist = !string.IsNullOrEmpty(freshName)
            && !string.Equals(freshName, matched.InducedName, StringComparison.Ordinal);
        if (shouldPersist)
        {
            var newId = matched.FingerprintId;
            var baseName = freshName!;

            _ = Task.Run(async () =>
            {
                var displayName = baseName;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var collisions = await _store.CountByDisplayNameExcludingFingerprintAsync(
                        displayName, newId, CancellationToken.None).ConfigureAwait(false);
                    if (collisions == 0) break;

                    var modifier = attempt < 3
                        ? FingerprintNameComposer.BuildDistinctiveModifier(signalsSnapshot, attempt)
                        : null;
                    modifier ??= newId.Length >= 8 ? newId[..8] : newId;

                    displayName = $"{baseName} ({modifier})";
                }

                if (!string.Equals(displayName, matched.InducedName, StringComparison.Ordinal))
                {
                    var snapshotJson = FingerprintNameComposer.SerialiseProjectionSnapshot(signalsSnapshot);
                    await _store.UpdateInducedNameAsync(
                        newId, displayName, DateTime.UtcNow, CancellationToken.None,
                        signalSnapshotJson: snapshotJson).ConfigureAwait(false);
                }
            });
        }
    }

    private void EmitPostObservationSignals(
        SignalSink sink, string sessionId, long postObservationCount, int centroidMaturity)
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
                        sink.Raise($"{SignalKeys.IdentityFingerprintObservationCountCrossed}:{threshold}", sessionId);
                        break;
                    }
                }
            }

            if (centroidMaturity == _options.Vector.AbsorptionMaturityThreshold)
                sink.Raise($"{SignalKeys.IdentityFingerprintMaturityCrossed}:true", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-observation signal emission failed");
        }
    }

    private async Task BumpAmbiguityAsync(
        SignalSink sink, string sessionId, string fingerprintId, bool isAmbiguous, CancellationToken ct)
    {
        try
        {
            var newValue = await _store.BumpAmbiguityPersistenceAsync(
                fingerprintId, isAmbiguous, _options.Drift.AmbiguityEwmaAlpha, ct).ConfigureAwait(false);
            sink.Raise($"{SignalKeys.IdentityAmbiguityPersistence}:{newValue.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
            if (newValue >= _options.Drift.AmbiguityProbingThreshold)
                sink.Raise($"{SignalKeys.IdentityAmbiguityProbing}:true", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ambiguity persistence bump failed for fingerprint {Id}", fingerprintId);
        }
    }

    /// <summary>
    ///     Build an <see cref="IReadOnlyDictionary{TKey,TValue}"/> view of the
    ///     sink for consumers (FingerprintNameComposer) that still take the
    ///     dict-shape. Every string-payload signal survives as
    ///     <c>[key] = value_string</c>; presence-only signals land as
    ///     <c>[signal] = true</c>. Cheap wrapper; no O(n) allocations per
    ///     signal beyond the terminal dictionary.
    /// </summary>
    private static Dictionary<string, object> SnapshotSinkSignals(SignalSink sink)
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        var signals = sink.Sense(_ => true);
        foreach (var signal in signals)
        {
            var raw = signal.Signal;
            var colon = raw.IndexOf(':');
            if (colon <= 0)
            {
                dict.TryAdd(raw, true);
                continue;
            }
            dict.TryAdd(raw[..colon], raw[(colon + 1)..]);
        }
        return dict;
    }
}

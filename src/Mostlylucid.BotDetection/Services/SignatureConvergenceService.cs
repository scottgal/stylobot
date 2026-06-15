using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Periodic merge/split evaluator for signature families. Detects when
///     multiple signatures from the same IP should be grouped (UA rotation)
///     and when divergent members should be split from a family.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(EvaluationIntervalSeconds)</c> loop (default 15 s,
///         load-sensor-adaptive); now subscribes to
///         <see cref="TickCadence.Tick10s"/> and gates each
///         <see cref="RunEvaluation"/> pass on
///         "<see cref="PipelineLoadSensor.GetAdaptiveInterval"/>-stretched
///         interval since last run". Default cadence rounds 15 s up to 20 s
///         (every other tick); shorter configured intervals fire every tick.
///         The 20-second warm-up sleep on first start is dropped -- the first
///         tick lands within 10 s of boot, which is shorter than the old warm
///         period anyway.
///     </para>
/// </summary>
public sealed class SignatureConvergenceService : IDisposable
{
    private readonly ILogger<SignatureConvergenceService> _logger;
    private readonly SignatureConvergenceOptions _options;
    private readonly SignatureCoordinator _signatureCoordinator;

    // Cooldown: recently split signatures can't be re-merged for 5 minutes
    private readonly ConcurrentDictionary<string, DateTime> _splitCooldowns = new();

    private readonly PipelineLoadSensor? _loadSensor;
    private readonly ISessionVectorSearch? _vectorSearch;
    private readonly IDisposable? _subscription;
    private DateTime _lastRunUtc = DateTime.MinValue;
    private int _disposed;

    public SignatureConvergenceService(
        ILogger<SignatureConvergenceService> logger,
        IOptions<BotDetectionOptions> options,
        SignatureCoordinator signatureCoordinator,
        PipelineLoadSensor? loadSensor = null,
        ISessionVectorSearch? vectorSearch = null,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _logger = logger;
        _options = options.Value.SignatureConvergence;
        _signatureCoordinator = signatureCoordinator;
        _loadSensor = loadSensor;
        _vectorSearch = vectorSearch;

        // Optional so unit tests that drive RunEvaluation() directly (without
        // scheduling) keep working. Production DI passes the real coordinator.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "SignatureConvergenceService",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Fires every Tick10s; gates the
    ///     evaluation pass on the load-sensor-adapted EvaluationIntervalSeconds
    ///     so under steady-state the cadence honours configuration while under
    ///     pressure the sensor can stretch it. Public so tests can drive a
    ///     single beat deterministically.
    /// </summary>
    public Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return Task.CompletedTask;
        if (!_options.Enabled) return Task.CompletedTask;

        var baseInterval = TimeSpan.FromSeconds(Math.Max(1, _options.EvaluationIntervalSeconds));
        var adaptive = _loadSensor?.GetAdaptiveInterval(baseInterval) ?? baseInterval;
        if (_lastRunUtc != DateTime.MinValue &&
            now.UtcDateTime - _lastRunUtc < adaptive)
        {
            return Task.CompletedTask; // Not yet due.
        }

        try
        {
            RunEvaluation();
            _lastRunUtc = DateTime.UtcNow;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Error during convergence evaluation");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }

    internal void RunEvaluation()
    {
        // Clean up expired cooldowns
        var now = DateTime.UtcNow;
        foreach (var (key, expiry) in _splitCooldowns)
        {
            if (now > expiry)
                _splitCooldowns.TryRemove(key, out _);
        }

        // Build shared lookups once: IP merges and vector merges both need these.
        // Merges run before splits so newly-joined members are split correctly if divergent.
        var allBehaviors = _signatureCoordinator.GetAllBehaviors()
            .ToDictionary(b => b.Signature, StringComparer.OrdinalIgnoreCase);
        var ipIndex = _signatureCoordinator.GetIpIndex();

        var familiesCreated = EvaluateMerges(allBehaviors, ipIndex);
        familiesCreated += EvaluateVectorSimilarityMerges(allBehaviors);
        var familiesSplit = EvaluateSplits();

        var totalFamilies = _signatureCoordinator.GetAllFamilies().Count;
        if (familiesCreated > 0 || familiesSplit > 0)
        {
            _logger.LogInformation(
                "Convergence: created {Created} families, split {Split}, total {Total}",
                familiesCreated, familiesSplit, totalFamilies);
        }
        else
        {
            _logger.LogDebug("Convergence: no changes, {Total} families active", totalFamilies);
        }
    }

    private int EvaluateMerges(
        IReadOnlyDictionary<string, SignatureBehavior> allBehaviors,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ipIndex)
    {
        var created = 0;

        // Under load: process IP groups ordered by highest average bot probability first,
        // capped to worst offenders so CPU time goes where it matters most.
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> groups = ipIndex;
        var cap = _loadSensor?.GetWorstOffenderCap(ipIndex.Count);
        if (cap.HasValue && ipIndex.Count > cap.Value)
        {
            groups = ipIndex
                .OrderByDescending(kvp => kvp.Value
                    .Select(s => allBehaviors.TryGetValue(s, out var b) ? b.AverageBotProbability : 0)
                    .DefaultIfEmpty(0)
                    .Average())
                .Take(cap.Value);
        }

        foreach (var (ipHash, signatures) in groups)
        {
            if (signatures.Count < _options.MinSignaturesForMerge)
                continue;

            // Get behaviors for all signatures under this IP from the pre-built lookup
            var behaviors = new Dictionary<string, SignatureBehavior>();
            foreach (var sig in signatures)
            {
                if (allBehaviors.TryGetValue(sig, out var behavior) && behavior.RequestCount > 0)
                    behaviors[sig] = behavior;
            }

            if (behaviors.Count < _options.MinSignaturesForMerge)
                continue;

            // Check if all are already in the same family
            var existingFamilies = behaviors.Keys
                .Select(s => _signatureCoordinator.GetFamily(s))
                .Where(f => f != null)
                .Select(f => f!.FamilyId)
                .Distinct()
                .ToList();

            if (existingFamilies.Count == 1 &&
                behaviors.Keys.All(s => _signatureCoordinator.GetFamily(s) != null))
                continue; // Already all in the same family

            // Evaluate pairwise merge scores
            var sigList = behaviors.Keys.ToList();
            var bestCandidate = default(MergeCandidate?);

            for (var i = 0; i < sigList.Count; i++)
            {
                for (var j = i + 1; j < sigList.Count; j++)
                {
                    var sigA = sigList[i];
                    var sigB = sigList[j];

                    // Skip if on cooldown
                    var cooldownKey = GetCooldownKey(sigA, sigB);
                    if (_splitCooldowns.ContainsKey(cooldownKey))
                        continue;

                    // Skip if already in the same family
                    var famA = _signatureCoordinator.GetFamily(sigA);
                    var famB = _signatureCoordinator.GetFamily(sigB);
                    if (famA != null && famB != null && famA.FamilyId == famB.FamilyId)
                        continue;

                    var candidate = ComputeMergeScore(sigA, sigB, behaviors[sigA], behaviors[sigB]);
                    if (candidate.TotalScore >= _options.MergeScoreThreshold &&
                        (bestCandidate == null || candidate.TotalScore > bestCandidate.Value.TotalScore))
                    {
                        bestCandidate = candidate;
                    }
                }
            }

            if (bestCandidate.HasValue)
            {
                var c = bestCandidate.Value;

                var existingFamily = _signatureCoordinator.GetFamily(c.SignatureA) ??
                                    _signatureCoordinator.GetFamily(c.SignatureB);

                if (existingFamily != null)
                {
                    ExtendFamily(existingFamily, c.SignatureA, c.SignatureB);
                }
                else
                {
                    // Create new family
                    if (_signatureCoordinator.GetAllFamilies().Count >= _options.MaxFamilies)
                        continue;

                    var members = new HashSet<string> { c.SignatureA, c.SignatureB };
                    // Also add any other signatures from this IP that pass the threshold
                    foreach (var sig in sigList)
                    {
                        if (members.Contains(sig)) continue;
                        var score1 = ComputeMergeScore(c.SignatureA, sig, behaviors[c.SignatureA], behaviors[sig]);
                        if (score1.TotalScore >= _options.MergeScoreThreshold)
                            members.Add(sig);
                    }

                    var canonical = DetermineCanonicalSignature(members, behaviors);
                    var reason = DetermineFormationReason(c);

                    var family = new SignatureFamily
                    {
                        FamilyId = ComputeFamilyId(members),
                        CanonicalSignature = canonical,
                        MemberSignatures = SignatureFamily.CreateMemberSet(members),
                        CreatedUtc = DateTime.UtcNow,
                        LastEvaluatedUtc = DateTime.UtcNow,
                        FormationReason = reason,
                        MergeConfidence = c.TotalScore,
                        EvaluationCount = 1
                    };

                    _signatureCoordinator.RegisterFamily(family);
                    created++;

                    _logger.LogInformation(
                        "Created family {FamilyId} with {Count} members (reason={Reason}, confidence={Confidence:F2})",
                        family.FamilyId[..8], members.Count, reason, c.TotalScore);
                }
            }
        }

        return created;
    }

    private int EvaluateSplits()
    {
        var allFamilies = _signatureCoordinator.GetAllFamilies();
        var splits = 0;

        // Under load: evaluate highest-bot-probability families first.
        // Build a quick lookup from FamilyAwareBehaviors which already aggregates per family.
        IEnumerable<SignatureFamily> families = allFamilies;
        var cap = _loadSensor?.GetWorstOffenderCap(allFamilies.Count);
        if (cap.HasValue && allFamilies.Count > cap.Value)
        {
            var behaviorBySignature = _signatureCoordinator
                .GetFamilyAwareBehaviors()
                .ToDictionary(b => b.Signature, b => b.AverageBotProbability,
                    StringComparer.OrdinalIgnoreCase);

            families = allFamilies
                .OrderByDescending(f =>
                    f.MemberSignatures.Keys
                        .Select(s => behaviorBySignature.TryGetValue(s, out var p) ? p : 0)
                        .DefaultIfEmpty(0)
                        .Max())
                .Take(cap.Value);
        }

        foreach (var family in families)
        {
            if (family.EvaluationCount < _options.MinEvaluationsBeforeSplit)
            {
                family.EvaluationCount++;
                family.LastEvaluatedUtc = DateTime.UtcNow;
                continue;
            }

            // Get current behaviors for all members
            var memberProbs = new Dictionary<string, double>();
            foreach (var sig in family.MemberSignatures.Keys.ToList())
            {
                var behavior = _signatureCoordinator
                    .GetSignatureBehaviorAsync(sig, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (behavior != null && behavior.RequestCount > 0)
                    memberProbs[sig] = behavior.AverageBotProbability;
            }

            if (memberProbs.Count < 2)
                continue;

            var familyAvg = memberProbs.Values.Average();

            // Find divergent members
            var divergent = memberProbs
                .Where(kvp => Math.Abs(kvp.Value - familyAvg) > _options.SplitDivergenceThreshold)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var sig in divergent)
            {
                family.MemberSignatures.TryRemove(sig, out _);

                // Clean up reverse index so GetFamily() no longer returns this family for the split signature
                _signatureCoordinator.RemoveSignatureFromFamilyIndex(sig);

                // Add cooldown to prevent immediate re-merge
                foreach (var remaining in family.MemberSignatures.Keys)
                {
                    var cooldownKey = GetCooldownKey(sig, remaining);
                    _splitCooldowns[cooldownKey] = DateTime.UtcNow.AddMinutes(5);
                }

                _logger.LogInformation(
                    "Split {Signature} from family {FamilyId} (prob={Prob:F2}, familyAvg={Avg:F2}, divergence={Div:F2})",
                    sig[..Math.Min(8, sig.Length)], family.FamilyId[..8],
                    memberProbs[sig], familyAvg,
                    Math.Abs(memberProbs[sig] - familyAvg));
            }

            if (divergent.Count > 0)
                splits++;

            // Dissolve family if only 1 member remains
            if (family.MemberSignatures.Count <= 1)
            {
                _signatureCoordinator.RemoveFamily(family.FamilyId);
            }
            else
            {
                family.LastEvaluatedUtc = DateTime.UtcNow;
                family.EvaluationCount++;
                _signatureCoordinator.RegisterFamily(family);
            }
        }

        return splits;
    }

    private int EvaluateVectorSimilarityMerges(IReadOnlyDictionary<string, SignatureBehavior> allBehaviors)
    {
        if (_vectorSearch == null) return 0;

        var snapshot = _vectorSearch.GetAllVectorsSnapshot();
        if (snapshot.Count < 2) return 0;

        // Reverse lookup: sig -> IP hash, built from the already-fetched IP index
        var sigToIpHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ipHash, sigs) in _signatureCoordinator.GetIpIndex())
            foreach (var sig in sigs)
                sigToIpHash.TryAdd(sig, ipHash);

        IEnumerable<(float[] Vector, SessionVectorMetadata Metadata)> candidates = snapshot;
        var cap = _loadSensor?.GetWorstOffenderCap(snapshot.Count);
        if (cap.HasValue && snapshot.Count > cap.Value)
        {
            candidates = snapshot
                .OrderByDescending(e => allBehaviors.TryGetValue(e.Metadata.Signature, out var b)
                    ? b.AverageBotProbability : 0)
                .Take(cap.Value);
        }

        var created = 0;
        var processedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxFamilies = _options.MaxFamilies;

        foreach (var (vector, meta) in candidates)
        {
            if (vector.Length == 0) continue;

            var sigA = meta.Signature;
            var similar = _vectorSearch.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.75f)
                .GetAwaiter().GetResult();

            foreach (var match in similar)
            {
                var sigB = match.Signature;
                if (string.Equals(sigA, sigB, StringComparison.OrdinalIgnoreCase)) continue;

                if (!processedPairs.Add(GetCooldownKey(sigA, sigB))) continue;

                var famA = _signatureCoordinator.GetFamily(sigA);
                var famB = _signatureCoordinator.GetFamily(sigB);
                if (famA != null && famB != null && famA.FamilyId == famB.FamilyId) continue;

                if (_splitCooldowns.ContainsKey(GetCooldownKey(sigA, sigB))) continue;

                sigToIpHash.TryGetValue(sigA, out var ipA);
                sigToIpHash.TryGetValue(sigB, out var ipB);
                if (ipA != null && ipB != null &&
                    !string.Equals(ipA, ipB, StringComparison.OrdinalIgnoreCase))
                {
                    _signatureCoordinator.RecordRotationEvent(new IdentityRotationEvent
                    {
                        CanonicalSignature = sigA,
                        RotatedSignature = sigB,
                        VectorSimilarity = match.Similarity,
                        PreviousIpHash = ipA,
                        NewIpHash = ipB,
                        DetectedUtc = DateTime.UtcNow
                    });
                    _logger.LogInformation(
                        "Identity rotation: {SigA}→{SigB} similarity={Sim:F3} (IP changed)",
                        sigA[..Math.Min(8, sigA.Length)], sigB[..Math.Min(8, sigB.Length)],
                        match.Similarity);
                }

                var existingFamily = famA ?? famB;
                if (existingFamily != null)
                {
                    ExtendFamily(existingFamily, sigA, sigB);
                }
                else
                {
                    if (_signatureCoordinator.GetAllFamilies().Count >= maxFamilies) continue;

                    var members = new HashSet<string> { sigA, sigB };
                    var memberBehaviors = members
                        .Where(allBehaviors.ContainsKey)
                        .ToDictionary(s => s, s => allBehaviors[s]);
                    var canonical = memberBehaviors.Count > 0
                        ? DetermineCanonicalSignature(members, memberBehaviors)
                        : sigA;

                    var family = new SignatureFamily
                    {
                        FamilyId = ComputeFamilyId(members),
                        CanonicalSignature = canonical,
                        MemberSignatures = SignatureFamily.CreateMemberSet(members),
                        CreatedUtc = DateTime.UtcNow,
                        LastEvaluatedUtc = DateTime.UtcNow,
                        FormationReason = FamilyFormationReason.VectorSimilarity,
                        MergeConfidence = match.Similarity,
                        EvaluationCount = 1
                    };
                    _signatureCoordinator.RegisterFamily(family);
                    created++;

                    _logger.LogInformation(
                        "Created vector-similarity family {FamilyId}: {SigA}+{SigB} (similarity={Sim:F3})",
                        family.FamilyId[..8], sigA[..Math.Min(8, sigA.Length)],
                        sigB[..Math.Min(8, sigB.Length)], match.Similarity);
                }
            }
        }

        return created;
    }

    private void ExtendFamily(SignatureFamily family, string sigA, string sigB)
    {
        var newSig = family.MemberSignatures.ContainsKey(sigA) ? sigB : sigA;
        family.MemberSignatures.TryAdd(newSig, 0);
        family.LastEvaluatedUtc = DateTime.UtcNow;
        family.EvaluationCount++;
        _signatureCoordinator.RegisterFamily(family);
    }

    private MergeCandidate ComputeMergeScore(
        string sigA, string sigB,
        SignatureBehavior behaviorA, SignatureBehavior behaviorB)
    {
        // Bot probability: both bots = merge, one human + one bot = hard veto
        var botProbScore = ComputeBotProbabilityAgreement(behaviorA, behaviorB);

        // Hard veto: if one is bot-classified and the other is not, never merge.
        // This prevents fuzzing detection resolution by merging a human with a bot.
        if (botProbScore == 0.0)
            return new MergeCandidate(sigA, sigB, 0, 0, 0, 0);

        // Temporal: overlap between [FirstSeen, LastSeen] windows
        var temporalScore = ComputeTemporalOverlap(behaviorA, behaviorB);

        // Behavioral: similarity of timing CV, path entropy, request rate
        var behavioralScore = ComputeBehavioralSimilarity(behaviorA, behaviorB);

        var totalScore = _options.TemporalWeight * temporalScore +
                         _options.BehavioralWeight * behavioralScore +
                         _options.BotProbabilityWeight * botProbScore;

        return new MergeCandidate(sigA, sigB, temporalScore, behavioralScore, botProbScore, totalScore);
    }

    private double ComputeTemporalOverlap(SignatureBehavior a, SignatureBehavior b)
    {
        var window = TimeSpan.FromSeconds(_options.TemporalProximityWindowSeconds);

        // Check if time windows overlap within the proximity window
        var overlapStart = a.FirstSeen > b.FirstSeen ? a.FirstSeen : b.FirstSeen;
        var overlapEnd = a.LastSeen < b.LastSeen ? a.LastSeen : b.LastSeen;
        var overlap = (overlapEnd - overlapStart).TotalSeconds;

        if (overlap > 0)
            return 1.0; // Direct overlap

        // Check if they're within the temporal proximity window
        var gap = -overlap; // gap is positive when no overlap
        if (gap <= window.TotalSeconds)
            return 1.0 - (gap / window.TotalSeconds);

        return 0.0;
    }

    private static double ComputeBehavioralSimilarity(SignatureBehavior a, SignatureBehavior b)
    {
        // Timing CV similarity (lower diff = more similar)
        var timingDiff = Math.Abs(a.TimingCoefficient - b.TimingCoefficient);
        var timingSim = 1.0 - Math.Min(1.0, timingDiff / 2.0);

        // Path entropy similarity
        var entropyDiff = Math.Abs(a.PathEntropy - b.PathEntropy);
        var entropySim = 1.0 - Math.Min(1.0, entropyDiff / 5.0);

        // Request rate similarity (using average interval)
        double rateSim;
        if (a.AverageInterval > 0 && b.AverageInterval > 0)
        {
            var ratio = Math.Min(a.AverageInterval, b.AverageInterval) /
                        Math.Max(a.AverageInterval, b.AverageInterval);
            rateSim = ratio; // 1.0 = identical rates
        }
        else
        {
            rateSim = a.AverageInterval == b.AverageInterval ? 1.0 : 0.0;
        }

        return (timingSim + entropySim + rateSim) / 3.0;
    }

    private static double ComputeBotProbabilityAgreement(SignatureBehavior a, SignatureBehavior b)
    {
        // VETO: one human + one bot = never merge
        var aIsBot = a.AverageBotProbability > 0.5;
        var bIsBot = b.AverageBotProbability > 0.5;

        if (aIsBot != bIsBot)
            return 0.0; // VETO

        // Both bots: strong merge signal
        if (aIsBot && bIsBot)
            return 1.0;

        // Both humans: moderate merge signal (could be shared household/office IP)
        return 0.5;
    }

    private static string DetermineCanonicalSignature(
        HashSet<string> members, Dictionary<string, SignatureBehavior> behaviors)
    {
        // Pick the member with the most requests as canonical
        return members
            .Where(behaviors.ContainsKey)
            .OrderByDescending(m => behaviors[m].RequestCount)
            .ThenBy(m => behaviors[m].FirstSeen)
            .FirstOrDefault() ?? members.First();
    }

    private static FamilyFormationReason DetermineFormationReason(MergeCandidate candidate)
    {
        if (candidate.BotProbabilityScore >= 0.9)
            return FamilyFormationReason.HighBotProbabilityCluster;
        if (candidate.TemporalScore >= candidate.BehavioralScore)
            return FamilyFormationReason.TemporalProximity;
        return FamilyFormationReason.BehavioralSimilarity;
    }

    private static string ComputeFamilyId(HashSet<string> members)
    {
        var sorted = members.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var combined = string.Join("|", sorted);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexStringLower(hash);
    }

    private static string GetCooldownKey(string sigA, string sigB)
    {
        return string.Compare(sigA, sigB, StringComparison.Ordinal) < 0
            ? $"{sigA}|{sigB}"
            : $"{sigB}|{sigA}";
    }
}
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that evaluates merge/split candidates for signature families.
///     Detects when multiple signatures from the same IP should be grouped (UA rotation)
///     and when divergent members should be split from a family.
/// </summary>
public class SignatureConvergenceService : BackgroundService
{
    private readonly ILogger<SignatureConvergenceService> _logger;
    private readonly SignatureConvergenceOptions _options;
    private readonly SignatureCoordinator _signatureCoordinator;

    // Cooldown: recently split signatures can't be re-merged for 5 minutes
    private readonly ConcurrentDictionary<string, DateTime> _splitCooldowns = new();

    public SignatureConvergenceService(
        ILogger<SignatureConvergenceService> logger,
        IOptions<BotDetectionOptions> options,
        SignatureCoordinator signatureCoordinator)
    {
        _logger = logger;
        _options = options.Value.SignatureConvergence;
        _signatureCoordinator = signatureCoordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SignatureConvergenceService disabled");
            return;
        }

        _logger.LogInformation(
            "SignatureConvergenceService started (interval={Interval}s, mergeThreshold={MergeThreshold}, splitThreshold={SplitThreshold})",
            _options.EvaluationIntervalSeconds,
            _options.MergeScoreThreshold,
            _options.SplitDivergenceThreshold);

        // Let the system warm up before first evaluation
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunEvaluation();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during convergence evaluation");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal void RunEvaluation()
    {
        var familiesCreated = 0;
        var familiesSplit = 0;

        // Clean up expired cooldowns
        var now = DateTime.UtcNow;
        foreach (var (key, expiry) in _splitCooldowns)
        {
            if (now > expiry)
                _splitCooldowns.TryRemove(key, out _);
        }

        // Phase 1: Evaluate merge candidates
        familiesCreated = EvaluateMerges();

        // Phase 2: Evaluate split candidates
        familiesSplit = EvaluateSplits();

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

    private int EvaluateMerges()
    {
        var ipIndex = _signatureCoordinator.GetIpIndex();
        var created = 0;

        foreach (var (ipHash, signatures) in ipIndex)
        {
            if (signatures.Count < _options.MinSignaturesForMerge)
                continue;

            // Get behaviors for all signatures under this IP
            var behaviors = new Dictionary<string, SignatureBehavior>();
            foreach (var sig in signatures)
            {
                var behavior = _signatureCoordinator
                    .GetSignatureBehaviorAsync(sig, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (behavior != null && behavior.RequestCount > 0)
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

                // Check if either signature is already in a family -> extend it
                var existingFamily = _signatureCoordinator.GetFamily(c.SignatureA) ??
                                    _signatureCoordinator.GetFamily(c.SignatureB);

                if (existingFamily != null)
                {
                    // Add the non-member to existing family
                    var newSig = existingFamily.MemberSignatures.ContainsKey(c.SignatureA)
                        ? c.SignatureB
                        : c.SignatureA;
                    existingFamily.MemberSignatures.TryAdd(newSig, 0);
                    existingFamily.LastEvaluatedUtc = DateTime.UtcNow;
                    existingFamily.EvaluationCount++;
                    _signatureCoordinator.RegisterFamily(existingFamily);
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
        var families = _signatureCoordinator.GetAllFamilies();
        var splits = 0;

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

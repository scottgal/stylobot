using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Analyzes PoW challenge solve characteristics as detection signals.
///     When a visitor has previously solved a proof-of-work challenge, this contributor
///     reads the solve metadata (duration, worker count, timing jitter) and emits
///     human or bot signals based on the solve profile.
///
///     This is the feedback loop: challenge is an ACTION (post-detection),
///     verification result is a SIGNAL (consumed on next request). No circular dependency.
///
///     Key insight: a visitor scoring 0.5-0.7 (edge case) who solves a PoW with
///     realistic browser timing gets a strong human signal, reducing false positives.
///     Configuration loaded from: challengeverification.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:ChallengeVerificationContributor:*
/// </summary>
public class ChallengeVerificationContributor : ConfiguredContributorBase
{
    private readonly IChallengeStore _challengeStore;
    private readonly ILogger<ChallengeVerificationContributor> _logger;

    public ChallengeVerificationContributor(
        IChallengeStore challengeStore,
        ILogger<ChallengeVerificationContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _challengeStore = challengeStore;
        _logger = logger;
    }

    public override string Name => "ChallengeVerification";
    public override int Priority => Manifest?.Priority ?? 25;

    // Config-driven thresholds
    private double SuspiciousFastMsPerPuzzle => GetParam("suspicious_fast_ms_per_puzzle", 50.0);
    private double ExpectedMinMsPerPuzzle => GetParam("expected_min_ms_per_puzzle", 200.0);
    private double ExpectedMaxMsPerPuzzle => GetParam("expected_max_ms_per_puzzle", 5000.0);
    private double SuspiciousLowJitter => GetParam("suspicious_low_jitter", 0.05);
    private double NormalJitterMin => GetParam("normal_jitter_min", 0.15);
    private int SuspiciousMaxWorkerCount => GetParam("suspicious_max_worker_count", 1);
    private int MinPuzzlesForWorkerCheck => GetParam("min_puzzles_for_worker_check", 4);
    private double FastSolveConfidence => GetParam("fast_solve_confidence", 0.15);
    private double FastSolveWeight => GetParam("fast_solve_weight", 1.2);
    private double RealisticSolveConfidence => GetParam("realistic_solve_confidence", -0.35);
    private double RealisticSolveWeight => GetParam("realistic_solve_weight", 1.5);
    private double LowJitterConfidence => GetParam("low_jitter_confidence", 0.10);
    private double LowJitterWeight => GetParam("low_jitter_weight", 1.0);
    private double NormalJitterConfidence => GetParam("normal_jitter_confidence", -0.05);
    private double NormalJitterWeight => GetParam("normal_jitter_weight", 0.8);
    private double LowWorkerConfidence => GetParam("low_worker_confidence", 0.08);
    private double LowWorkerWeight => GetParam("low_worker_weight", 0.9);

    public override IReadOnlyList<TriggerCondition> TriggerConditions => [];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            // Get signature from context
            var signature = state.HttpContext.Items.TryGetValue("BotDetection:Signature", out var sig) && sig is string s
                ? s
                : null;

            if (signature is null)
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

            var verification = _challengeStore.GetVerification(signature);
            if (verification is null)
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

            // Write signals for downstream consumers
            state.WriteSignal(SignalKeys.ChallengeVerified, true);
            state.WriteSignal(SignalKeys.ChallengeSolveDurationMs, verification.TotalSolveDurationMs);
            state.WriteSignal(SignalKeys.ChallengeTimingJitter, verification.TimingJitter);
            state.WriteSignal(SignalKeys.ChallengeWorkerCount, verification.ReportedWorkerCount);
            state.WriteSignal(SignalKeys.ChallengePuzzleCount, verification.PuzzleCount);

            // Analyze solve characteristics
            var perPuzzleMs = verification.PuzzleCount > 0
                ? verification.TotalSolveDurationMs / verification.PuzzleCount
                : verification.TotalSolveDurationMs;

            // Too fast: powerful hardware or pre-computed
            if (perPuzzleMs < SuspiciousFastMsPerPuzzle)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = FastSolveConfidence,
                    Weight = FastSolveWeight,
                    Reason = $"PoW solved suspiciously fast ({perPuzzleMs:F0}ms/puzzle) - possible dedicated solver"
                });
            }
            // Expected human range
            else if (perPuzzleMs >= ExpectedMinMsPerPuzzle && perPuzzleMs <= ExpectedMaxMsPerPuzzle)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = RealisticSolveConfidence,
                    Weight = RealisticSolveWeight,
                    Reason = $"PoW solved with realistic timing ({perPuzzleMs:F0}ms/puzzle)"
                });
            }

            // Timing jitter analysis (performance.now() has 5ms quantization in workers)
            // Near-zero jitter with multiple puzzles = suspicious (dedicated solver, no OS scheduling noise)
            if (verification.PuzzleCount >= MinPuzzlesForWorkerCheck && verification.TimingJitter < SuspiciousLowJitter)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = LowJitterConfidence,
                    Weight = LowJitterWeight,
                    Reason = $"PoW timing jitter unusually low ({verification.TimingJitter:F3}) - possible automation"
                });
            }
            else if (verification.TimingJitter >= NormalJitterMin)
            {
                // High jitter = normal browser with OS scheduling noise = human-like
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = NormalJitterConfidence,
                    Weight = NormalJitterWeight,
                    Reason = "PoW timing jitter consistent with real browser"
                });
            }

            // Worker count analysis
            // 0-1 workers = possible headless browser (many don't expose navigator.hardwareConcurrency)
            if (verification.ReportedWorkerCount <= SuspiciousMaxWorkerCount && verification.PuzzleCount >= MinPuzzlesForWorkerCheck)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = LowWorkerConfidence,
                    Weight = LowWorkerWeight,
                    Reason = $"PoW solved with {verification.ReportedWorkerCount} worker(s) - possible headless browser"
                });
            }

            _logger.LogDebug(
                "ChallengeVerification for {Signature}: {Duration:F0}ms total, {PerPuzzle:F0}ms/puzzle, jitter={Jitter:F3}, workers={Workers}",
                signature, verification.TotalSolveDurationMs, perPuzzleMs, verification.TimingJitter, verification.ReportedWorkerCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChallengeVerification analysis failed");
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }
}

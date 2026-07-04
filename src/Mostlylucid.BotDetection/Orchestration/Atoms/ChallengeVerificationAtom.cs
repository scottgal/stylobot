using System.Globalization;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that analyzes PoW-challenge solve
///     characteristics as detection signals. When a visitor has previously
///     solved a proof-of-work challenge, this atom reads the solve metadata
///     (duration, worker count, timing jitter) and emits human or bot
///     contributions based on the solve profile.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ChallengeVerificationContributor</c>. Feedback loop: challenge is
///         an ACTION (post-detection), verification result is a SIGNAL
///         (consumed on next request) -- no circular dependency.
///     </para>
///     <para>
///         Key insight: a visitor scoring 0.5-0.7 (edge case) who solves a PoW
///         with realistic browser timing gets a strong human signal, reducing
///         false positives. Priority 25.
///     </para>
/// </remarks>
public sealed class ChallengeVerificationAtom : DetectorAtomBase
{
    private readonly IChallengeStore _challengeStore;
    private readonly ILogger<ChallengeVerificationAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;

    public ChallengeVerificationAtom(
        IChallengeStore challengeStore,
        ILogger<ChallengeVerificationAtom> logger,
        IDetectorConfigProvider configProvider)
        : base(name: "ChallengeVerification", category: "ChallengeVerification")
    {
        _challengeStore = challengeStore;
        _logger = logger;
        _configProvider = configProvider;
    }

    public override int Priority => 25;

    // Config-driven thresholds
    private double SuspiciousFastMsPerPuzzle => _configProvider.GetParameter(Name, "suspicious_fast_ms_per_puzzle", 50.0);
    private double ExpectedMinMsPerPuzzle => _configProvider.GetParameter(Name, "expected_min_ms_per_puzzle", 200.0);
    private double ExpectedMaxMsPerPuzzle => _configProvider.GetParameter(Name, "expected_max_ms_per_puzzle", 5000.0);
    private double SuspiciousLowJitter => _configProvider.GetParameter(Name, "suspicious_low_jitter", 0.05);
    private double NormalJitterMin => _configProvider.GetParameter(Name, "normal_jitter_min", 0.15);
    private int SuspiciousMaxWorkerCount => _configProvider.GetParameter(Name, "suspicious_max_worker_count", 1);
    private int MinPuzzlesForWorkerCheck => _configProvider.GetParameter(Name, "min_puzzles_for_worker_check", 4);
    private double FastSolveConfidence => _configProvider.GetParameter(Name, "fast_solve_confidence", 0.15);
    private double FastSolveWeight => _configProvider.GetParameter(Name, "fast_solve_weight", 1.2);
    private double RealisticSolveConfidence => _configProvider.GetParameter(Name, "realistic_solve_confidence", -0.35);
    private double RealisticSolveWeight => _configProvider.GetParameter(Name, "realistic_solve_weight", 1.5);
    private double LowJitterConfidence => _configProvider.GetParameter(Name, "low_jitter_confidence", 0.10);
    private double LowJitterWeight => _configProvider.GetParameter(Name, "low_jitter_weight", 1.0);
    private double NormalJitterConfidence => _configProvider.GetParameter(Name, "normal_jitter_confidence", -0.05);
    private double NormalJitterWeight => _configProvider.GetParameter(Name, "normal_jitter_weight", 0.8);
    private double LowWorkerConfidence => _configProvider.GetParameter(Name, "low_worker_confidence", 0.08);
    private double LowWorkerWeight => _configProvider.GetParameter(Name, "low_worker_weight", 0.9);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var signature = sink.ReadHint(SignalKeys.PrimarySignature);
            if (string.IsNullOrEmpty(signature))
                return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);

            var verification = _challengeStore.GetVerification(signature);
            if (verification is null)
                return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);

            // Presence + hint signals for downstream consumers
            sink.Raise(SignalKeys.ChallengeVerified, sessionId);
            sink.Raise($"{SignalKeys.ChallengeSolveDurationMs}:{verification.TotalSolveDurationMs.ToString("F0", CultureInfo.InvariantCulture)}", sessionId);
            sink.Raise($"{SignalKeys.ChallengeTimingJitter}:{verification.TimingJitter.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
            sink.Raise($"{SignalKeys.ChallengeWorkerCount}:{verification.ReportedWorkerCount}", sessionId);
            sink.Raise($"{SignalKeys.ChallengePuzzleCount}:{verification.PuzzleCount}", sessionId);

            var perPuzzleMs = verification.PuzzleCount > 0
                ? verification.TotalSolveDurationMs / verification.PuzzleCount
                : verification.TotalSolveDurationMs;

            // Too fast: powerful hardware or pre-computed
            if (perPuzzleMs < SuspiciousFastMsPerPuzzle)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
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
                    Category = Category,
                    ConfidenceDelta = RealisticSolveConfidence,
                    Weight = RealisticSolveWeight,
                    Reason = $"PoW solved with realistic timing ({perPuzzleMs:F0}ms/puzzle)"
                });
            }

            // Timing jitter analysis (performance.now() has 5ms quantization in workers)
            if (verification.PuzzleCount >= MinPuzzlesForWorkerCheck && verification.TimingJitter < SuspiciousLowJitter)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = LowJitterConfidence,
                    Weight = LowJitterWeight,
                    Reason = $"PoW timing jitter unusually low ({verification.TimingJitter:F3}) - possible automation"
                });
            }
            else if (verification.TimingJitter >= NormalJitterMin)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = NormalJitterConfidence,
                    Weight = NormalJitterWeight,
                    Reason = "PoW timing jitter consistent with real browser"
                });
            }

            // Worker count analysis
            if (verification.ReportedWorkerCount <= SuspiciousMaxWorkerCount && verification.PuzzleCount >= MinPuzzlesForWorkerCheck)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
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

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }
}

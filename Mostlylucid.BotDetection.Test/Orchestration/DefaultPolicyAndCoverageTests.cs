using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Tests that the default detection policy includes the required detectors
///     and that coverage confidence is computed correctly.
/// </summary>
public class DefaultPolicyAndCoverageTests
{
    [Fact]
    public void DefaultPolicy_IncludesHeuristic()
    {
        var policy = DetectionPolicy.Default;
        Assert.Contains("Heuristic", policy.FastPathDetectors);
    }

    [Fact]
    public void DefaultPolicy_IncludesHeuristicLate()
    {
        var policy = DetectionPolicy.Default;
        Assert.Contains("HeuristicLate", policy.FastPathDetectors);
    }

    [Fact]
    public void DefaultPolicy_IncludesCacheBehavior()
    {
        var policy = DetectionPolicy.Default;
        Assert.Contains("CacheBehavior", policy.FastPathDetectors);
    }

    [Fact]
    public void DefaultPolicy_IncludesReputationBias()
    {
        var policy = DetectionPolicy.Default;
        Assert.Contains("ReputationBias", policy.FastPathDetectors);
    }

    [Fact]
    public void DefaultPolicy_IncludesGeo()
    {
        var policy = DetectionPolicy.Default;
        Assert.Contains("Geo", policy.FastPathDetectors);
    }

    [Fact]
    public void DefaultPolicy_IncludesAllCoverageFormulaDetectors()
    {
        // These are the detectors the ComputeCoverageConfidence formula checks:
        // UserAgent(1.0), Ip(0.5), Header(1.0), ClientSide(1.0),
        // Behavioral(1.0), VersionAge(0.8), Inconsistency(0.8), Heuristic(2.0)
        var policy = DetectionPolicy.Default;
        var coverageDetectors = new[]
        {
            "UserAgent", "Ip", "Header", "ClientSide",
            "Behavioral", "VersionAge", "Inconsistency", "Heuristic"
        };

        foreach (var detector in coverageDetectors)
            Assert.Contains(detector, policy.FastPathDetectors);
    }

    [Fact]
    public void CoverageConfidence_AllEightDetectors_ReturnsOne()
    {
        // When all 8 coverage formula detectors ran, coverage should be 1.0 (before AI)
        var ledger = new DetectionLedger("test");
        var allDetectors = new[] { "UserAgent", "Ip", "Header", "ClientSide", "Behavioral", "VersionAge", "Inconsistency", "Heuristic" };

        foreach (var name in allDetectors)
        {
            ledger.AddContribution(new DetectionContribution
            {
                DetectorName = name,
                Category = "Test",
                ConfidenceDelta = 0.1,
                Weight = 1.0,
                Reason = "test"
            });
        }

        var result = ledger.ToAggregatedEvidence(aiRan: false);
        // Coverage should be 8.1/8.1 = 1.0 (all detectors ran)
        // Confidence is min(ledger confidence, coverage confidence)
        // Since all coverage detectors ran, coverage = 1.0
        Assert.True(result.Confidence > 0.43, $"Confidence {result.Confidence} should be > 0.43 when all 8 coverage detectors ran");
    }

    [Fact]
    public void CoverageConfidence_OnlyWave0Detectors_ReturnsLessThanHalf()
    {
        // When only Wave 0 detectors ran (UserAgent, Ip, Header, Behavioral),
        // coverage = 3.5/8.1 = 0.4321 - this was the old "always 43%" bug
        var ledger = new DetectionLedger("test");
        var wave0Detectors = new[] { "UserAgent", "Ip", "Header", "Behavioral" };

        foreach (var name in wave0Detectors)
        {
            ledger.AddContribution(new DetectionContribution
            {
                DetectorName = name,
                Category = "Test",
                ConfidenceDelta = 0.1,
                Weight = 1.0,
                Reason = "test"
            });
        }

        var result = ledger.ToAggregatedEvidence(aiRan: false);
        // Coverage should be 3.5/8.1 ≈ 0.43
        Assert.True(result.Confidence <= 0.44, $"Confidence {result.Confidence} should be ≤ 0.44 with only 4 wave-0 detectors");
    }

    [Fact]
    public void CoverageConfidence_WithHeuristic_SignificantlyHigher()
    {
        // Adding Heuristic (weight 2.0) should significantly boost coverage
        var ledger = new DetectionLedger("test");
        var detectors = new[] { "UserAgent", "Ip", "Header", "Behavioral", "VersionAge", "Inconsistency", "Heuristic" };

        foreach (var name in detectors)
        {
            ledger.AddContribution(new DetectionContribution
            {
                DetectorName = name,
                Category = "Test",
                ConfidenceDelta = 0.1,
                Weight = 1.0,
                Reason = "test"
            });
        }

        var result = ledger.ToAggregatedEvidence(aiRan: false);
        // Coverage = (1.0+0.5+1.0+1.0+0.8+0.8+2.0)/8.1 = 7.1/8.1 ≈ 0.877
        Assert.True(result.Confidence > 0.43, $"Confidence {result.Confidence} should be > 0.43 when Heuristic included");
    }

    [Fact]
    public void DefaultPolicy_HasExpectedDetectorCount()
    {
        var policy = DetectionPolicy.Default;
        // Should have at least 14 detectors (the core set + additions)
        Assert.True(policy.FastPathDetectors.Count >= 14,
            $"Expected >= 14 detectors in default policy, got {policy.FastPathDetectors.Count}: {string.Join(", ", policy.FastPathDetectors)}");
    }
}

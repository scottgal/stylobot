using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Regression lock for the dead-detector class of bug: a contributor gets registered in DI
///     but no production policy names it in FastPath/SlowPath/AiPath/ResponsePath, so it is
///     silently never invoked. This is invisible at startup and at runtime — only an audit
///     against the policy lists catches it.
///
///     This test enumerates every registered IContributingDetector, filters out the ones that
///     are legitimately not in a fixed-path list (foundation contributors that always run,
///     trigger-gated classifiers that fire from another contributor's signal), and asserts the
///     remainder appears in <see cref="DetectionPolicy.Default"/>'s combined detector lists.
///
///     If this test fails, EITHER add the detector to <c>DetectionPolicy.Default</c>, OR mark it
///     as <c>IFoundationContributor</c>, OR give it non-empty <c>TriggerConditions</c> if it is
///     genuinely event-gated. Do NOT just delete the assertion — the whole point is that the
///     drift gets caught.
/// </summary>
public class DetectorRegistrationCoverageTests
{
    [Fact]
    public async Task EveryUnconditionalClassifier_IsNamedByDetectionPolicyDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddBotDetection();

        await using var sp = services.BuildServiceProvider();
        var detectors = sp.GetServices<IContributingDetector>().ToList();

        var defaultPolicy = DetectionPolicy.Default;
        var policyDetectors = new HashSet<string>(
            defaultPolicy.FastPathDetectors
                .Concat(defaultPolicy.SlowPathDetectors)
                .Concat(defaultPolicy.AiPathDetectors)
                .Concat(defaultPolicy.ResponsePathDetectors),
            StringComparer.OrdinalIgnoreCase);

        var dead = new List<string>();
        foreach (var d in detectors)
        {
            if (d is IFoundationContributor) continue;                  // always runs
            if (d.TriggerConditions.Count > 0) continue;                // fires on signal
            if (defaultPolicy.ExcludedDetectors.Contains(d.Name)) continue; // deliberately off
            if (policyDetectors.Contains(d.Name)) continue;             // wired in

            dead.Add($"{d.Name} ({d.GetType().Name})");
        }

        Assert.True(dead.Count == 0,
            "The following classifiers are registered in DI but never run under " +
            $"DetectionPolicy.Default — they have no triggers and no policy slot:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", dead.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) +
            Environment.NewLine + Environment.NewLine +
            "Fix one of: add to DetectionPolicy.Default's FastPathDetectors, mark IFoundationContributor, " +
            "give the detector non-empty TriggerConditions, or add to ExcludedDetectors with a comment.");
    }

    /// <summary>
    ///     Specific anchors named in CLAUDE.md's Fast Path enumeration. These are critical
    ///     classifiers and must be in <see cref="DetectionPolicy.Default"/> by name. Listed
    ///     individually so a regression names the specific signal that went missing.
    /// </summary>
    [Theory]
    [InlineData("UserAgent")]
    [InlineData("Header")]
    [InlineData("Ip")]
    [InlineData("SecurityTool")]
    [InlineData("Behavioral")]
    [InlineData("ClientSide")]
    [InlineData("Inconsistency")]
    [InlineData("VersionAge")]
    [InlineData("Heuristic")]
    [InlineData("FastPathReputation")]
    [InlineData("CacheBehavior")]
    [InlineData("ReputationBias")]
    [InlineData("AiScraper")]
    [InlineData("Haxxor")]
    [InlineData("TlsFingerprint")]
    // NOTE: VerifiedBot is NOT in this list - rDNS is too expensive for the inline path,
    // so it runs via BackgroundEnrichmentService and feeds FastPathReputation on the next
    // hit. It's in ExcludedDetectors by design.
    [InlineData("Http2Fingerprint")]
    [InlineData("Http3Fingerprint")]
    [InlineData("TcpIpFingerprint")]
    [InlineData("MultiLayerCorrelation")]
    [InlineData("SessionVector")]
    [InlineData("BehavioralWaveform")]
    [InlineData("Periodicity")]
    [InlineData("ResponseBehavior")]
    [InlineData("ThreatIntel")]
    public void CriticalFastPathDetector_IsNamedByDetectionPolicyDefault(string detectorName)
    {
        var defaultPolicy = DetectionPolicy.Default;
        var policyDetectors = new HashSet<string>(
            defaultPolicy.FastPathDetectors
                .Concat(defaultPolicy.SlowPathDetectors)
                .Concat(defaultPolicy.AiPathDetectors)
                .Concat(defaultPolicy.ResponsePathDetectors),
            StringComparer.OrdinalIgnoreCase);

        Assert.True(policyDetectors.Contains(detectorName),
            $"{detectorName} is named in CLAUDE.md's critical detector enumeration but is " +
            $"absent from DetectionPolicy.Default. A bare `stylobot` install therefore does " +
            $"not run {detectorName} — half the detection surface goes dark silently. " +
            $"Add {detectorName} to DetectionPolicy.Default.FastPathDetectors.");
    }
}

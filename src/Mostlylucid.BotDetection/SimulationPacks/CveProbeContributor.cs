using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Detects CVE vulnerability probes by matching request paths against loaded simulation packs.
///     Works without the Holodeck - this is core detection functionality that identifies
///     bots scanning for known vulnerabilities (WordPress, Drupal, etc.).
///     Priority 11 = Wave 0, runs with other fast-path detectors.
/// </summary>
public class CveProbeContributor : ContributingDetectorBase
{
    private readonly ISimulationPackRegistry _registry;
    private readonly ILogger<CveProbeContributor> _logger;

    public CveProbeContributor(
        ISimulationPackRegistry registry,
        ILogger<CveProbeContributor> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public override string Name => "CveProbe";
    public override int Priority => 11;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var path = state.Path;

            if (!_registry.IsHoneypotPath(path, out var matchedPack, out var matchedCve))
            {
                // No match - nothing to contribute
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            // We have a match - this is a bot probing for vulnerabilities
            state.WriteSignal(SignalKeys.SimulationPackMatch, true);
            state.WriteSignal(SignalKeys.CveProbePackId, matchedPack!.Id);

            if (matchedCve is not null)
            {
                // CVE probe match - high confidence malicious scanning
                state.WriteSignal(SignalKeys.CveProbeDetected, true);
                state.WriteSignal(SignalKeys.CveProbeId, matchedCve.CveId);
                state.WriteSignal(SignalKeys.CveProbeSeverity, matchedCve.Severity ?? "unknown");

                var severity = matchedCve.Severity?.ToLowerInvariant() ?? "unknown";
                var isCriticalOrHigh = severity is "critical" or "high";

                var confidence = severity switch
                {
                    "critical" => 0.95,
                    "high" => 0.90,
                    "medium" => 0.80,
                    _ => 0.75
                };

                var botType = isCriticalOrHigh
                    ? BotType.MaliciousBot.ToString()
                    : BotType.Scraper.ToString();

                // Trigger simulation-pack action policy for CVE probes with sufficient confidence
                if (confidence >= 0.7)
                {
                    state.WriteSignal(SignalKeys.ActionPolicyTrigger, "simulation-pack");
                    state.WriteSignal(SignalKeys.ActionPolicyTriggerReason,
                        $"CVE probe: {matchedCve.CveId} ({severity})");
                }

                contributions.Add(DetectionContribution.Bot(
                    Name,
                    "CVE Probe",
                    confidence,
                    $"CVE probe detected: {matchedCve.CveId} ({severity}) - {matchedCve.Description ?? "vulnerability scan"} [pack: {matchedPack.Id}]",
                    weight: 2.5,
                    botType: botType));

                _logger.LogInformation(
                    "CVE probe detected: {CveId} ({Severity}) on path {Path} [pack: {PackId}]",
                    matchedCve.CveId, severity, path, matchedPack.Id);
            }
            else
            {
                // Honeypot path match (not a specific CVE, but still suspicious)
                // Find the matching honeypot path entry for confidence/weight
                var matchingHp = matchedPack.HoneypotPaths
                    .FirstOrDefault(hp => System.IO.Enumeration.FileSystemName
                        .MatchesSimpleExpression(hp.Path, path, ignoreCase: true));

                var confidence = matchingHp?.Confidence ?? 0.80;
                var weight = matchingHp?.Weight ?? 1.5;
                var category = matchingHp?.Category ?? "honeypot";

                // Trigger simulation-pack action policy for honeypot hits with sufficient confidence
                if (confidence >= 0.7)
                {
                    state.WriteSignal(SignalKeys.ActionPolicyTrigger, "simulation-pack");
                    state.WriteSignal(SignalKeys.ActionPolicyTriggerReason,
                        $"Honeypot path: {path} [{category}]");
                }

                contributions.Add(DetectionContribution.Bot(
                    Name,
                    "Simulation Pack",
                    confidence,
                    $"Honeypot path matched: {path} [{category}] [pack: {matchedPack.Id}]",
                    weight: weight,
                    botType: BotType.Scraper.ToString()));

                _logger.LogDebug(
                    "Simulation pack honeypot hit: {Path} [{Category}] [pack: {PackId}]",
                    path, category, matchedPack.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in CVE probe detection");
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }
}

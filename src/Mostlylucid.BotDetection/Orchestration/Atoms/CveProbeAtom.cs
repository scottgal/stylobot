using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.SimulationPacks;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that detects CVE vulnerability probes by
///     matching request paths against loaded simulation packs. Works without
///     the Holodeck — core detection functionality that identifies bots
///     scanning for known vulnerabilities (WordPress, Drupal, etc.).
///     Native <see cref="IDetectorAtom"/> replacement for
///     <c>CveProbeContributor</c>. Priority 11 — Wave 0.
/// </summary>
public sealed class CveProbeAtom : DetectorAtomBase
{
    private readonly ISimulationPackRegistry _registry;
    private readonly ILogger<CveProbeAtom> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CveProbeAtom(
        ISimulationPackRegistry registry,
        ILogger<CveProbeAtom> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "CveProbe", category: "CveProbe")
    {
        _registry = registry;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 11;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var path = context.Request.Path.Value ?? string.Empty;

        try
        {
            if (!_registry.IsHoneypotPath(path, out var matchedPack, out var matchedCve))
                return Task.FromResult(None());

            sink.Raise($"{SignalKeys.SimulationPackMatch}:true", sessionId);
            sink.Raise($"{SignalKeys.CveProbePackId}:{matchedPack!.Id}", sessionId);

            if (matchedCve is not null)
            {
                sink.Raise($"{SignalKeys.CveProbeDetected}:true", sessionId);
                sink.Raise($"{SignalKeys.CveProbeId}:{matchedCve.CveId}", sessionId);
                sink.Raise($"{SignalKeys.CveProbeSeverity}:{matchedCve.Severity ?? "unknown"}", sessionId);

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

                if (confidence >= 0.7)
                {
                    sink.Raise($"{SignalKeys.ActionPolicyTrigger}:simulation-pack", sessionId);
                    sink.Raise($"{SignalKeys.ActionPolicyTriggerReason}:CVE probe: {matchedCve.CveId} ({severity})", sessionId);
                }

                _logger.LogInformation(
                    "CVE probe detected: {CveId} ({Severity}) on path {Path} [pack: {PackId}]",
                    matchedCve.CveId, severity, path, matchedPack.Id);

                return Task.FromResult(Single(DetectionContribution.Bot(
                    Name,
                    "CVE Probe",
                    confidence,
                    $"CVE probe detected: {matchedCve.CveId} ({severity}) - {matchedCve.Description ?? "vulnerability scan"} [pack: {matchedPack.Id}]",
                    weight: 2.5,
                    botType: botType)));
            }

            // Honeypot path match without a specific CVE.
            var matchingHp = matchedPack.HoneypotPaths
                .FirstOrDefault(hp => System.IO.Enumeration.FileSystemName
                    .MatchesSimpleExpression(hp.Path, path, ignoreCase: true));

            var hpConfidence = matchingHp?.Confidence ?? 0.80;
            var hpWeight = matchingHp?.Weight ?? 1.5;
            var category = matchingHp?.Category ?? "honeypot";

            if (hpConfidence >= 0.7)
            {
                sink.Raise($"{SignalKeys.ActionPolicyTrigger}:simulation-pack", sessionId);
                sink.Raise($"{SignalKeys.ActionPolicyTriggerReason}:Honeypot path: {path} [{category}]", sessionId);
            }

            _logger.LogDebug(
                "Simulation pack honeypot hit: {Path} [{Category}] [pack: {PackId}]",
                path, category, matchedPack.Id);

            return Task.FromResult(Single(DetectionContribution.Bot(
                Name,
                "Simulation Pack",
                hpConfidence,
                $"Honeypot path matched: {path} [{category}] [pack: {matchedPack.Id}]",
                weight: hpWeight,
                botType: BotType.Scraper.ToString())));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in CVE probe detection");
            return Task.FromResult(None());
        }
    }
}

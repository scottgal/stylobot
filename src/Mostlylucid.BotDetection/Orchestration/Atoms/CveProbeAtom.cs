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
    private readonly Honeypot.IHoneypotExemptStore? _exemptStore;

    public CveProbeAtom(
        ISimulationPackRegistry registry,
        ILogger<CveProbeAtom> logger,
        IHttpContextAccessor httpContextAccessor,
        Honeypot.IHoneypotExemptStore? exemptStore = null)
        : base(name: "CveProbe", category: "CveProbe")
    {
        _registry = registry;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _exemptStore = exemptStore;
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

            // PATH-EXISTENCE honeypot match, no specific CVE. On a site that genuinely runs this
            // stack (the operator set the matching site profile), the path existing is not evidence
            // of anything -- it is the normal front door. Honour the SAME framework_paths exemption
            // HaxxorAtom's path_probes honours (operator P0 2026-08-17, and the wordpress profile
            // documents the two mechanisms sharing it on purpose). Without this, CveProbe was the
            // third reader of the honeypot catalog and the only one not exempted, so a real browser
            // hitting /wp-login.php on a WordPress-profiled host scored Scraper and crossed BotFloor
            // (bot_probability 0.52) -- caught by the BDF rig, which is why CI was red.
            //
            // BOUNDARY, deliberately drawn the same way as HaxxorAtom's: only the "this URL existing
            // is suspicious" signal is exempted. A matched CVE already returned above and still fires,
            // because that names a specific vulnerability rather than the mere presence of a path.
            if (_exemptStore is not null
                && _exemptStore.IsExempt(Honeypot.HoneypotPathDefinitions.NormalizePath(path), context))
            {
                _logger.LogDebug(
                    "Simulation pack honeypot hit on {Path} suppressed: the active site profile " +
                    "declares this path as its own framework path", path);
                return Task.FromResult(None());
            }

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

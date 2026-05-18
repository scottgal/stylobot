using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.ThreatIntel;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Threat-intel enrichment contributor. Reads cached verdicts from
///     <see cref="IThreatIntelCoordinator"/> for the client IP (and any CVE that
///     <c>CveProbeContributor</c> / <c>CveFingerprintContributor</c> already extracted)
///     and surfaces them as <c>threatintel.*</c> signals + a single risk-scaled
///     contribution.
///
///     <para>Runs at priority 7 - after <c>IpContributor</c> (12, gives us the
///     resolved client IP) but the manifest's no-trigger setup means it just runs
///     after IpContributor's signals are on the board. See
///     <c>docs/architecture/threat-intel.md</c>.</para>
///
///     <para>Short-circuits when the coordinator reports IsEnabled == false (FOSS
///     default: every provider disabled). No per-request work, no signals emitted.</para>
/// </summary>
public class ThreatIntelContributor : ConfiguredContributorBase
{
    private readonly IThreatIntelCoordinator _coordinator;
    private readonly ILogger<ThreatIntelContributor> _logger;

    public ThreatIntelContributor(
        ILogger<ThreatIntelContributor> logger,
        IDetectorConfigProvider configProvider,
        IThreatIntelCoordinator coordinator)
        : base(configProvider)
    {
        _logger = logger;
        _coordinator = coordinator;
    }

    public override string Name => "ThreatIntel";
    public override int Priority => Manifest?.Priority ?? 7;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Tunables from YAML defaults.parameters.
    // threat_score_weight: scales threatintel.score into a BotContribution's confidence.
    // kev_match_threat_floor: minimum threatintel.score when KEV matches an extracted CVE.
    private double ThreatScoreWeight => GetParam("threat_score_weight", 0.6);
    private double KevMatchThreatFloor => GetParam("kev_match_threat_floor", 0.7);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        if (!_coordinator.IsEnabled)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        var contributions = new List<DetectionContribution>();
        var verdicts = new List<ThreatIntelVerdict>(capacity: 4);

        // IP lookup (the bread-and-butter case).
        if (state.Signals.TryGetValue(SignalKeys.ClientIp, out var ipObj) && ipObj is string ip && !string.IsNullOrEmpty(ip))
        {
            foreach (var v in _coordinator.Lookup(new ThreatSubject(ThreatSubjectType.Ip, ip)))
                verdicts.Add(v);
        }

        // CVE lookup. CISA KEV provider answers here when CveProbe / CveFingerprint
        // already extracted a CVE id. Skip GHSA-* advisories (KEV is CVE-only).
        var cveId = TryGetCveId(state);
        if (cveId is not null)
        {
            foreach (var v in _coordinator.Lookup(new ThreatSubject(ThreatSubjectType.Cve, cveId)))
                verdicts.Add(v);
        }

        if (verdicts.Count == 0)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        EmitSignals(state, verdicts, cveId);

        var topScore = 0.0;
        ThreatIntelVerdict? top = null;
        foreach (var v in verdicts)
        {
            if (v.Confidence > topScore) { topScore = v.Confidence; top = v; }
        }

        // KEV match against a known-exploited CVE: hold the score at a configurable
        // floor so the verdict can't be drowned out by a single low-confidence offline
        // hit. Operator controls the floor via threatintel.kev_match_threat_floor.
        var kevHit = verdicts.Any(v => v.Classification == "kev");
        if (kevHit && topScore < KevMatchThreatFloor) topScore = KevMatchThreatFloor;

        if (topScore > 0 && top is not null)
        {
            var reason = $"{top.Provider} classified as {top.Classification}";
            if (verdicts.Count > 1) reason += $" (+{verdicts.Count - 1} more)";
            contributions.Add(BotContribution(
                "ThreatIntel",
                reason,
                confidenceOverride: Math.Clamp(topScore * ThreatScoreWeight, 0.0, 1.0)));
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private static string? TryGetCveId(BlackboardState state)
    {
        // Honeypot probe match wins over fingerprint match (it's stronger evidence).
        if (state.Signals.TryGetValue(SignalKeys.CveProbeId, out var probeObj) && probeObj is string probe
            && probe.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            return probe.ToUpperInvariant();
        if (state.Signals.TryGetValue(SignalKeys.CveTopAdvisoryId, out var advObj) && advObj is string adv
            && adv.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            return adv.ToUpperInvariant();
        return null;
    }

    private static void EmitSignals(BlackboardState state, List<ThreatIntelVerdict> verdicts, string? cveId)
    {
        var max = 0.0;
        var classifications = new HashSet<string>(StringComparer.Ordinal);
        var providers = new HashSet<string>(StringComparer.Ordinal);
        var torFlag = false;
        string? kevMatch = null;
        foreach (var v in verdicts)
        {
            if (v.Confidence > max) max = v.Confidence;
            classifications.Add(v.Classification);
            providers.Add(v.Provider);
            if (v.Classification == "tor") torFlag = true;
            if (v.Classification == "kev" && cveId is not null) kevMatch = cveId;
            // Per-provider signal so dashboards can filter by source.
            state.WriteSignal($"threatintel.{v.Provider}", v.Classification);
        }
        state.WriteSignal(SignalKeys.ThreatIntelScore, max);
        state.WriteSignal(SignalKeys.ThreatIntelClassifications, string.Join(";", classifications));
        state.WriteSignal(SignalKeys.ThreatIntelProvidersHit, string.Join(";", providers));
        state.WriteSignal(SignalKeys.ThreatIntelTor, torFlag);
        if (kevMatch is not null)
            state.WriteSignal(SignalKeys.ThreatIntelKevMatch, kevMatch);
    }
}

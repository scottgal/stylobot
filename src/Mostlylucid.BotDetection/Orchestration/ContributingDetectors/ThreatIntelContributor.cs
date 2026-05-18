using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.ThreatIntel;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Threat-intel evidence contributor. Reads cached verdicts from
///     <see cref="IThreatIntelCoordinator"/> and projects them onto the
///     blackboard as <c>threatintel.*</c> + <c>intel.*</c> + <c>endpoint.*</c>
///     signals. Crucially, this is a <em>separate evidence lane</em> from
///     behavioural detection - the contribution weight is modulated by
///     <see cref="EndpointRiskClassifier">endpoint risk</see> so the same
///     intel verdict acts very differently depending on what's being touched:
///
///     <list type="bullet">
///       <item>Static asset path → evidence only (no probability bump).</item>
///       <item>Normal path → weak per-class additive evidence (default
///         0.08-0.20, capped at <c>generic_max_confidence</c>).</item>
///       <item>Sensitive path (login / admin / .env / .git / api/token / …) →
///         strong modifier. <c>intel.hard_gate = true</c> signal lets the
///         policy layer respond hard (challenge, throttle-status, block)
///         without needing to interpret raw scores.</item>
///     </list>
///
///     <para>This avoids two failure modes spec'd in
///     <c>docs/architecture/threat-intel.md</c>: turning StyloBot into a dumb
///     blacklist on a single CIDR-match (uniform additive weight on benign
///     pages produces too many false positives), AND being too soft on a
///     known-bad source probing <c>/wp-login.php</c> just because the
///     behavioural score hasn't crossed a threshold yet.</para>
/// </summary>
public class ThreatIntelContributor : ConfiguredContributorBase
{
    private readonly IThreatIntelCoordinator _coordinator;
    private readonly ThreatIntelEnrichmentQueue? _enrichmentQueue;
    private readonly ILogger<ThreatIntelContributor> _logger;

    public ThreatIntelContributor(
        ILogger<ThreatIntelContributor> logger,
        IDetectorConfigProvider configProvider,
        IThreatIntelCoordinator coordinator,
        ThreatIntelEnrichmentQueue? enrichmentQueue = null)
        : base(configProvider)
    {
        _logger = logger;
        _coordinator = coordinator;
        _enrichmentQueue = enrichmentQueue;
    }

    public override string Name => "ThreatIntel";
    public override int Priority => Manifest?.Priority ?? 7;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Per-class additive weights for Normal endpoints. Operators tune via YAML
    // defaults.parameters. Generic-case weights are deliberately weak so a single
    // CIDR hit doesn't blanket-block benign traffic; the sensitive-endpoint path
    // applies the multiplier below to give them real teeth.
    private double WeightReputation             => GetParam("class_reputation_weight", 0.10);
    private double WeightVulnerability          => GetParam("class_vulnerability_weight", 0.20);
    private double WeightScannerInfrastructure  => GetParam("class_scanner_infra_weight", 0.12);
    private double WeightExploitCampaign        => GetParam("class_exploit_campaign_weight", 0.25);
    private double WeightKnownBadAutomation     => GetParam("class_known_bad_weight", 0.15);
    private double WeightSuspiciousNetworkRange => GetParam("class_suspicious_netrange_weight", 0.10);
    private double WeightCloudInfrastructure    => GetParam("class_cloud_weight", 0.0);    // identification only
    private double WeightTorExit                => GetParam("class_tor_weight", 0.10);

    /// <summary>Generic-case ceiling: even a stack of high-class hits can't pin past this on a Normal endpoint.</summary>
    private double GenericMaxConfidence  => GetParam("generic_max_confidence", 0.35);

    /// <summary>Multiplier applied to per-class weights when endpoint.risk = Sensitive.</summary>
    private double SensitiveMultiplier   => GetParam("sensitive_endpoint_multiplier", 3.0);

    /// <summary>Floor on threatintel.score when CISA KEV matches an extracted CVE on a Sensitive endpoint.</summary>
    private double KevMatchSensitiveFloor => GetParam("kev_match_sensitive_floor", 0.85);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Short-circuit ASAP when threat-intel is disabled (FOSS default) - skips
        // the regex on the path, two WriteSignal calls, and the rest of the pipeline.
        // endpoint.risk + endpoint.risk_sensitive only fire when at least one intel
        // provider is configured, which keeps the disabled-state cost at "one
        // boolean check + Task.FromResult".
        if (!_coordinator.IsEnabled)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        var path = state.HttpContext?.Request?.Path.Value;
        var risk = EndpointRiskClassifier.Classify(path);
        state.WriteSignal(SignalKeys.EndpointRisk, risk.ToString());
        state.WriteSignal(SignalKeys.EndpointRiskSensitive, risk == EndpointRisk.Sensitive);

        var verdicts = new List<ThreatIntelVerdict>(capacity: 4);

        if (state.Signals.TryGetValue(SignalKeys.ClientIp, out var ipObj) && ipObj is string ip && !string.IsNullOrEmpty(ip))
        {
            var ipSubject = new ThreatSubject(ThreatSubjectType.Ip, ip);
            foreach (var v in _coordinator.Lookup(ipSubject)) verdicts.Add(v);

            if (_enrichmentQueue is not null && AnyLiveProviderSupports(ThreatSubjectType.Ip)
                && !HasLiveProviderVerdict(verdicts))
            {
                _enrichmentQueue.TryEnqueue(ipSubject);
            }
        }

        var cveId = TryGetCveId(state);
        if (cveId is not null)
        {
            foreach (var v in _coordinator.Lookup(new ThreatSubject(ThreatSubjectType.Cve, cveId)))
                verdicts.Add(v);
        }

        if (verdicts.Count == 0)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        EmitSignals(state, verdicts, cveId, risk);

        var contributions = BuildContributions(verdicts, cveId, risk);
        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private IReadOnlyList<DetectionContribution> BuildContributions(
        List<ThreatIntelVerdict> verdicts, string? cveId, EndpointRisk risk)
    {
        if (risk == EndpointRisk.Static)
        {
            // Static asset path: emit signals only, no probability bump. The intel
            // is still searchable in the dashboard but never blocks an image fetch.
            return Array.Empty<DetectionContribution>();
        }

        // Per-class sum of weights, deduped per class so two providers in the same
        // class don't double-count (Spamhaus DROP + Spamhaus EDROP both produce
        // SuspiciousNetworkRange - we want one class hit, not two).
        var classHits = new HashSet<IntelligenceSignalClass>();
        var weight = 0.0;
        ThreatIntelVerdict? top = null;
        var topW = 0.0;
        foreach (var v in verdicts)
        {
            if (!classHits.Add(v.IntelligenceClass)) continue;
            var w = WeightFor(v.IntelligenceClass);
            weight += w;
            if (w > topW) { topW = w; top = v; }
        }

        if (risk == EndpointRisk.Sensitive)
        {
            weight = Math.Min(1.0, weight * SensitiveMultiplier);
            // KEV at a sensitive endpoint earns the harder floor (this overlays the
            // multiplier above; whichever is higher wins).
            if (classHits.Contains(IntelligenceSignalClass.Vulnerability) && cveId is not null)
                weight = Math.Max(weight, KevMatchSensitiveFloor);
        }
        else
        {
            weight = Math.Min(GenericMaxConfidence, weight);
        }

        if (weight <= 0 || top is null)
            return Array.Empty<DetectionContribution>();

        var reason = $"{top.Provider} {top.Classification} ({top.IntelligenceClass})";
        if (classHits.Count > 1) reason += $" + {classHits.Count - 1} more class(es)";
        if (risk == EndpointRisk.Sensitive) reason += " on sensitive endpoint";

        return new[] { BotContribution("ThreatIntel", reason, confidenceOverride: weight) };
    }

    private double WeightFor(IntelligenceSignalClass cls) => cls switch
    {
        IntelligenceSignalClass.Reputation             => WeightReputation,
        IntelligenceSignalClass.Vulnerability          => WeightVulnerability,
        IntelligenceSignalClass.ScannerInfrastructure  => WeightScannerInfrastructure,
        IntelligenceSignalClass.ExploitCampaign        => WeightExploitCampaign,
        IntelligenceSignalClass.KnownBadAutomation     => WeightKnownBadAutomation,
        IntelligenceSignalClass.SuspiciousNetworkRange => WeightSuspiciousNetworkRange,
        IntelligenceSignalClass.CloudInfrastructure    => WeightCloudInfrastructure,
        IntelligenceSignalClass.TorExit                => WeightTorExit,
        _                                              => 0.05
    };

    private bool AnyLiveProviderSupports(ThreatSubjectType type)
    {
        foreach (var p in _coordinator.Providers)
            if (p.Mode == ThreatIntelMode.Live && p.SupportedSubjects.Contains(type))
                return true;
        return false;
    }

    private bool HasLiveProviderVerdict(List<ThreatIntelVerdict> verdicts)
    {
        foreach (var v in verdicts)
            if (IsLiveProvider(v.Provider)) return true;
        return false;
    }

    private bool IsLiveProvider(string providerName)
    {
        foreach (var p in _coordinator.Providers)
            if (p.Name == providerName) return p.Mode == ThreatIntelMode.Live;
        return false;
    }

    private static string? TryGetCveId(BlackboardState state)
    {
        if (state.Signals.TryGetValue(SignalKeys.CveProbeId, out var probeObj) && probeObj is string probe
            && probe.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            return probe.ToUpperInvariant();
        if (state.Signals.TryGetValue(SignalKeys.CveTopAdvisoryId, out var advObj) && advObj is string adv
            && adv.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            return adv.ToUpperInvariant();
        return null;
    }

    private static void EmitSignals(
        BlackboardState state, List<ThreatIntelVerdict> verdicts, string? cveId, EndpointRisk risk)
    {
        var maxConfidence = 0.0;
        var classifications = new HashSet<string>(StringComparer.Ordinal);
        var providers = new HashSet<string>(StringComparer.Ordinal);
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var torFlag = false;
        string? kevMatch = null;
        foreach (var v in verdicts)
        {
            if (v.Confidence > maxConfidence) maxConfidence = v.Confidence;
            classifications.Add(v.Classification);
            providers.Add(v.Provider);
            classes.Add(v.IntelligenceClass.ToWireString());
            if (v.Classification == "tor") torFlag = true;
            if (v.Classification == "kev" && cveId is not null) kevMatch = cveId;
            state.WriteSignal($"threatintel.{v.Provider}", v.Classification);
        }
        state.WriteSignal(SignalKeys.ThreatIntelScore, maxConfidence);
        state.WriteSignal(SignalKeys.ThreatIntelClassifications, string.Join(";", classifications));
        state.WriteSignal(SignalKeys.ThreatIntelProvidersHit, string.Join(";", providers));
        state.WriteSignal(SignalKeys.ThreatIntelTor, torFlag);
        state.WriteSignal(SignalKeys.IntelClasses, string.Join(";", classes));
        if (kevMatch is not null)
            state.WriteSignal(SignalKeys.ThreatIntelKevMatch, kevMatch);
        // Hard-gate convenience: intel evidence + risky surface in one signal so
        // transitions can fire on a single boolean. Cloud-only verdicts don't
        // qualify because CloudInfrastructure is identification, not risk.
        var nonCloudHit = false;
        foreach (var v in verdicts)
        {
            if (v.IntelligenceClass != IntelligenceSignalClass.CloudInfrastructure)
            {
                nonCloudHit = true;
                break;
            }
        }
        state.WriteSignal(SignalKeys.IntelHardGate, nonCloudHit && risk == EndpointRisk.Sensitive);
    }
}

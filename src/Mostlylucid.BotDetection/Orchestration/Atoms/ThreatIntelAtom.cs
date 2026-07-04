using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.ThreatIntel;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that projects
///     <see cref="IThreatIntelCoordinator"/> verdicts onto the sink and
///     scores them against endpoint risk. Priority 7.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ThreatIntelContributor</c>. Preserves the three-way path-risk
///         attenuation: static asset -> evidence only; normal -> weak
///         additive; sensitive -> multiplied + KEV floor + hard-gate signal.
///     </para>
///     <para>
///         FOSS default has <c>_coordinator.IsEnabled == false</c>; the atom
///         short-circuits at zero cost. Everything downstream is authored to
///         key off <c>threatintel.*</c> presence, so absence is the "not
///         checked" state.
///     </para>
/// </remarks>
public sealed class ThreatIntelAtom : DetectorAtomBase
{
    private readonly IThreatIntelCoordinator _coordinator;
    private readonly ThreatIntelEnrichmentQueue? _enrichmentQueue;
    private readonly ILogger<ThreatIntelAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ThreatIntelAtom(
        ILogger<ThreatIntelAtom> logger,
        IDetectorConfigProvider configProvider,
        IThreatIntelCoordinator coordinator,
        IHttpContextAccessor httpContextAccessor,
        ThreatIntelEnrichmentQueue? enrichmentQueue = null)
        : base(name: "ThreatIntel", category: "ThreatIntel")
    {
        _logger = logger;
        _coordinator = coordinator;
        _enrichmentQueue = enrichmentQueue;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 7;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double WeightReputation             => _configProvider.GetParameter(Name, "class_reputation_weight", 0.10);
    private double WeightVulnerability          => _configProvider.GetParameter(Name, "class_vulnerability_weight", 0.20);
    private double WeightScannerInfrastructure  => _configProvider.GetParameter(Name, "class_scanner_infra_weight", 0.12);
    private double WeightExploitCampaign        => _configProvider.GetParameter(Name, "class_exploit_campaign_weight", 0.25);
    private double WeightKnownBadAutomation     => _configProvider.GetParameter(Name, "class_known_bad_weight", 0.15);
    private double WeightSuspiciousNetworkRange => _configProvider.GetParameter(Name, "class_suspicious_netrange_weight", 0.10);
    private double WeightCloudInfrastructure    => _configProvider.GetParameter(Name, "class_cloud_weight", 0.0);
    private double WeightTorExit                => _configProvider.GetParameter(Name, "class_tor_weight", 0.10);
    private double GenericMaxConfidence         => _configProvider.GetParameter(Name, "generic_max_confidence", 0.35);
    private double SensitiveMultiplier          => _configProvider.GetParameter(Name, "sensitive_endpoint_multiplier", 3.0);
    private double KevMatchSensitiveFloor       => _configProvider.GetParameter(Name, "kev_match_sensitive_floor", 0.85);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // Zero-cost short-circuit when threat-intel is off.
        if (!_coordinator.IsEnabled)
            return Task.FromResult(None());

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var path = context.Request.Path.Value;
        var risk = EndpointRiskClassifier.Classify(path);

        sink.Raise($"{SignalKeys.EndpointRisk}:{risk}", sessionId);
        sink.Raise($"{SignalKeys.EndpointRiskSensitive}:{(risk == EndpointRisk.Sensitive ? "true" : "false")}", sessionId);

        var verdicts = new List<ThreatIntelVerdict>(capacity: 4);

        var clientIp = sink.ReadHint(SignalKeys.ClientIp) ?? context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(clientIp))
        {
            var ipSubject = new ThreatSubject(ThreatSubjectType.Ip, clientIp);
            foreach (var v in _coordinator.Lookup(ipSubject)) verdicts.Add(v);

            if (_enrichmentQueue is not null && AnyLiveProviderSupports(ThreatSubjectType.Ip)
                && !HasLiveProviderVerdict(verdicts))
            {
                _enrichmentQueue.TryEnqueue(ipSubject);
            }
        }

        var cveId = TryGetCveId(sink);
        if (cveId is not null)
        {
            foreach (var v in _coordinator.Lookup(new ThreatSubject(ThreatSubjectType.Cve, cveId)))
                verdicts.Add(v);
        }

        if (verdicts.Count == 0) return Task.FromResult(None());

        EmitSignals(sink, sessionId, verdicts, cveId, risk);

        var contributions = BuildContributions(verdicts, cveId, risk);
        return Task.FromResult(contributions);
    }

    private IReadOnlyList<DetectionContribution> BuildContributions(
        List<ThreatIntelVerdict> verdicts, string? cveId, EndpointRisk risk)
    {
        if (risk == EndpointRisk.Static) return Array.Empty<DetectionContribution>();

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
            if (classHits.Contains(IntelligenceSignalClass.Vulnerability) && cveId is not null)
                weight = Math.Max(weight, KevMatchSensitiveFloor);
        }
        else
        {
            weight = Math.Min(GenericMaxConfidence, weight);
        }

        if (weight <= 0 || top is null) return Array.Empty<DetectionContribution>();

        var reason = $"{top.Provider} {top.Classification} ({top.IntelligenceClass})";
        if (classHits.Count > 1) reason += $" + {classHits.Count - 1} more class(es)";
        if (risk == EndpointRisk.Sensitive) reason += " on sensitive endpoint";

        return new[]
        {
            new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = weight,
                Weight = 1.0,
                Reason = reason,
                BotType = BotType.Unknown.ToString()
            }
        };
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

    private static string? TryGetCveId(SignalSink sink)
    {
        var probe = sink.ReadHint(SignalKeys.CveProbeId);
        if (!string.IsNullOrEmpty(probe) && probe.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            return probe.ToUpperInvariant();
        var advisory = sink.ReadHint(SignalKeys.CveTopAdvisoryId);
        if (!string.IsNullOrEmpty(advisory) && advisory.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            return advisory.ToUpperInvariant();
        return null;
    }

    private static void EmitSignals(
        SignalSink sink, string sessionId, List<ThreatIntelVerdict> verdicts, string? cveId, EndpointRisk risk)
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
            sink.Raise($"threatintel.{v.Provider}:{v.Classification}", sessionId);
        }
        sink.Raise($"{SignalKeys.ThreatIntelScore}:{maxConfidence.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.ThreatIntelClassifications}:{string.Join(";", classifications)}", sessionId);
        sink.Raise($"{SignalKeys.ThreatIntelProvidersHit}:{string.Join(";", providers)}", sessionId);
        sink.Raise($"{SignalKeys.ThreatIntelTor}:{(torFlag ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.IntelClasses}:{string.Join(";", classes)}", sessionId);
        if (kevMatch is not null)
            sink.Raise($"{SignalKeys.ThreatIntelKevMatch}:{kevMatch}", sessionId);

        var nonCloudHit = false;
        foreach (var v in verdicts)
        {
            if (v.IntelligenceClass != IntelligenceSignalClass.CloudInfrastructure)
            {
                nonCloudHit = true;
                break;
            }
        }
        sink.Raise($"{SignalKeys.IntelHardGate}:{(nonCloudHit && risk == EndpointRisk.Sensitive ? "true" : "false")}", sessionId);
    }
}

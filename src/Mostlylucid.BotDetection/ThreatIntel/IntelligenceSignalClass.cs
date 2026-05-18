namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     What kind of risk prior a threat-intel verdict represents. Lets the
///     contributor + policy layer treat different classes with different
///     operational weights (a Vulnerability signal at <c>/.env</c> is far
///     stronger than the same signal at <c>/static/logo.png</c>) rather than
///     summing them all into one BotProbability.
///
///     <para>The framing: threat-intel signals are NOT behavioural truth ("this
///     request behaves like X") — they're <em>contextual priors</em> ("this
///     request comes from a context where X is plausible"). They should land in
///     a separate evidence lane and be modulated by endpoint risk.</para>
/// </summary>
public enum IntelligenceSignalClass
{
    /// <summary>Default / unclassified. Treat as the weakest tier.</summary>
    Unknown,

    /// <summary>IP / netblock with a community-reported abuse history (AbuseIPDB, Spamhaus-style).</summary>
    Reputation,

    /// <summary>IP block known to host vulnerable infrastructure OR a CVE-keyed verdict (CISA KEV).</summary>
    Vulnerability,

    /// <summary>Source is known infrastructure for opportunistic internet-wide scanning (GreyNoise noise / scanner).</summary>
    ScannerInfrastructure,

    /// <summary>Source ties to an identified exploit campaign or botnet (commercial-only providers typically).</summary>
    ExploitCampaign,

    /// <summary>Generic known-bad automation (datacenter UA + cookie-stripping etc., from a feed).</summary>
    KnownBadAutomation,

    /// <summary>Netblock is allocated for abuse or never legitimately routed (Spamhaus DROP / EDROP).</summary>
    SuspiciousNetworkRange,

    /// <summary>Cloud-provider netblock (AWS / Azure / GCP / etc.). Informational; not malicious on its own.</summary>
    CloudInfrastructure,

    /// <summary>Tor exit. Privacy-protecting but coverage-shaped detection should still see it.</summary>
    TorExit
}

/// <summary>
///     Stable string mapping for <see cref="IntelligenceSignalClass"/>. Used by
///     <c>ThreatIntelContributor</c> to format the <c>intel.classes</c> signal
///     value so renaming an enum member doesn't silently break dashboard filters
///     or policy transition rules that key on the wire string.
///     <para>The mapping is fixed forever - dashboards, alerts, and external
///     consumers can rely on these literal strings. Adding a new enum member
///     requires adding a new mapping; renaming an existing member must NOT
///     change its wire string.</para>
/// </summary>
public static class IntelligenceSignalClassExtensions
{
    /// <summary>
    ///     Stable, snake-case wire representation. Returns <c>"unknown"</c> for
    ///     enum values that don't have an explicit mapping (defends against
    ///     future enum additions that forget to update this map).
    /// </summary>
    public static string ToWireString(this IntelligenceSignalClass cls) => cls switch
    {
        IntelligenceSignalClass.Reputation             => "reputation",
        IntelligenceSignalClass.Vulnerability          => "vulnerability",
        IntelligenceSignalClass.ScannerInfrastructure  => "scanner_infrastructure",
        IntelligenceSignalClass.ExploitCampaign        => "exploit_campaign",
        IntelligenceSignalClass.KnownBadAutomation     => "known_bad_automation",
        IntelligenceSignalClass.SuspiciousNetworkRange => "suspicious_network_range",
        IntelligenceSignalClass.CloudInfrastructure    => "cloud_infrastructure",
        IntelligenceSignalClass.TorExit                => "tor_exit",
        _                                              => "unknown"
    };
}

/// <summary>
///     Coarse risk classification for the request's path. Drives how aggressively
///     the threat-intel contributor (and downstream policies) act on intelligence
///     verdicts. Derived in <see cref="EndpointRiskClassifier"/> from a small
///     FOSS-generic pattern list; commercial wires per-endpoint operator overrides
///     on top.
/// </summary>
public enum EndpointRisk
{
    /// <summary>Static assets (images, CSS, JS, fonts). Threat-intel signals are evidence only — never block.</summary>
    Static,

    /// <summary>Default category. Standard scoring + standard policy.</summary>
    Normal,

    /// <summary>Authn / authz / admin / payment / config-leak / VCS-leak surfaces. Intelligence signals become strong modifiers.</summary>
    Sensitive
}

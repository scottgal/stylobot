namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     What kind of thing a threat-intel lookup is keyed on.
///     Most providers are IP-keyed; CISA KEV is CVE-keyed; future TLS-fingerprint
///     feeds will use JA3/JA4. Adding a new value here means providers can opt into
///     supporting it via <see cref="IThreatIntelProvider.SupportedSubjects"/>.
/// </summary>
public enum ThreatSubjectType
{
    Ip,
    Asn,
    Domain,
    Cidr,
    Cve,
    JA3,
    JA4
}

/// <summary>
///     A single lookup key for the threat-intel coordinator. The <paramref name="Value"/>
///     is the canonical string form for the subject type (dotted-quad for IPv4,
///     uppercase CVE-YYYY-NNNN for CVEs, etc.). Providers normalise on the way in.
/// </summary>
public sealed record ThreatSubject(ThreatSubjectType Type, string Value);

/// <summary>
///     Lookup mode for a provider. Offline providers hold the entire dataset in
///     memory after sync and answer <see cref="IThreatIntelProvider.TryLookup"/>
///     with a pure cache hit. Live providers can either return a cached prior
///     response or null when the subject hasn't been enriched yet - in the null
///     case the coordinator queues a background enrichment but never blocks the
///     request.
/// </summary>
public enum ThreatIntelMode
{
    Offline,
    Live
}

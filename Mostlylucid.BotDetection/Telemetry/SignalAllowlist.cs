namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Default set of high-value signal keys promoted to span attributes.
///     These ~30 keys cover the most useful signals for tracing and debugging.
/// </summary>
public static class SignalAllowlist
{
    /// <summary>
    ///     Default signal keys promoted to OTel span attributes.
    /// </summary>
    public static readonly HashSet<string> Default = new(StringComparer.OrdinalIgnoreCase)
    {
        // Risk & Classification
        "risk.band",
        "risk.score",
        "ai.prediction",
        "ai.confidence",
        "heuristic.prediction",
        "heuristic.confidence",

        // Identity
        "ua.bot_type",
        "ua.bot_name",
        "ua.family",
        "verifiedbot.confirmed",
        "verifiedbot.name",
        "verifiedbot.spoofed",

        // Network
        "ip.is_datacenter",
        "ip.provider",
        "ip.asn_org",
        "geo.country_code",
        "geo.is_vpn",
        "geo.is_tor",
        "geo.is_hosting",

        // Behavioral
        "behavioral.rate_exceeded",
        "behavioral.anomaly",
        "cache.rapid_repeated",
        "cache.behavior_anomaly",

        // Attack
        "attack.detected",
        "attack.categories",
        "attack.severity",
        "ato.detected",
        "ato.credential_stuffing",

        // Fingerprint
        "fingerprint.headless_score",
        "fingerprint.integrity_score",
        "tls.protocol",
        "h2.client_type",

        // Cluster
        "cluster.id",
        "cluster.member_count",
    };
}

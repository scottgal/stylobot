using Mostlylucid.BotDetection.ThreatIntel.Providers;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Top-level config block under <c>BotDetection:ThreatIntel</c>. FOSS default
///     posture is everything off: the master switch is off and each provider's
///     Enabled flag is off independently. Operator opts in per-provider. See
///     <c>docs/architecture/threat-intel.md</c> for the full rationale.
/// </summary>
public sealed class ThreatIntelOptions
{
    /// <summary>Master enable. When false, the coordinator + refresh service short-circuit and no provider work runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Privacy posture for live providers. Has no effect on offline providers
    ///     (they don't transmit anything to the vendor at lookup time).
    ///     <list type="bullet">
    ///       <item><c>ip</c>: send the raw IP (vendor default expectation)</item>
    ///       <item><c>redacted-ip</c>: /24 truncate IPv4 before sending</item>
    ///       <item><c>hash</c>: HMAC-SHA256 the IP (rare; only works with providers that accept hashed lookups)</item>
    ///       <item><c>offline-only</c>: never call live providers</item>
    ///     </list>
    /// </summary>
    public ThreatIntelPrivacyMode PrivacyMode { get; set; } = ThreatIntelPrivacyMode.Ip;

    /// <summary>
    ///     When <see cref="Enabled"/> is true AND any provider is enabled, block service startup
    ///     until each enabled provider completes its first refresh (or its
    ///     <see cref="StartupFetchTimeoutSeconds"/> elapses). Set false to let the cache populate
    ///     opportunistically while the host starts serving requests.
    /// </summary>
    public bool BlockStartupOnFirstFetch { get; set; } = true;

    /// <summary>Per-provider startup-fetch timeout. Past this we fail fast (or warn-and-continue when blocking is off).</summary>
    public int StartupFetchTimeoutSeconds { get; set; } = 60;

    /// <summary>
    ///     Background-refresh stagger window. Each provider's post-bootstrap refresh
    ///     fires at <c>now + Random(0..StaggerWindowSeconds)</c> then ticks on its own
    ///     <c>RefreshInterval</c> from there. Avoids N concurrent fetches on the same tick.
    /// </summary>
    public int StaggerWindowSeconds { get; set; } = 300;

    /// <summary>Per-provider option blocks.</summary>
    public ThreatIntelProviderOptions Providers { get; set; } = new();
}

/// <summary>Container for per-provider option classes. New providers add a new property here.</summary>
public sealed class ThreatIntelProviderOptions
{
    public SpamhausDropOptions SpamhausDrop { get; set; } = new();
    public TorExitOptions TorExit { get; set; } = new();
    public CisaKevOptions CisaKev { get; set; } = new();
    public CloudRangesOptions CloudRanges { get; set; } = new();
}

/// <summary>Privacy posture for live-provider lookups. See <see cref="ThreatIntelOptions.PrivacyMode"/>.</summary>
public enum ThreatIntelPrivacyMode
{
    Ip,
    RedactedIp,
    Hash,
    OfflineOnly
}

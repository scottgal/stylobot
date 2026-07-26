namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     The per-fingerprint surface dimensions <see cref="Orchestration.Atoms.IdentityChangeAtom"/>
///     compares request-to-request to detect a profile swap under a matched identity
///     (geo country / ASN / UA family / datacenter+Tor introduction / canvas-WebGL shape
///     hash / BotD verdict).
///     <para>
///     These are EPHEMERAL last-seen values, not durable identity facts. They live only in
///     the fingerprint store's in-memory hot cache (co-indexed on the same
///     <c>_fingerprintById</c> entry as the fingerprint, co-evicted with it) and are NEVER
///     persisted to SQLite. Folding them here (rather than a separate per-fingerprint cache)
///     is the #16 gateway-OOM fix: a separate accumulator grew one unbounded entry per
///     rotated fingerprint; riding the bounded fingerprint cache means a rotated fingerprint
///     we no longer cache simply drops its dims and re-baselines.
///     </para>
/// </summary>
public sealed record SurfaceDims(
    string Country,
    string Asn,
    string UaFamily,
    bool IsDatacenter,
    bool IsTorOrVpn,
    DateTimeOffset LastSeenUtc,
    // Canvas + WebGL "shape" hash. Hardware-derived, effectively immutable for real
    // users; under the same fingerprint id, a change is the canonical Multilogin /
    // Kameleo profile-swap signal.
    string ShapeHash = "",
    // BotD verdict ("selenium", "puppeteer", "headless_chrome", ...). Drift here means
    // the automation framework changed under the same identity — rare for real operators.
    string BotdKind = "");

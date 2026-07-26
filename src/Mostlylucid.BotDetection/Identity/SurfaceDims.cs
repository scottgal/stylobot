namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     The per-fingerprint surface dimensions used to detect a profile swap under a matched
///     identity (geo country / ASN / UA family / datacenter+Tor introduction / canvas-WebGL
///     shape hash / BotD verdict).
///     <para>
///     These are EPHEMERAL shape values, not durable identity facts. They ride the single
///     bounded per-fingerprint hot cache entry (<c>SqliteFingerprintStore._fingerprintById</c>)
///     as TWO transient, in-memory-only, co-evicted fields:
///     <list type="bullet">
///         <item><c>EstablishedDims</c> — the fingerprint's held/baseline shape.</item>
///         <item><c>PendingDims</c> — the most-recently-observed dims, stamped per request by
///         <see cref="Orchestration.Atoms.IdentityChangeAtom"/> via
///         <see cref="IFingerprintStore.StampObservedDims"/>.</item>
///     </list>
///     Neither is ever persisted to SQLite. Riding the bounded fingerprint cache (rather than a
///     separate per-fingerprint accumulator) is the #16 gateway-OOM fix: a rotated fingerprint
///     we no longer cache simply drops its dims and re-baselines when it next resolves.
///     </para>
///     <para>
///     Drift is NOT raised per request. It is detected once, at the session → fingerprint
///     ABSORPTION boundary (<see cref="FingerprintAbsorptionService"/>), by diffing
///     <c>PendingDims</c> against <c>EstablishedDims</c>. On a change the store folds a bounded,
///     fixed-width, DURABLE drift SUMMARY onto the fingerprint row (a 7-float per-dim EWMA
///     change-magnitude vector + an EWMA change-frequency scalar) — the "frequently-drifting
///     fingerprint = anti-detect signal" accumulator — then promotes established = pending.
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
    string BotdKind = "")
{
    /// <summary>Fixed width of the per-dim drift-magnitude vector (one slot per surface dim).</summary>
    public const int DriftDimCount = 7;

    // Stable slot indices into the DriftDimCount-wide per-dim change vector. Kept as named
    // constants (no magic numbers) so both the diff producer and the dashboard reader agree.
    public const int CountryIndex = 0;
    public const int AsnIndex = 1;
    public const int UaFamilyIndex = 2;
    public const int DatacenterIndex = 3;
    public const int TorVpnIndex = 4;
    public const int ShapeHashIndex = 5;
    public const int BotdKindIndex = 6;

    /// <summary>
    ///     Pure comparison of the ESTABLISHED (baseline) shape against the LATEST observed shape,
    ///     producing the fixed-width per-dim change vector consumed at the absorption boundary.
    ///     A slot is 1.0 when that dimension changed, 0.0 otherwise. Same change rules the
    ///     request-path atom used to apply:
    ///     <list type="bullet">
    ///         <item>categorical dims (country / ASN / UA family / shape hash / BotD kind) change
    ///         only when BOTH values are non-empty and differ — an empty side is "unknown", not a
    ///         change, so it never manufactures drift;</item>
    ///         <item>infra dims (datacenter / Tor-or-VPN) change only on NEWLY-INTRODUCED
    ///         infrastructure (established false → latest true), never on its disappearance.</item>
    ///     </list>
    ///     Values are 1.0/0.0 rather than weighted so the durable summary stays a normalized
    ///     per-dim change rate after EWMA folding; policy weighting is applied by consumers.
    /// </summary>
    public static float[] ComputeDriftMagnitudes(SurfaceDims established, SurfaceDims latest)
    {
        var mag = new float[DriftDimCount];

        if (CategoricalChanged(established.Country, latest.Country, StringComparison.OrdinalIgnoreCase))
            mag[CountryIndex] = 1.0f;
        if (CategoricalChanged(established.Asn, latest.Asn, StringComparison.OrdinalIgnoreCase))
            mag[AsnIndex] = 1.0f;
        if (CategoricalChanged(established.UaFamily, latest.UaFamily, StringComparison.OrdinalIgnoreCase))
            mag[UaFamilyIndex] = 1.0f;
        if (!established.IsDatacenter && latest.IsDatacenter)
            mag[DatacenterIndex] = 1.0f;
        if (!established.IsTorOrVpn && latest.IsTorOrVpn)
            mag[TorVpnIndex] = 1.0f;
        if (CategoricalChanged(established.ShapeHash, latest.ShapeHash, StringComparison.Ordinal))
            mag[ShapeHashIndex] = 1.0f;
        if (CategoricalChanged(established.BotdKind, latest.BotdKind, StringComparison.OrdinalIgnoreCase))
            mag[BotdKindIndex] = 1.0f;

        return mag;
    }

    private static bool CategoricalChanged(string prior, string current, StringComparison cmp)
        => !string.IsNullOrEmpty(prior)
           && !string.IsNullOrEmpty(current)
           && !string.Equals(prior, current, cmp);
}

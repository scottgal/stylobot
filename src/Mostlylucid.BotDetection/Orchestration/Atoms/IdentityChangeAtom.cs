using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Per-request STAMP of the matched fingerprint's latest surface dimensions
///     (geo country, ASN, UA family, datacenter / Tor introduction, canvas-WebGL
///     shape hash, BotD verdict) onto the fingerprint's transient hot-cache entry.
/// </summary>
/// <remarks>
///     <para>
///         Detection of surface-dim DRIFT no longer happens per request. This atom's sole
///         job is to build the current <see cref="SurfaceDims"/> from the sink hints and
///         stamp them as the fingerprint's <c>PendingDims</c> via
///         <see cref="IFingerprintStore.StampObservedDims"/>. The actual drift compare —
///         PendingDims vs EstablishedDims — runs once, at the session → fingerprint
///         ABSORPTION boundary (<see cref="FingerprintAbsorptionService"/>), where a change
///         folds a bounded, durable per-fingerprint drift summary. The accumulated
///         change-frequency is the durable signal; there are no per-request <c>risk.*</c>
///         drift signals any more.
///     </para>
///     <para>
///         The dims ride the single bounded per-fingerprint hot cache in
///         <see cref="IFingerprintStore"/> (co-indexed with the fingerprint entry,
///         co-evicted, never persisted) — not a separate cache. That fold is the #16
///         gateway-OOM fix. Priority 30, RequiredSignals(<c>identity.fingerprint_id</c>) so it
///         still runs per request once the fingerprint has been resolved.
///     </para>
/// </remarks>
public sealed class IdentityChangeAtom : DetectorAtomBase
{
    private readonly ILogger<IdentityChangeAtom> _logger;
    private readonly IFingerprintStore _store;
    private readonly IDetectorConfigProvider _configProvider;

    public IdentityChangeAtom(
        ILogger<IdentityChangeAtom> logger,
        IDetectorConfigProvider configProvider,
        IFingerprintStore store)
        : base(name: "IdentityChange", category: "IdentityChange")
    {
        _logger = logger;
        _store = store;
        _configProvider = configProvider;
    }

    public override int Priority => 30;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.IdentityFingerprintId };

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var fingerprintId = sink.ReadHint(SignalKeys.IdentityFingerprintId);
        if (string.IsNullOrEmpty(fingerprintId))
            return Task.FromResult(None());

        var current = new SurfaceDims(
            Country: sink.ReadHint(SignalKeys.GeoCountryCode) ?? string.Empty,
            Asn: sink.ReadHint(SignalKeys.IpAsn) ?? string.Empty,
            UaFamily: sink.ReadHint(SignalKeys.UserAgentFamily) ?? string.Empty,
            IsDatacenter: sink.ReadBoolHint(SignalKeys.IpIsDatacenter),
            IsTorOrVpn: sink.ReadBoolHint(SignalKeys.GeoIsTor)
                     || sink.ReadBoolHint(SignalKeys.GeoIsVpn)
                     || sink.ReadBoolHint(SignalKeys.ThreatIntelTor),
            LastSeenUtc: DateTimeOffset.UtcNow,
            ShapeHash: sink.ReadHint(SignalKeys.ClientSideShapeHash) ?? string.Empty,
            BotdKind: sink.ReadHint(SignalKeys.ClientSideBotdKind) ?? string.Empty);

        // Stamp the latest observed dims as PendingDims. No-op if the fingerprint isn't
        // resident (never creates a phantom entry — the #16 leak). Drift is detected at the
        // absorption boundary, not here; this atom raises no signals and no contribution.
        _store.StampObservedDims(fingerprintId, current);

        return Task.FromResult(None());
    }
}

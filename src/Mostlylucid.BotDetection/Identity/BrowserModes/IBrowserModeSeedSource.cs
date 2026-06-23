namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     One seed entry consumed by <see cref="ModeCentroidCatalogue"/> on
///     cold-start. <see cref="ModeId"/> matches the id surfaced by
///     <see cref="BrowserModeRegistry.Classify"/>; <see cref="SeedCentroid"/>
///     is the canonical centroid the mode starts at (drift moves it from
///     here once observations land).
/// </summary>
public sealed record ModeSeed(string ModeId, float[] SeedCentroid);

/// <summary>
///     Source of the cold-start centroid seed set for the
///     <c>browser_mode</c> catalogue. Pulled out as an interface so the YAML
///     loader (T13) can drop in without touching <see cref="ModeCentroidCatalogue"/>
///     and so tests can swap in a fixed seed table -- the catalogue itself
///     stays oblivious to where the centroid prior comes from.
/// </summary>
public interface IBrowserModeSeedSource
{
    /// <summary>
    ///     Load the YAML-backed seed set. Returns one entry per known mode;
    ///     order is irrelevant -- the catalogue persists each row via
    ///     <see cref="IFingerprintStore.UpsertCentroidAsync"/> which is
    ///     idempotent on <c>archetype_id</c>.
    /// </summary>
    IReadOnlyList<ModeSeed> LoadYamlSeeds();
}

/// <summary>
///     Default seed source used until T13 fills in <c>centroid:</c> blocks on
///     the <c>Definitions/BrowserModes/*.yaml</c> files. Builds layout-sized
///     centroid vectors via <see cref="ModeVectorEncoder"/> so seeds live in
///     the SAME vector space as observations the classifier encodes at
///     request time. The dimensionality matches
///     <see cref="IdentityVectorLayout.Dimension"/> — mode centroids are
///     sparse identity vectors per the design spec
///     (<c>docs/superpowers/specs/2026-06-22-identity-mode-archetype-name-design.md</c>),
///     not a separate four-float subspace.
///
///     <para>
///     T13 will replace this with the YAML-backed source and keep the
///     interface stable. The seed values here are the design's "prior"; once
///     observations absorb into a row, drift updates the persisted centroid
///     and the YAML seed is never consulted again for that row.
///     </para>
/// </summary>
public sealed class HardcodedBrowserModeSeedSource : IBrowserModeSeedSource
{
    private readonly IdentityVectorLayout _layout;
    private readonly IReadOnlyList<ModeSeed> _seeds;

    public HardcodedBrowserModeSeedSource(IdentityVectorLayout layout)
    {
        _layout = layout;
        _seeds = BuildSeeds();
    }

    public IReadOnlyList<ModeSeed> LoadYamlSeeds() => _seeds;

    private IReadOnlyList<ModeSeed> BuildSeeds()
    {
        // Seeds are encoded via ModeVectorEncoder so the canonical method /
        // sec-fetch / upgrade slot writes match what the classifier produces
        // for a live observation. Per the design table in
        // docs/superpowers/specs/2026-06-22-identity-mode-archetype-name-design.md:
        //
        //   navigation         GET   navigate  upgrade=false
        //   xhr                GET   cors      upgrade=false
        //   sub-resource       GET   no-cors   upgrade=false
        //   signalr-negotiate  GET   cors      upgrade=false   (path-distinguished
        //                                                       in production; the
        //                                                       3-input classifier
        //                                                       can't separate it
        //                                                       from xhr, so seed
        //                                                       mirrors xhr and
        //                                                       iter-order picks
        //                                                       xhr first on tie —
        //                                                       T14 will fold path
        //                                                       into the catalogue
        //                                                       vector to break it)
        //   websocket-upgrade  GET   navigate  upgrade=true
        //   bot-raw            GET   (no sfm)  upgrade=false   (no Sec-Fetch headers
        //                                                       = bitmask 0 →
        //                                                       BitmaskScaled = -1)
        //   unknown            all zeros (sentinel)
        return new List<ModeSeed>
        {
            new("navigation",        ModeVectorEncoder.Encode(_layout, "GET",  "navigate", upgradeWebsocket: false)),
            new("xhr",               ModeVectorEncoder.Encode(_layout, "GET",  "cors",     upgradeWebsocket: false)),
            new("sub-resource",      ModeVectorEncoder.Encode(_layout, "GET",  "no-cors",  upgradeWebsocket: false)),
            new("signalr-negotiate", ModeVectorEncoder.Encode(_layout, "GET",  "cors",     upgradeWebsocket: false)),
            new("websocket-upgrade", ModeVectorEncoder.Encode(_layout, "GET",  "navigate", upgradeWebsocket: true)),
            new("bot-raw",           ModeVectorEncoder.Encode(_layout, "GET",  secFetchMode: null, upgradeWebsocket: false)),
            // The unknown sentinel is the catalogue's "didn't recognise
            // anything" row. Centroid is all-zeros so cosine only picks it
            // when no other centroid scores positive against the
            // observation, which is exactly the desired fallback semantics.
            new("unknown",           new float[_layout.Dimension]),
        };
    }
}
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
///     the <c>Definitions/BrowserModes/*.yaml</c> files. Hard-codes the seven
///     known modes' centroids per the design table in
///     <c>docs/superpowers/specs/2026-06-22-identity-mode-archetype-name-design.md</c>.
///     The dimensions here are intentionally tiny (4 floats) -- the
///     classifier (T12) projects the full <see cref="IdentityVectorLayout"/>
///     vector down to the mode-comparable subspace before scoring, so the
///     seed centroid only needs to live in that subspace. T13 will replace
///     this with the YAML-backed source and keep the interface stable.
/// </summary>
public sealed class HardcodedBrowserModeSeedSource : IBrowserModeSeedSource
{
    // [method_get, sec_fetch_navigate, sec_fetch_cors, upgrade_ws]
    // Order pinned by docs/superpowers/specs/2026-06-22-...md seed table.
    private static readonly IReadOnlyList<ModeSeed> _seeds = new[]
    {
        new ModeSeed("navigation",         new[] { 1f, 1f, 0f, 0f }),
        new ModeSeed("xhr",                new[] { 1f, 0f, 1f, 0f }),
        new ModeSeed("sub-resource",       new[] { 1f, 0f, 0f, 0f }),
        new ModeSeed("signalr-negotiate",  new[] { 0f, 0f, 1f, 0f }),
        new ModeSeed("websocket-upgrade",  new[] { 1f, 1f, 0f, 1f }),
        new ModeSeed("bot-raw",            new[] { 1f, 0f, 0f, 0f }),
        new ModeSeed("unknown",            new[] { 0f, 0f, 0f, 0f }),
    };

    public IReadOnlyList<ModeSeed> LoadYamlSeeds() => _seeds;
}

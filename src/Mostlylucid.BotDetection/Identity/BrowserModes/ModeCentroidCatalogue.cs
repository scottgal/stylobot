namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     One mode centroid as the catalogue surfaces it -- id, centroid,
///     maturity. The catalogue's read shape is intentionally narrower than
///     the underlying <c>identity_archetypes</c> row: the mode classifier
///     (T12) only needs the projection vector + maturity to score a request
///     against each mode, and decoupling the read shape from the storage
///     row lets T13's YAML loader fill in the centroid block without
///     dragging the calibration columns into the mode-centroid hot path.
/// </summary>
public sealed record ModeCentroidRow(string ModeId, float[] Centroid, double Maturity);

/// <summary>
///     DB-backed catalogue of per-mode centroids. Lives on top of the
///     existing <c>identity_archetypes</c> table (filtered by
///     <c>catalogue_kind = 'browser_mode'</c> -- see T11a) so drift on a
///     mode centroid survives gateway restart without duplicating the
///     calibration / drainer infrastructure or introducing a parallel
///     <c>mode_centroids</c> table.
///
///     <para>
///     Boot behaviour: <see cref="LoadAsync"/> reads every <c>browser_mode</c>
///     row from the store. On a fresh DB (zero rows) it pulls the seed set
///     from <see cref="IBrowserModeSeedSource"/>, persists each via
///     <see cref="IFingerprintStore.UpsertCentroidAsync"/> (idempotent on
///     <c>archetype_id</c>; safe under a concurrent boot race), and returns
///     the seed values. Subsequent calls read directly from the store --
///     YAML is consulted exactly once at cold-start. This is the
///     architectural piece that makes mode-centroid drift survive: once the
///     row exists in DB, the YAML seed is bypassed and the live (possibly
///     drifted) centroid wins.
///     </para>
///
///     <para>
///     Wiring (DI) lands in T14; this class is shape-only for now so T12
///     can build the classifier against the read surface.
///     </para>
/// </summary>
public sealed class ModeCentroidCatalogue
{
    /// <summary>
    ///     Discriminator value stored in <c>identity_archetypes.catalogue_kind</c>
    ///     for every row this catalogue owns. Constant -- changing it would
    ///     orphan persisted drift state and is never the right answer.
    /// </summary>
    public const string CatalogueKind = "browser_mode";

    private readonly IFingerprintStore _store;
    private readonly IBrowserModeSeedSource _seedSource;

    public ModeCentroidCatalogue(IFingerprintStore store, IBrowserModeSeedSource seedSource)
    {
        _store = store;
        _seedSource = seedSource;
    }

    /// <summary>
    ///     Snapshot of every persisted mode centroid. First call against an
    ///     empty table seeds from <see cref="IBrowserModeSeedSource"/>;
    ///     subsequent calls read straight from the store so drift updates
    ///     written by the classifier / absorption tick are visible.
    /// </summary>
    public async Task<IReadOnlyList<ModeCentroidRow>> LoadAsync(CancellationToken ct = default)
    {
        var rows = await _store.GetByCatalogueKindAsync(CatalogueKind, ct);
        if (rows.Count > 0)
        {
            return rows
                .Select(r => new ModeCentroidRow(r.ArchetypeId, r.Centroid, r.Maturity))
                .ToList();
        }

        // Cold-start: seed from the YAML-backed source, persist into the
        // store. Idempotent: if a parallel boot races here, both paths
        // produce the same rows and UpsertCentroidAsync's ON CONFLICT
        // resolves it -- the seed has a fixed centroid + maturity = 0 so
        // either writer's row is equivalent until the first drift update
        // lands.
        var seeds = _seedSource.LoadYamlSeeds();
        foreach (var seed in seeds)
        {
            await _store.UpsertCentroidAsync(
                archetypeId: seed.ModeId,
                catalogueKind: CatalogueKind,
                centroid: seed.SeedCentroid,
                maturity: 0.0,
                ct: ct);
        }
        return seeds
            .Select(s => new ModeCentroidRow(s.ModeId, s.SeedCentroid, 0.0))
            .ToList();
    }
}

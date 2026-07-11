namespace Mostlylucid.BotDetection.Identity.BrowserChar;

/// <summary>
///     One browser-characteristic centroid as the catalogue surfaces it: the composite
///     <c>{family}:{mode}</c> key, its centroid vector, and maturity.
/// </summary>
public sealed record BrowserCharCentroidRow(string Key, float[] Centroid, double Maturity);

/// <summary>
///     DB-backed catalogue of per-<c>{family}:{mode}</c> browser-characteristic centroids.
///     A direct clone of <see cref="BrowserModes.ModeCentroidCatalogue"/>: it lives on the
///     same <c>identity_archetypes</c> table filtered by <c>catalogue_kind = 'browser_char'</c>,
///     so learned drift survives restart without any parallel table or drainer -- exactly the
///     reuse overview ratified. <see cref="LoadAsync"/> reads persisted rows, or on a cold
///     table seeds from <see cref="IBrowserCharSeedSource"/> and persists each (idempotent).
///     Once a row exists in DB the seed is bypassed and the live (possibly drifted) centroid
///     wins -- "seed once, then live wins."
/// </summary>
public sealed class BrowserCharCentroidCatalogue
{
    /// <summary>Discriminator stored in <c>identity_archetypes.catalogue_kind</c>. Constant.</summary>
    public const string CatalogueKind = "browser_char";

    private readonly IFingerprintStore _store;
    private readonly IBrowserCharSeedSource _seedSource;

    public BrowserCharCentroidCatalogue(IFingerprintStore store, IBrowserCharSeedSource seedSource)
    {
        _store = store;
        _seedSource = seedSource;
    }

    public async Task<IReadOnlyList<BrowserCharCentroidRow>> LoadAsync(CancellationToken ct = default)
    {
        var rows = await _store.GetByCatalogueKindAsync(CatalogueKind, ct);
        if (rows.Count > 0)
        {
            return rows
                .Select(r => new BrowserCharCentroidRow(r.ArchetypeId, r.Centroid, r.Maturity))
                .ToList();
        }

        // Cold-start: seed from the prior, persist. Idempotent on archetype_id under a
        // concurrent boot race (both writers produce the same fixed seed at maturity 0).
        var seeds = _seedSource.LoadSeeds();
        foreach (var seed in seeds)
        {
            await _store.UpsertCentroidAsync(
                archetypeId: seed.Key,
                catalogueKind: CatalogueKind,
                centroid: seed.SeedCentroid,
                maturity: 0.0,
                ct: ct);
        }

        return seeds
            .Select(s => new BrowserCharCentroidRow(s.Key, s.SeedCentroid, 0.0))
            .ToList();
    }
}

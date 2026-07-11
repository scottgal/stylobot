namespace Mostlylucid.BotDetection.Identity.BrowserChar;

/// <summary>
///     Result of scoring a request's browser-characteristic observation against the
///     centroid for its CLAIMED <c>{family}:{mode}</c> key. <see cref="Drift"/> is in
///     [0,1] (0 = perfectly consistent, 1 = opposite); <see cref="Consistency"/> is the
///     raw weighted cosine in [-1,1]. <see cref="KeyFound"/> is false when the claimed
///     browser has no centroid (unknown family) -- the caller then fails open.
/// </summary>
public sealed record BrowserCharScore(string Key, double Drift, double Consistency, bool KeyFound);

/// <summary>
///     Scores a browser-characteristic observation against the LEARNED centroid for its
///     claimed <c>{family}:{mode}</c> key -- NOT a nearest-centroid search (that would
///     answer "which browser is this"; we already have the claim and are checking it).
///     Deviation from the claimed centroid is the inconsistency signal.
///
///     <para>
///     The cosine is WEIGHTED by <see cref="BrowserCharVectorEncoder.BuildMask"/>: engine
///     dims high, feature dims low, so the score is anchored on the un-spoofable engine
///     substrate. A spoofer claiming Chrome while running Firefox lands a strong negative
///     cosine on the engine dims (v8 vs spidermonkey-jsc) -> high drift, regardless of how
///     perfectly they fake the feature presences.
///     </para>
/// </summary>
public sealed class BrowserCharConsistencyScorer
{
    private readonly IReadOnlyDictionary<string, float[]> _centroids;
    private readonly float[] _mask;

    private BrowserCharConsistencyScorer(IReadOnlyDictionary<string, float[]> centroids, float[] mask)
    {
        _centroids = centroids;
        _mask = mask;
    }

    /// <summary>
    ///     Snapshot the catalogue's centroids + build the engine-weighted mask. Cheap to
    ///     call at startup; hold the instance. A later rebuild picks up drift.
    /// </summary>
    public static async Task<BrowserCharConsistencyScorer> LoadAsync(
        BrowserCharCentroidCatalogue catalogue,
        IdentityVectorLayout layout,
        float engineWeight,
        float featureWeight,
        CancellationToken ct = default)
    {
        var rows = await catalogue.LoadAsync(ct);
        var dict = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) dict[r.Key] = r.Centroid;
        var mask = BrowserCharVectorEncoder.BuildMask(layout, engineWeight, featureWeight);
        return new BrowserCharConsistencyScorer(dict, mask);
    }

    /// <summary>
    ///     Build a store-less scorer straight from a seed list. Used by the seed-only
    ///     path and by tests; the catalogue-backed <see cref="LoadAsync"/> is the
    ///     drift-aware production factory.
    /// </summary>
    public static BrowserCharConsistencyScorer FromSeeds(
        IReadOnlyList<BrowserCharSeed> seeds, IdentityVectorLayout layout,
        float engineWeight, float featureWeight)
    {
        var dict = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in seeds) dict[s.Key] = s.SeedCentroid;
        return new BrowserCharConsistencyScorer(
            dict, BrowserCharVectorEncoder.BuildMask(layout, engineWeight, featureWeight));
    }

    /// <summary>
    ///     Score the observation against the centroid for <paramref name="claimedKey"/>.
    ///     Returns a result with <see cref="BrowserCharScore.KeyFound"/> = false (drift 0)
    ///     when the claimed family has no centroid -- the caller must fail open, never flag
    ///     an unknown browser.
    /// </summary>
    public BrowserCharScore Score(string claimedKey, float[] observation)
    {
        if (!_centroids.TryGetValue(claimedKey, out var centroid))
            return new BrowserCharScore(claimedKey, Drift: 0, Consistency: 0, KeyFound: false);

        var cos = WeightedCosine(observation, centroid, _mask);
        var drift = Math.Clamp((1.0 - cos) / 2.0, 0.0, 1.0);
        return new BrowserCharScore(claimedKey, drift, cos, KeyFound: true);
    }

    private static double WeightedCosine(float[] a, float[] b, float[] w)
    {
        if (a.Length != b.Length || a.Length != w.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var wi = w[i];
            if (wi == 0f) continue;
            dot += wi * a[i] * b[i];
            na += wi * a[i] * a[i];
            nb += wi * b[i] * b[i];
        }

        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

internal static class IdentityTestHelpers
{
    public static float[] MakeUnitVector(int dim, int seed)
    {
        var rng = new Random(seed);
        var v = new float[dim];
        for (var i = 0; i < dim; i++) v[i] = (float)rng.NextDouble();
        Normalize(v);
        return v;
    }

    // Places mass only on dims where `orthogonalTo` is zero so the dot product
    // with the basis vector is exactly 0. Caller supplies a sparse basis (the
    // identity layout has many zero dims even on well-populated centroids).
    public static float[] MakeUnitVectorOrthogonalTo(int dim, int seed, float[] orthogonalTo)
    {
        var rng = new Random(seed);
        var v = new float[dim];
        for (var i = 0; i < dim; i++)
            if (orthogonalTo[i] == 0f) v[i] = (float)rng.NextDouble();
        Normalize(v);
        return v;
    }

    public static void Normalize(float[] v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += v[i] * v[i];
        var norm = (float)Math.Sqrt(sum);
        if (norm <= 0) return;
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }

    public static Fingerprint MakeFingerprint(
        string id,
        float[] centroid,
        string inferredClientType = "test",
        string? archetypeOrigin = null,
        int observationCount = 1,
        int centroidMaturity = 1,
        double quality = 0.8,
        double cachedBotProbability = 0.0,
        string? cachedRiskBand = null,
        DateTime? cachedScoreUpdatedAt = null,
        string? displayName = null)
    {
        var weights = new float[centroid.Length];
        Array.Fill(weights, 1.0f);
        var now = DateTime.UtcNow;
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = centroid,
            CentroidMaturity = centroidMaturity,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = observationCount,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = quality,
            ArchetypeOrigin = archetypeOrigin,
            InferredClientType = inferredClientType,
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            CachedBotProbability = cachedBotProbability,
            CachedRiskBand = cachedRiskBand,
            CachedScoreUpdatedAt = cachedScoreUpdatedAt,
            InducedName = displayName,
            InducedNameUpdatedAt = displayName is null ? default(DateTime?) : now
        };
    }
}

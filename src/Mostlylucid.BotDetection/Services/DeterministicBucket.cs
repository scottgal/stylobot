namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Shared helper for deterministic "should this request take the rare branch?"
///     decisions used by <see cref="SignatureVerdictGate"/> (refresh sampling) and
///     <see cref="LoadShedDecision"/> (load shedding). Maps an integer or string seed
///     through a Knuth multiplicative hash to a uniformly-distributed bucket in
///     [0, 1.0). Identical seeds always land in the same bucket so retries are stable.
/// </summary>
public static class DeterministicBucket
{
    private const uint KnuthMultiplier = 2654435761u;
    private const uint BucketResolution = 10_000;

    /// <summary>Returns true if the seed falls under <paramref name="rate"/> in [0, 1].</summary>
    public static bool ShouldFire(int seed, double rate)
    {
        if (rate <= 0.0) return false;
        if (rate >= 1.0) return true;
        unchecked
        {
            var h = (uint)seed * KnuthMultiplier;
            var bucket = (h % BucketResolution) / (double)BucketResolution;
            return bucket < rate;
        }
    }

    /// <summary>Returns true if the seed falls under <paramref name="rate"/> in [0, 1].</summary>
    public static bool ShouldFire(string seed, double rate)
        => ShouldFire(seed?.GetHashCode() ?? 0, rate);
}

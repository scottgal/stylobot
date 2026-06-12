using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using MathNet.Numerics.Statistics;
using Microsoft.Extensions.Caching.Memory;

namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
///     Advanced statistical analysis for behavioral pattern detection.
///     Uses entropy analysis, Markov chains, and time-series anomaly detection.
///     PRIVACY NOTE: This analyzer uses hashed identities to ensure NO PII (like IP addresses)
///     is ever stored directly. All cache keys are derived from XxHash64 signatures.
/// </summary>
public partial class BehavioralPatternAnalyzer
{
    private readonly TimeSpan _analysisWindow;
    private readonly IMemoryCache _cache;
    private readonly string _salt;

    public BehavioralPatternAnalyzer(
        IMemoryCache cache,
        TimeSpan? analysisWindow = null,
        string? salt = null)
    {
        _cache = cache;
        _analysisWindow = analysisWindow ?? TimeSpan.FromMinutes(15);
        _salt = salt ?? Guid.NewGuid().ToString();
    }

    /// <summary>
    ///     Creates a privacy-safe signature from an identity key (e.g., IP address).
    ///     Uses XxHash64 with a salt to create a deterministic signature.
    ///     IMPORTANT: The signature IS the lookup key. Given the same IP + salt,
    ///     you always get the same signature, enabling lookups without storing the IP.
    ///     Example:
    ///     - IP: "192.168.1.1" + Salt: "secret" → Signature: "A1B2C3D4E5F6G7H8"
    ///     - Later lookups with same IP + salt → Same signature → Same behavioral data
    ///     - Cannot reverse: Signature alone cannot reveal the original IP
    /// </summary>
    /// <param name="identityKey">The identity (e.g., IP address) to hash</param>
    /// <returns>A deterministic hex string signature that can be used for lookups</returns>
    private string HashIdentity(string identityKey)
    {
        // Combine identity with salt for deterministic hashing
        var salted = $"{identityKey}:{_salt}";
        var bytes = Encoding.UTF8.GetBytes(salted);
        var hash = XxHash64.Hash(bytes);

        // Return just the hex string - this IS the signature we use for lookups
        return Convert.ToHexString(hash);
    }

    /// <summary>
    ///     Calculate Shannon entropy of request paths.
    ///     High entropy = random/bot-like
    ///     Low entropy = predictable human patterns
    /// </summary>
    public double CalculatePathEntropy(string identityKey)
    {
        var paths = GetRecentPaths(identityKey);
        if (paths.Count < 5) return 0; // Not enough data

        // Count frequency of each path without allocating a fresh
        // IGrouping + Dictionary per call (the LINQ GroupBy + ToDictionary
        // form allocates ~3 objects per group plus the group keys). For the
        // bounded paths list (cap 50) this dictionary is small but called
        // on every advanced-behavioural detection.
        var counts = new Dictionary<string, int>(capacity: paths.Count, StringComparer.Ordinal);
        for (var i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            counts[p] = counts.TryGetValue(p, out var existing) ? existing + 1 : 1;
        }

        // Shannon entropy: H = -Σ(p * log2(p))
        var total = (double)paths.Count;
        var entropy = 0.0;
        foreach (var count in counts.Values)
        {
            var freq = count / total;
            if (freq > 0) entropy -= freq * Math.Log2(freq);
        }
        return entropy;
    }

    /// <summary>
    ///     Calculate entropy of request timing intervals.
    ///     Bots often have very regular (low entropy) or very random (high entropy) timing.
    /// </summary>
    public double CalculateTimingEntropy(string identityKey)
    {
        var timings = GetRecentTimings(identityKey);
        if (timings.Count < 5) return 0;

        // Bin intervals into 100ms buckets and count inline. The previous
        // implementation built an intermediate List<int> + GroupBy + ToDictionary;
        // the bounded timings list (cap 100) makes the bucket count small but
        // the bucket key density is high (every 100ms is its own key), so the
        // dictionary fits the allocations into a single bounded chunk instead
        // of a List + 3 LINQ chained allocs.
        var counts = new Dictionary<int, int>(capacity: timings.Count);
        var intervalsCount = 0;
        for (var i = 1; i < timings.Count; i++)
        {
            var intervalMs = (timings[i] - timings[i - 1]).TotalMilliseconds;
            var bucket = (int)(intervalMs / 100) * 100;
            counts[bucket] = counts.TryGetValue(bucket, out var existing) ? existing + 1 : 1;
            intervalsCount++;
        }

        if (intervalsCount == 0) return 0;

        var total = (double)intervalsCount;
        var entropy = 0.0;
        foreach (var count in counts.Values)
        {
            var freq = count / total;
            if (freq > 0) entropy -= freq * Math.Log2(freq);
        }
        return entropy;
    }

    /// <summary>
    ///     Detect anomalous request timing using statistical methods.
    ///     Returns (isAnomalous, zScore, description)
    /// </summary>
    public (bool IsAnomalous, double ZScore, string Description) DetectTimingAnomaly(
        string identityKey,
        DateTime currentRequestTime)
    {
        var timings = GetRecentTimings(identityKey);
        if (timings.Count < 10) return (false, 0, "Insufficient data");

        // Calculate intervals
        var intervals = new List<double>();
        for (var i = 1; i < timings.Count; i++) intervals.Add((timings[i] - timings[i - 1]).TotalSeconds);

        // Current interval
        var currentInterval = (currentRequestTime - timings[^1]).TotalSeconds;

        // Calculate statistics using MathNet.Numerics
        var mean = intervals.Mean();
        var stdDev = intervals.StandardDeviation();

        if (stdDev < 0.01) return (false, 0, "Constant timing"); // Avoid division by zero

        // Z-score: how many standard deviations from mean
        var zScore = Math.Abs((currentInterval - mean) / stdDev);

        // Anomaly if z-score > 3 (99.7% confidence interval)
        if (zScore > 3.0)
            return (true, zScore, $"Timing anomaly: {currentInterval:F1}s vs {mean:F1}±{stdDev:F1}s (z={zScore:F1})");

        return (false, zScore, "Normal timing");
    }

    /// <summary>
    ///     Analyze request sequence using first-order Markov chain.
    ///     Detects non-human navigation patterns.
    /// </summary>
    public (double TransitionScore, string Pattern) AnalyzeNavigationPattern(
        string identityKey,
        string currentPath)
    {
        var paths = GetRecentPaths(identityKey);
        if (paths.Count < 3) return (0, "Insufficient history");

        // We only need transitions out of `lastPath`. The previous code
        // built the full first-order transition matrix
        // (Dictionary<string, List<string>>) just to query a single key.
        // For paths cap=50, that's 50 SimplifyPath calls + ~25 List
        // allocations + a Dictionary -- all thrown away after one lookup.
        // Single-pass counter that skips both the transition matrix and the
        // LINQ Count(predicate) delegate at the end.
        var lastPath = SimplifyPath(paths[^1]);
        var currentSimplified = SimplifyPath(currentPath);
        var transitionCount = 0;
        var matchingCount = 0;
        for (var i = 0; i < paths.Count - 1; i++)
        {
            if (SimplifyPath(paths[i]) != lastPath) continue;
            transitionCount++;
            if (SimplifyPath(paths[i + 1]) == currentSimplified) matchingCount++;
        }

        if (transitionCount > 0)
        {
            var probability = (double)matchingCount / transitionCount;
            if (probability < 0.1 && transitionCount >= 3)
                return (0.3, $"Unusual navigation: {lastPath}→{currentSimplified} (p={probability:P0})");
            if (probability > 0.9 && transitionCount >= 5)
                return (0.4, $"Highly repetitive: {lastPath}→{currentSimplified} (p={probability:P0})");
        }

        return (0, "Normal navigation");
    }

    /// <summary>
    ///     Detect if request timing follows a too-perfect pattern (bot-like).
    ///     Uses coefficient of variation (CV) - lower CV = more regular = more bot-like.
    /// </summary>
    public (bool IsTooRegular, double CV, string Description) DetectRegularPattern(string identityKey)
    {
        var timings = GetRecentTimings(identityKey);
        if (timings.Count < 10) return (false, 0, "Insufficient data");

        var intervals = new List<double>();
        for (var i = 1; i < timings.Count; i++) intervals.Add((timings[i] - timings[i - 1]).TotalSeconds);

        var mean = intervals.Mean();
        var stdDev = intervals.StandardDeviation();

        if (mean < 0.1) return (false, 0, "Too fast to analyze");

        // Coefficient of variation: CV = stdDev / mean
        var cv = stdDev / mean;

        // Very low CV (< 0.15) = too regular, likely bot
        // Human browsing typically has CV > 0.5
        if (cv < 0.15 && mean < 10)
            return (true, cv, $"Too regular timing: CV={cv:F2} (mean={mean:F1}s, σ={stdDev:F1}s)");

        return (false, cv, "Natural variation");
    }

    /// <summary>
    ///     Detect burst patterns - sudden spike in request rate.
    /// </summary>
    public (bool IsBurst, int BurstSize, TimeSpan BurstDuration) DetectBurstPattern(
        string identityKey,
        TimeSpan burstWindow)
    {
        var timings = GetRecentTimings(identityKey);
        if (timings.Count < 5) return (false, 0, TimeSpan.Zero);

        var now = DateTime.UtcNow;
        var burstStart = now - burstWindow;

        // Count requests in burst window
        var burstCount = timings.Count(t => t >= burstStart);

        // Calculate normal rate from historical data (excluding burst window)
        var historicalTimings = timings.Where(t => t < burstStart).ToList();
        if (historicalTimings.Count < 5) return (false, burstCount, TimeSpan.Zero);

        var historicalDuration = (historicalTimings[^1] - historicalTimings[0]).TotalSeconds;
        var historicalRate = historicalTimings.Count / Math.Max(1, historicalDuration);
        var burstRate = burstCount / burstWindow.TotalSeconds;

        // Burst if rate is > 5x historical rate
        if (burstRate > historicalRate * 5 && burstCount >= 10) return (true, burstCount, burstWindow);

        return (false, burstCount, TimeSpan.Zero);
    }

    #region Helper Methods

    // The defensive copy callers previously needed is no longer required:
    // RecordRequest's copy-on-write pattern (create a new list, then Set) means
    // any list reference returned here is immutable for its lifetime. Callers
    // (CalculatePathEntropy, CalculateTimingEntropy, DetectTimingAnomaly, etc.)
    // only read; returning the cached reference directly cuts the per-request
    // List<string>/<DateTime> alloc + the wasted snapshot copy. At 32 KB/req
    // for Behavioral_Normal the GetRecent* allocations were one of the bigger
    // line items.

    private static readonly List<string> EmptyPaths = new(0);
    private static readonly List<DateTime> EmptyTimings = new(0);

    private List<string> GetRecentPaths(string identityKey)
    {
        var hashedKey = HashIdentity(identityKey);
        var key = "pattern_paths_" + hashedKey;
        return _cache.Get<List<string>>(key) ?? EmptyPaths;
    }

    private List<DateTime> GetRecentTimings(string identityKey)
    {
        var hashedKey = HashIdentity(identityKey);
        var key = "pattern_timings_" + hashedKey;
        return _cache.Get<List<DateTime>>(key) ?? EmptyTimings;
    }

    /// <summary>
    ///     Simplify path for Markov analysis (remove IDs, group similar paths).
    /// </summary>
    private static string SimplifyPath(string path)
    {
        // Replace numeric IDs with placeholder
        var simplified = NumericIdRegex().Replace(path, "{id}");

        // Replace GUIDs with placeholder
        simplified = GuidRegex().Replace(simplified, "{guid}");

        return simplified.ToLowerInvariant();
    }

    /// <summary>
    ///     Record a new request for pattern analysis.
    ///     PRIVACY: Uses hashed identity to ensure NO PII is stored.
    /// </summary>
    public void RecordRequest(string identityKey, string path, DateTime timestamp)
    {
        // Hash once per call instead of three times via the GetRecentPaths /
        // GetRecentTimings chain (each of those previously rebuilt
        // pattern_paths_/pattern_timings_ keys via $-interpolation + a fresh
        // List copy). Behavioral_Normal microbench at 32 KB/req was dominated
        // by these per-detection LINQ + List copy paths.
        var hashedKey = HashIdentity(identityKey);
        var pathKey = "pattern_paths_" + hashedKey;
        var timingKey = "pattern_timings_" + hashedKey;

        // Copy-on-write: clone the stored list so concurrent readers of the old
        // reference are safe. The previous code did this twice -- once inside
        // GetRecentPaths to produce a defensive snapshot, then again inside
        // RecordRequest's `new List<string>(snapshot) { path }`. Reading the
        // cache directly and copying once produces the same safety.
        var storedPaths = _cache.Get<List<string>>(pathKey);
        var paths = storedPaths is null
            ? new List<string>(capacity: 4) { path }
            : new List<string>(storedPaths.Count + 1) { };
        if (storedPaths is not null)
        {
            paths.AddRange(storedPaths);
            paths.Add(path);
        }
        if (paths.Count > 50) paths.RemoveAt(0);
        _cache.Set(pathKey, paths, _analysisWindow);

        var storedTimings = _cache.Get<List<DateTime>>(timingKey);
        var timings = storedTimings is null
            ? new List<DateTime>(capacity: 4) { timestamp }
            : new List<DateTime>(storedTimings.Count + 1) { };
        if (storedTimings is not null)
        {
            timings.AddRange(storedTimings);
            timings.Add(timestamp);
        }
        if (timings.Count > 100) timings.RemoveAt(0);
        _cache.Set(timingKey, timings, _analysisWindow);
    }

    #endregion

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumericIdRegex();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();
}
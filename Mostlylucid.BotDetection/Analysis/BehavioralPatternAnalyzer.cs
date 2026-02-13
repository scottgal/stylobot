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

        // Count frequency of each path
        var frequencies = paths
            .GroupBy(p => p)
            .ToDictionary(g => g.Key, g => (double)g.Count() / paths.Count);

        // Calculate Shannon entropy: H = -Σ(p * log2(p))
        var entropy = 0.0;
        foreach (var freq in frequencies.Values)
            if (freq > 0)
                entropy -= freq * Math.Log2(freq);

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

        // Convert to intervals in milliseconds, binned to 100ms buckets
        var intervals = new List<int>();
        for (var i = 1; i < timings.Count; i++)
        {
            var intervalMs = (timings[i] - timings[i - 1]).TotalMilliseconds;
            var bucket = (int)(intervalMs / 100) * 100; // Round to 100ms buckets
            intervals.Add(bucket);
        }

        // Calculate frequency distribution
        var frequencies = intervals
            .GroupBy(i => i)
            .ToDictionary(g => g.Key, g => (double)g.Count() / intervals.Count);

        // Shannon entropy
        var entropy = 0.0;
        foreach (var freq in frequencies.Values)
            if (freq > 0)
                entropy -= freq * Math.Log2(freq);

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

        // Build transition matrix (simplified first-order)
        var transitions = new Dictionary<string, List<string>>();
        for (var i = 0; i < paths.Count - 1; i++)
        {
            var from = SimplifyPath(paths[i]);
            var to = SimplifyPath(paths[i + 1]);

            if (!transitions.ContainsKey(from)) transitions[from] = new List<string>();
            transitions[from].Add(to);
        }

        // Analyze current transition
        if (paths.Count > 0)
        {
            var lastPath = SimplifyPath(paths[^1]);
            var currentSimplified = SimplifyPath(currentPath);

            if (transitions.ContainsKey(lastPath))
            {
                var possibleNext = transitions[lastPath];
                var transitionCount = possibleNext.Count;
                var matchingCount = possibleNext.Count(p => p == currentSimplified);

                // Transition probability
                var probability = (double)matchingCount / transitionCount;

                // Low probability = unusual transition
                if (probability < 0.1 && transitionCount >= 3)
                    return (0.3, $"Unusual navigation: {lastPath}→{currentSimplified} (p={probability:P0})");

                // Very repetitive = bot-like
                if (probability > 0.9 && transitionCount >= 5)
                    return (0.4, $"Highly repetitive: {lastPath}→{currentSimplified} (p={probability:P0})");
            }
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

    private List<string> GetRecentPaths(string identityKey)
    {
        // Use hashed identity to protect PII
        var hashedKey = HashIdentity(identityKey);
        var key = $"pattern_paths_{hashedKey}";
        return _cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = _analysisWindow;
            return new List<string>();
        }) ?? new List<string>();
    }

    private List<DateTime> GetRecentTimings(string identityKey)
    {
        // Use hashed identity to protect PII
        var hashedKey = HashIdentity(identityKey);
        var key = $"pattern_timings_{hashedKey}";
        return _cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = _analysisWindow;
            return new List<DateTime>();
        }) ?? new List<DateTime>();
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
        // Use hashed identity to protect PII
        var hashedKey = HashIdentity(identityKey);

        // Record path
        var pathKey = $"pattern_paths_{hashedKey}";
        var paths = GetRecentPaths(identityKey);
        paths.Add(path);

        // Keep last 50 paths
        if (paths.Count > 50) paths.RemoveAt(0);

        _cache.Set(pathKey, paths, _analysisWindow);

        // Record timing
        var timingKey = $"pattern_timings_{hashedKey}";
        var timings = GetRecentTimings(identityKey);
        timings.Add(timestamp);

        // Keep last 100 timings
        if (timings.Count > 100) timings.RemoveAt(0);

        _cache.Set(timingKey, timings, _analysisWindow);
    }

    #endregion

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumericIdRegex();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();
}
using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
///     Fixed-capacity circular buffer for path strings.
///     Zero-allocation once the backing array is created — Add() overwrites in place.
/// </summary>
internal sealed class PathRing
{
    private readonly string[] _buf;
    private int _head, _count;
    public PathRing(int cap) => _buf = new string[cap];
    public int Count => _count;
    public string this[int i] => _buf[((_head - _count + _buf.Length) + i) % _buf.Length];
    public string Last => this[_count - 1];
    public void Add(string s) { _buf[_head++ % _buf.Length] = s; _count = Math.Min(_count + 1, _buf.Length); }
}

/// <summary>
///     Fixed-capacity circular buffer for DateTime timings.
///     Zero-allocation once the backing array is created — Add() overwrites in place.
/// </summary>
internal sealed class TimingRing
{
    private readonly DateTime[] _buf;
    private int _head, _count;
    public TimingRing(int cap) => _buf = new DateTime[cap];
    public int Count => _count;
    public DateTime this[int i] => _buf[((_head - _count + _buf.Length) + i) % _buf.Length];
    public DateTime Last => this[_count - 1];
    public void Add(DateTime t) { _buf[_head++ % _buf.Length] = t; _count = Math.Min(_count + 1, _buf.Length); }
}

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

    // Small bounded cache for HashIdentity results so a single ContributeAsync
    // call doesn't recompute the hex hash 3x (RecordRequest + two GetRecent*
    // entry points). The identity is the client IP which has very high reuse;
    // 4096 distinct IPs cover the typical per-minute working set.
    private readonly Mostlylucid.BotDetection.Services.BoundedCache<string, string> _hashCache =
        new(maxSize: 4096, defaultTtl: TimeSpan.FromMinutes(15));

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
        // Cache hit: return without allocating the salted string + UTF8 bytes
        // + hex string for repeat lookups within the bounded TTL window.
        if (_hashCache.TryGet(identityKey, out var cached) && cached != null) return cached;

        // Combine identity with salt for deterministic hashing
        var salted = $"{identityKey}:{_salt}";
        var bytes = Encoding.UTF8.GetBytes(salted);
        var hash = XxHash64.Hash(bytes);

        // Return just the hex string - this IS the signature we use for lookups
        var hex = Convert.ToHexString(hash);
        _hashCache.Set(identityKey, hex);
        return hex;
    }

    /// <summary>
    ///     Calculate Shannon entropy of request paths.
    ///     High entropy = random/bot-like
    ///     Low entropy = predictable human patterns
    /// </summary>
    public double CalculatePathEntropy(string identityKey)
    {
        var paths = GetRecentPaths(identityKey);
        if (paths.Count < 5) return 0;

        // Sort indices by path value so equal paths are adjacent — then count
        // runs without any Dictionary allocation. Paths cap = 50, so
        // stackalloc int[50] = 200B on the stack.
        Span<int> idx = stackalloc int[paths.Count];
        for (var i = 0; i < paths.Count; i++) idx[i] = i;
        idx.Sort(Comparer<int>.Create((a, b) =>
            StringComparer.Ordinal.Compare(paths[a], paths[b])));

        var total = (double)paths.Count;
        var entropy = 0.0;
        var run = 1;
        for (var i = 1; i < idx.Length; i++)
        {
            if (paths[idx[i]] == paths[idx[i - 1]]) { run++; continue; }
            var freq = run / total;
            entropy -= freq * Math.Log2(freq);
            run = 1;
        }
        var lastFreq = run / total;
        entropy -= lastFreq * Math.Log2(lastFreq);
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

        // Bin intervals into 100ms buckets using a stack-allocated int array.
        // 200 buckets covers 0-20s; intervals beyond that fall into the last bucket.
        // Zero allocations vs the previous Dictionary<int,int>.
        const int BucketCount = 200;
        Span<int> counts = stackalloc int[BucketCount];
        var intervalsCount = 0;
        for (var i = 1; i < timings.Count; i++)
        {
            var intervalMs = (timings[i] - timings[i - 1]).TotalMilliseconds;
            var bucket = Math.Clamp((int)(intervalMs / 100), 0, BucketCount - 1);
            counts[bucket]++;
            intervalsCount++;
        }

        if (intervalsCount == 0) return 0;

        var total = (double)intervalsCount;
        var entropy = 0.0;
        foreach (var count in counts)
        {
            if (count == 0) continue;
            var freq = count / total;
            entropy -= freq * Math.Log2(freq);
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

        // stackalloc avoids the List<double> heap allocation; timings is capped at 100.
        Span<double> intervals = stackalloc double[timings.Count - 1];
        for (var i = 1; i < timings.Count; i++)
            intervals[i - 1] = (timings[i] - timings[i - 1]).TotalSeconds;

        var currentInterval = (currentRequestTime - timings[^1]).TotalSeconds;

        // Inline mean + stddev: zero allocation, no MathNet IEnumerable overhead.
        var sum = 0.0;
        for (var i = 0; i < intervals.Length; i++) sum += intervals[i];
        var mean = sum / intervals.Length;
        var variance = 0.0;
        for (var i = 0; i < intervals.Length; i++) variance += (intervals[i] - mean) * (intervals[i] - mean);
        var stdDev = Math.Sqrt(variance / intervals.Length);

        if (stdDev < 0.01) return (false, 0, "Constant timing");

        var zScore = Math.Abs((currentInterval - mean) / stdDev);

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

        // Precompute simplified paths once per entry; the loop previously called
        // SimplifyPath(paths[i]) and SimplifyPath(paths[i+1]) inside each iteration,
        // allocating up to 3 strings per element (ToLowerInvariant + 2 regex replacements)
        // × O(n) iterations. Rent a temp array so the simplified strings share lifetime
        // with this stack frame only.
        var simplified = System.Buffers.ArrayPool<string>.Shared.Rent(paths.Count);
        try
        {
            for (var i = 0; i < paths.Count; i++) simplified[i] = SimplifyPath(paths[i]);

            var lastPath = simplified[paths.Count - 1];
            var currentSimplified = SimplifyPath(currentPath);
            var transitionCount = 0;
            var matchingCount = 0;
            for (var i = 0; i < paths.Count - 1; i++)
            {
                if (simplified[i] != lastPath) continue;
                transitionCount++;
                if (simplified[i + 1] == currentSimplified) matchingCount++;
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
        finally
        {
            System.Buffers.ArrayPool<string>.Shared.Return(simplified);
        }
    }

    /// <summary>
    ///     Detect if request timing follows a too-perfect pattern (bot-like).
    ///     Uses coefficient of variation (CV) - lower CV = more regular = more bot-like.
    /// </summary>
    public (bool IsTooRegular, double CV, string Description) DetectRegularPattern(string identityKey)
    {
        var timings = GetRecentTimings(identityKey);
        if (timings.Count < 10) return (false, 0, "Insufficient data");

        // stackalloc: zero heap allocation; timings capped at 100 entries.
        Span<double> intervals = stackalloc double[timings.Count - 1];
        for (var i = 1; i < timings.Count; i++)
            intervals[i - 1] = (timings[i] - timings[i - 1]).TotalSeconds;

        // Inline mean + stddev: zero allocation, no MathNet IEnumerable overhead.
        var sum = 0.0;
        for (var i = 0; i < intervals.Length; i++) sum += intervals[i];
        var mean = sum / intervals.Length;
        var variance = 0.0;
        for (var i = 0; i < intervals.Length; i++) variance += (intervals[i] - mean) * (intervals[i] - mean);
        var stdDev = Math.Sqrt(variance / intervals.Length);

        if (mean < 0.1) return (false, 0, "Too fast to analyze");

        var cv = stdDev / mean;

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

        // Single pass: count burst + historical without materialising a List<DateTime>.
        var burstCount = 0;
        var historicalCount = 0;
        DateTime histFirst = default, histLast = default;
        for (var i = 0; i < timings.Count; i++)
        {
            var t = timings[i];
            if (t >= burstStart) { burstCount++; continue; }
            if (historicalCount == 0) histFirst = t;
            histLast = t;
            historicalCount++;
        }

        if (historicalCount < 5) return (false, burstCount, TimeSpan.Zero);

        var historicalDuration = (histLast - histFirst).TotalSeconds;
        var historicalRate = historicalCount / Math.Max(1, historicalDuration);
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

    private static readonly PathRing EmptyPaths = new(0);
    private static readonly TimingRing EmptyTimings = new(0);

    private PathRing GetRecentPaths(string identityKey)
    {
        var hashedKey = HashIdentity(identityKey);
        return _cache.Get<PathRing>("pattern_paths_" + hashedKey) ?? EmptyPaths;
    }

    private TimingRing GetRecentTimings(string identityKey)
    {
        var hashedKey = HashIdentity(identityKey);
        return _cache.Get<TimingRing>("pattern_timings_" + hashedKey) ?? EmptyTimings;
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

        // Ring buffer: GetOrCreate allocates PathRing/TimingRing once per identity
        // per analysis window. Add() overwrites in-place — zero allocation on the hot path.
        var paths = _cache.GetOrCreate(pathKey, e =>
        {
            e.AbsoluteExpirationRelativeToNow = _analysisWindow;
            return new PathRing(50);
        })!;
        paths.Add(path);

        var timings = _cache.GetOrCreate(timingKey, e =>
        {
            e.AbsoluteExpirationRelativeToNow = _analysisWindow;
            return new TimingRing(100);
        })!;
        timings.Add(timestamp);
    }

    #endregion

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumericIdRegex();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();
}
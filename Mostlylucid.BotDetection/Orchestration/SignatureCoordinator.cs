using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.KeyedSequential;
using Mostlylucid.Ephemeral.Atoms.SlidingCache;
using CacheStats = Mostlylucid.Ephemeral.Atoms.SlidingCache.CacheStats;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Configuration for the cross-request signature coordinator.
/// </summary>
public class SignatureCoordinatorOptions
{
    /// <summary>
    ///     Maximum number of signatures to track in the sliding window.
    ///     This is the cross-request memory - tracks up to N unique signatures.
    ///     Default: 1000
    /// </summary>
    public int MaxSignaturesInWindow { get; set; } = 1000;

    /// <summary>
    ///     Time window for tracking signature behavior across requests.
    ///     Signatures older than this are evicted from the window.
    ///     Default: 15 minutes
    /// </summary>
    public TimeSpan SignatureWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Maximum requests to track per signature within the window.
    ///     This prevents memory exhaustion from a single signature flooding.
    ///     Default: 100
    /// </summary>
    public int MaxRequestsPerSignature { get; set; } = 100;

    /// <summary>
    ///     Aberration detection threshold - minimum requests before analysis.
    ///     Need enough data points to detect patterns.
    ///     Default: 5
    /// </summary>
    public int MinRequestsForAberrationDetection { get; set; } = 5;

    /// <summary>
    ///     Aberration score threshold for escalation.
    ///     Score above this triggers cross-request escalation.
    ///     Range: 0.0-1.0
    ///     Default: 0.7
    /// </summary>
    public double AberrationScoreThreshold { get; set; } = 0.7;

    /// <summary>
    ///     TTL for signature entries (time-to-live).
    ///     Entries older than this are auto-evicted even if LRU capacity not reached.
    ///     Enables auto-specialization - cache naturally focuses on active signatures.
    ///     Default: 30 minutes
    /// </summary>
    public TimeSpan SignatureTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Interval for TTL cleanup sweep.
    ///     Background task checks for expired entries at this interval.
    ///     Default: 1 minute
    /// </summary>
    public TimeSpan TtlCleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Enable signal emission for signature updates.
    ///     Useful for observability and monitoring.
    ///     Default: true
    /// </summary>
    public bool EnableUpdateSignals { get; set; } = true;

    /// <summary>
    ///     Enable error signal emission for failed updates.
    ///     Default: true
    /// </summary>
    public bool EnableErrorSignals { get; set; } = true;
}

/// <summary>
///     Represents a single request's signature data in the tracking window.
/// </summary>
public record SignatureRequest
{
    public required string RequestId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Path { get; init; }
    public required double BotProbability { get; init; }
    public required IReadOnlyDictionary<string, object> Signals { get; init; }
    public required HashSet<string> DetectorsRan { get; init; }
    public bool Escalated { get; set; }
}

/// <summary>
///     Aggregated behavior for a signature across multiple requests.
/// </summary>
public record SignatureBehavior
{
    public required string Signature { get; init; }
    public required List<SignatureRequest> Requests { get; init; }
    public required DateTime FirstSeen { get; init; }
    public required DateTime LastSeen { get; init; }
    public required int RequestCount { get; init; }
    public required double AverageInterval { get; init; }
    public required double PathEntropy { get; init; }
    public required double TimingCoefficient { get; init; }
    public required double AverageBotProbability { get; init; }
    public required double AberrationScore { get; init; }
    public required bool IsAberrant { get; init; }

    // Geo context for cluster analysis
    public string? CountryCode { get; init; }
    public string? Asn { get; init; }
    public bool IsDatacenter { get; init; }
}

/// <summary>
///     Signal emitted when a signature exhibits aberrant behavior across requests.
/// </summary>
public readonly record struct AberrationSignal(
    string Signature,
    double AberrationScore,
    int RequestCount,
    string Reason);

/// <summary>
///     Signal emitted when a signature is updated.
/// </summary>
public readonly record struct SignatureUpdateSignal(
    string Signature,
    int RequestCount,
    double AberrationScore,
    bool IsAberrant,
    DateTime Timestamp);

/// <summary>
///     Signal emitted when a signature update fails.
/// </summary>
public readonly record struct SignatureErrorSignal(
    string Signature,
    string ErrorMessage,
    Exception? Exception,
    DateTime Timestamp);

/// <summary>
///     Geo context for a signature, used by cluster analysis.
/// </summary>
internal record SignatureGeoContext
{
    public string? CountryCode { get; init; }
    public string? Asn { get; init; }
    public bool IsDatacenter { get; init; }
}

/// <summary>
///     Request to update a signature with a new request.
///     Processed sequentially per signature via KeyedSequentialAtom.
/// </summary>
internal record SignatureUpdateRequest
{
    public required string Signature { get; init; }
    public required SignatureRequest Request { get; init; }
    public string? CountryCode { get; init; }
    public string? Asn { get; init; }
    public bool IsDatacenter { get; init; }
}

/// <summary>
///     Singleton coordinator that tracks signatures across multiple requests.
///     Uses ephemeral atoms to maintain a sliding window of request data keyed by signature.
///     Detects aberrant behavior patterns that span multiple requests.
/// </summary>
public class SignatureCoordinator : IAsyncDisposable
{
    // Internal signal sink owned by this coordinator
    private readonly SignalSink _signals;
    private readonly ILogger<SignatureCoordinator> _logger;
    private readonly SignatureCoordinatorOptions _options;

    // TTL-aware LRU cache for signature atoms (ephemeral.complete pattern)
    private readonly SlidingCacheAtom<string, SignatureTrackingAtom> _signatureCache;

    // Per-signature sequential updates (ephemeral.complete pattern)
    private readonly KeyedSequentialAtom<SignatureUpdateRequest, string> _updateAtom;

    // Tracks known signatures for enumeration (SlidingCacheAtom doesn't expose keys)
    private readonly ConcurrentDictionary<string, SignatureGeoContext> _knownSignatures = new();

    // IP hash -> set of primary signatures (for convergence analysis)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _ipIndex = new();

    // Signature -> FamilyId reverse lookup
    private readonly ConcurrentDictionary<string, string> _signatureToFamily = new();

    // FamilyId -> SignatureFamily
    private readonly ConcurrentDictionary<string, SignatureFamily> _families = new();

    public SignatureCoordinator(
        ILogger<SignatureCoordinator> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value.SignatureCoordinator;

        // Initialize internal signal sink owned by this coordinator
        _signals = new SignalSink(
            _options.MaxSignaturesInWindow * 10,
            _options.SignatureWindow);

        // Create TTL-aware LRU cache using ephemeral.complete SlidingCacheAtom
        // This replaces manual LRU + TTL tracking!
        _signatureCache = new SlidingCacheAtom<string, SignatureTrackingAtom>(
            async (signature, ct) =>
            {
                // Factory creates new tracking atoms on cache miss
                _logger.LogDebug("Creating new SignatureTrackingAtom for signature: {Signature}", signature);
                return await Task.FromResult(
                    new SignatureTrackingAtom(signature, _options, _logger));
            },
            _options.SignatureTtl, // Access resets TTL
            _options.SignatureTtl * 2, // Hard limit
            _options.MaxSignaturesInWindow, // LRU capacity
            Environment.ProcessorCount, // Parallel factory calls
            10, // Signal sampling (1 in 10)
            _signals); // Coordinator-owned signals

        // Create per-signature sequential update atom using ephemeral.complete KeyedSequentialAtom
        // This ensures updates to the same signature are sequential while allowing global parallelism
        _updateAtom = new KeyedSequentialAtom<SignatureUpdateRequest, string>(
            req => req.Signature, // Key by signature
            async (req, ct) => await ProcessSignatureUpdateAsync(req, ct),
            Environment.ProcessorCount * 2, // Global parallelism
            1, // Per-signature sequential
            true, // Fair scheduling across signatures
            _signals); // Coordinator-owned signals

        _logger.LogInformation(
            "SignatureCoordinator initialized: window={Window}, maxSignatures={MaxSignatures}, ttl={Ttl}",
            _options.SignatureWindow,
            _options.MaxSignaturesInWindow,
            _options.SignatureTtl);
    }

    public async ValueTask DisposeAsync()
    {
        // Drain the update atom to complete pending updates
        await _updateAtom.DrainAsync();

        // Dispose atoms
        await _updateAtom.DisposeAsync();
        await _signatureCache.DisposeAsync();

        _logger.LogInformation("SignatureCoordinator disposed");
    }

    /// <summary>
    ///     Record a request for a signature. This is called by the per-request orchestrator
    ///     when it completes detection for a request.
    /// </summary>
    public async Task RecordRequestAsync(
        string signature,
        string requestId,
        string path,
        double botProbability,
        IReadOnlyDictionary<string, object> signals,
        HashSet<string> detectorsRan,
        CancellationToken cancellationToken = default,
        string? countryCode = null,
        string? asn = null,
        bool isDatacenter = false,
        string? ipHash = null)
    {
        // Create request record
        var request = new SignatureRequest
        {
            RequestId = requestId,
            Timestamp = DateTime.UtcNow,
            Path = path,
            BotProbability = botProbability,
            Signals = signals,
            DetectorsRan = detectorsRan,
            Escalated = false
        };

        // Track geo context for cluster analysis
        _knownSignatures.AddOrUpdate(
            signature,
            _ => new SignatureGeoContext { CountryCode = countryCode, Asn = asn, IsDatacenter = isDatacenter },
            (_, existing) => new SignatureGeoContext
            {
                CountryCode = countryCode ?? existing.CountryCode,
                Asn = asn ?? existing.Asn,
                IsDatacenter = isDatacenter || existing.IsDatacenter
            });

        // Index IP hash -> signature for convergence analysis
        if (!string.IsNullOrEmpty(ipHash))
        {
            var set = _ipIndex.GetOrAdd(ipHash, _ => new ConcurrentDictionary<string, byte>());
            set.TryAdd(signature, 0);
        }

        // Enqueue sequential update via KeyedSequentialAtom
        // This ensures updates to the same signature are processed in order
        await _updateAtom.EnqueueAsync(
            new SignatureUpdateRequest
            {
                Signature = signature,
                Request = request,
                CountryCode = countryCode,
                Asn = asn,
                IsDatacenter = isDatacenter
            },
            cancellationToken);
    }

    /// <summary>
    ///     Process a signature update request.
    ///     This runs sequentially per signature but parallel across signatures.
    /// </summary>
    private async Task ProcessSignatureUpdateAsync(
        SignatureUpdateRequest updateRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get or create tracking atom via SlidingCacheAtom
            // This handles TTL + LRU automatically!
            var atom = await _signatureCache.GetOrComputeAsync(
                updateRequest.Signature,
                cancellationToken);

            // Record the request in the atom
            await atom.RecordRequestAsync(updateRequest.Request, cancellationToken);

            // Get updated behavior
            var behavior = await atom.GetBehaviorAsync(cancellationToken);

            // Emit update signal if enabled
            if (_options.EnableUpdateSignals)
                _signals.Raise($"signature.update.{updateRequest.Signature}", updateRequest.Signature);

            // Check for aberrations and escalate if needed
            if (behavior.IsAberrant && !updateRequest.Request.Escalated)
            {
                // Emit aberration signal
                _signals.Raise($"signature.aberration.{updateRequest.Signature}", updateRequest.Signature);

                updateRequest.Request.Escalated = true;

                _logger.LogWarning(
                    "Aberration detected for signature {Signature}: score={Score:F2}, requests={Requests}, " +
                    "entropy={Entropy:F2}, timingCV={TimingCV:F2}",
                    updateRequest.Signature,
                    behavior.AberrationScore,
                    behavior.RequestCount,
                    behavior.PathEntropy,
                    behavior.TimingCoefficient);
            }
        }
        catch (Exception ex)
        {
            // Emit error signal
            if (_options.EnableErrorSignals)
                _signals.Raise($"signature.error.{updateRequest.Signature}", updateRequest.Signature);

            _logger.LogError(ex,
                "Failed to process signature update for {Signature}",
                updateRequest.Signature);

            throw; // Re-throw to maintain error visibility
        }
    }

    /// <summary>
    ///     Get behavior analysis for a signature across all tracked requests.
    /// </summary>
    public async Task<SignatureBehavior?> GetSignatureBehaviorAsync(
        string signature,
        CancellationToken cancellationToken = default)
    {
        // Try to get from cache (won't compute if missing)
        if (!_signatureCache.TryGet(signature, out var atom) || atom == null)
            return null;

        return await atom.GetBehaviorAsync(cancellationToken);
    }

    /// <summary>
    ///     Get recent aberration signals from coordinator-owned sink.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetAberrationSignals()
    {
        return _signals.Sense(e => e.Signal.StartsWith("signature.aberration."));
    }

    /// <summary>
    ///     Get the IP hash index for convergence analysis.
    ///     Returns IP hash -> list of signatures sharing that IP.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetIpIndex()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (ipHash, sigSet) in _ipIndex)
        {
            var sigs = sigSet.Keys.ToList();
            if (sigs.Count > 0)
                result[ipHash] = sigs;
        }
        return result;
    }

    /// <summary>
    ///     Register a signature family atomically.
    /// </summary>
    public void RegisterFamily(SignatureFamily family)
    {
        _families[family.FamilyId] = family;
        foreach (var sig in family.MemberSignatures.Keys)
            _signatureToFamily[sig] = family.FamilyId;
    }

    /// <summary>
    ///     Remove a family and clean up reverse index.
    /// </summary>
    public void RemoveFamily(string familyId)
    {
        if (_families.TryRemove(familyId, out var family))
        {
            foreach (var sig in family.MemberSignatures.Keys)
                _signatureToFamily.TryRemove(sig, out _);
        }
    }

    /// <summary>
    ///     Remove a single signature from the family reverse index.
    ///     Called after a signature is split from a family.
    /// </summary>
    public void RemoveSignatureFromFamilyIndex(string signature)
    {
        _signatureToFamily.TryRemove(signature, out _);
    }

    /// <summary>
    ///     O(1) family lookup via reverse index.
    /// </summary>
    public SignatureFamily? GetFamily(string signature)
    {
        if (_signatureToFamily.TryGetValue(signature, out var familyId) &&
            _families.TryGetValue(familyId, out var family))
            return family;
        return null;
    }

    /// <summary>
    ///     Get a snapshot of all families.
    /// </summary>
    public IReadOnlyList<SignatureFamily> GetAllFamilies()
    {
        return _families.Values.ToList();
    }

    /// <summary>
    ///     Get behaviors with family-level aggregation.
    ///     Family members are merged into single entries using the canonical signature.
    ///     Standalone signatures are returned as-is.
    /// </summary>
    public IReadOnlyList<SignatureBehavior> GetFamilyAwareBehaviors()
    {
        var allBehaviors = GetAllBehaviors();
        if (_families.IsEmpty)
            return allBehaviors;

        var result = new List<SignatureBehavior>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Process families: aggregate members into single behaviors
        foreach (var family in _families.Values)
        {
            var memberBehaviors = new List<SignatureBehavior>();
            foreach (var sig in family.MemberSignatures.Keys)
            {
                consumed.Add(sig);
                var behavior = allBehaviors.FirstOrDefault(b =>
                    string.Equals(b.Signature, sig, StringComparison.OrdinalIgnoreCase));
                if (behavior != null && behavior.RequestCount > 0)
                    memberBehaviors.Add(behavior);
            }

            if (memberBehaviors.Count == 0)
                continue;

            // Merge all requests from family members
            var allRequests = memberBehaviors.SelectMany(b => b.Requests).OrderBy(r => r.Timestamp).ToList();
            var firstSeen = memberBehaviors.Min(b => b.FirstSeen);
            var lastSeen = memberBehaviors.Max(b => b.LastSeen);

            // Recompute metrics from combined pool
            var intervals = new List<double>();
            for (var i = 1; i < allRequests.Count; i++)
                intervals.Add((allRequests[i].Timestamp - allRequests[i - 1].Timestamp).TotalSeconds);

            var avgInterval = intervals.Count > 0 ? intervals.Average() : 0;
            var timingCV = 0.0;
            if (intervals.Count > 1)
            {
                var stdDev = Math.Sqrt(intervals.Average(v => Math.Pow(v - avgInterval, 2)));
                timingCV = avgInterval > 0 ? stdDev / avgInterval : 0;
            }

            var pathCounts = allRequests.GroupBy(r => r.Path).ToDictionary(g => g.Key, g => g.Count());
            var pathEntropy = 0.0;
            if (pathCounts.Count > 1)
            {
                var total = allRequests.Count;
                foreach (var count in pathCounts.Values)
                {
                    var p = (double)count / total;
                    if (p > 0) pathEntropy -= p * Math.Log2(p);
                }
            }

            var avgBotProb = allRequests.Average(r => r.BotProbability);

            // Use canonical signature's geo context
            var geo = _knownSignatures.TryGetValue(family.CanonicalSignature, out var ctx)
                ? ctx
                : memberBehaviors.FirstOrDefault()?.CountryCode != null
                    ? new SignatureGeoContext
                    {
                        CountryCode = memberBehaviors.First().CountryCode,
                        Asn = memberBehaviors.First().Asn,
                        IsDatacenter = memberBehaviors.First().IsDatacenter
                    }
                    : new SignatureGeoContext();

            result.Add(new SignatureBehavior
            {
                Signature = family.CanonicalSignature,
                Requests = allRequests,
                FirstSeen = firstSeen,
                LastSeen = lastSeen,
                RequestCount = allRequests.Count,
                AverageInterval = avgInterval,
                PathEntropy = pathEntropy,
                TimingCoefficient = timingCV,
                AverageBotProbability = avgBotProb,
                AberrationScore = memberBehaviors.Max(b => b.AberrationScore),
                IsAberrant = memberBehaviors.Any(b => b.IsAberrant),
                CountryCode = geo.CountryCode,
                Asn = geo.Asn,
                IsDatacenter = geo.IsDatacenter
            });
        }

        // Add standalone signatures (not in any family)
        foreach (var behavior in allBehaviors)
        {
            if (!consumed.Contains(behavior.Signature))
                result.Add(behavior);
        }

        return result;
    }

    /// <summary>
    ///     Get a snapshot of all current SignatureBehavior records from the sliding window.
    ///     Used by BotClusterService for periodic cluster analysis.
    /// </summary>
    public IReadOnlyList<SignatureBehavior> GetAllBehaviors()
    {
        var behaviors = new List<SignatureBehavior>();
        foreach (var (key, geoContext) in _knownSignatures)
        {
            if (_signatureCache.TryGet(key, out var atom) && atom != null)
            {
                var behavior = atom.GetBehaviorAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (behavior.RequestCount > 0)
                {
                    // Enrich with geo context from tracked signatures
                    behaviors.Add(behavior with
                    {
                        CountryCode = geoContext.CountryCode,
                        Asn = geoContext.Asn,
                        IsDatacenter = geoContext.IsDatacenter
                    });
                }
            }
            else
            {
                // Signature has been evicted from cache, remove from tracking
                _knownSignatures.TryRemove(key, out _);

                // Clean up IP index entries for evicted signatures
                foreach (var (_, sigSet) in _ipIndex)
                    sigSet.TryRemove(key, out _);

                // Clean up family membership for evicted signatures
                if (_signatureToFamily.TryRemove(key, out var familyId) &&
                    _families.TryGetValue(familyId, out var family))
                {
                    family.MemberSignatures.TryRemove(key, out _);
                    if (family.MemberSignatures.Count <= 1)
                        RemoveFamily(familyId);
                }
            }
        }
        return behaviors;
    }

    /// <summary>
    ///     Get statistics about tracked signatures.
    /// </summary>
    public (int TrackedSignatures, int TotalRequests, int AberrantSignatures) GetStatistics()
    {
        var stats = _signatureCache.GetStats();

        // Get counts from cache statistics
        // Note: Can't easily get total requests and aberrant count without iterating cache
        // This is a tradeoff for using the ephemeral pattern
        return (stats.ValidEntries, 0, 0);
    }

    /// <summary>
    ///     Get cache statistics for observability.
    /// </summary>
    public CacheStats GetCacheStats()
    {
        return _signatureCache.GetStats();
    }

    /// <summary>
    ///     Get all signals from coordinator-owned sink.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        return _signals.Sense();
    }
}

/// <summary>
///     Tracks requests for a single signature using ephemeral patterns.
///     Maintains a sliding window of requests and computes behavioral metrics.
/// </summary>
internal class SignatureTrackingAtom : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger;
    private readonly SignatureCoordinatorOptions _options;

    // Sliding window of requests (bounded by MaxRequestsPerSignature)
    private readonly LinkedList<SignatureRequest> _requests;
    private readonly string _signature;

    // Cached behavior (recomputed on each request)
    private SignatureBehavior? _cachedBehavior;

    public SignatureTrackingAtom(
        string signature,
        SignatureCoordinatorOptions options,
        ILogger logger)
    {
        _signature = signature;
        _options = options;
        _logger = logger;
        _requests = new LinkedList<SignatureRequest>();
    }

    public int RequestCount => _requests.Count;
    public bool IsAberrant => _cachedBehavior?.IsAberrant ?? false;

    public void Dispose()
    {
        _lock.Dispose();
    }

    public async Task RecordRequestAsync(
        SignatureRequest request,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Add to windowUse 
            _requests.AddLast(request);

            // Evict old requests (outside window)
            var cutoff = DateTime.UtcNow - _options.SignatureWindow;
            while (_requests.Count > 0 && _requests.First!.Value.Timestamp < cutoff) _requests.RemoveFirst();

            // Enforce max requests per signature
            while (_requests.Count > _options.MaxRequestsPerSignature) _requests.RemoveFirst();

            // Recompute behavior
            _cachedBehavior = ComputeBehavior();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SignatureBehavior> GetBehaviorAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _cachedBehavior ?? ComputeBehavior();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Compute behavioral metrics from the request window.
    ///     This is where cross-request pattern detection happens.
    /// </summary>
    private SignatureBehavior ComputeBehavior()
    {
        if (_requests.Count == 0)
            return new SignatureBehavior
            {
                Signature = _signature,
                Requests = new List<SignatureRequest>(),
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                RequestCount = 0,
                AverageInterval = 0,
                PathEntropy = 0,
                TimingCoefficient = 0,
                AverageBotProbability = 0,
                AberrationScore = 0,
                IsAberrant = false
            };

        var requestList = _requests.ToList();
        var firstSeen = requestList.First().Timestamp;
        var lastSeen = requestList.Last().Timestamp;

        // Calculate average interval
        var intervals = new List<double>();
        for (var i = 1; i < requestList.Count; i++)
            intervals.Add((requestList[i].Timestamp - requestList[i - 1].Timestamp).TotalSeconds);

        var avgInterval = intervals.Count > 0 ? intervals.Average() : 0;

        // Calculate timing coefficient of variation
        var timingCV = 0.0;
        if (intervals.Count > 1)
        {
            var stdDev = Math.Sqrt(intervals.Average(v => Math.Pow(v - avgInterval, 2)));
            timingCV = avgInterval > 0 ? stdDev / avgInterval : 0;
        }

        // Calculate path entropy (Shannon)
        var pathCounts = requestList.GroupBy(r => r.Path).ToDictionary(g => g.Key, g => g.Count());
        var pathEntropy = 0.0;
        if (pathCounts.Count > 1)
        {
            var total = requestList.Count;
            foreach (var count in pathCounts.Values)
            {
                var p = (double)count / total;
                if (p > 0)
                    pathEntropy -= p * Math.Log2(p);
            }
        }

        // Average bot probability across requests
        var avgBotProb = requestList.Average(r => r.BotProbability);

        // Compute aberration score (weighted combination)
        var aberrationScore = ComputeAberrationScore(
            requestList.Count,
            avgBotProb,
            pathEntropy,
            timingCV,
            avgInterval);

        var isAberrant = requestList.Count >= _options.MinRequestsForAberrationDetection &&
                         aberrationScore >= _options.AberrationScoreThreshold;

        return new SignatureBehavior
        {
            Signature = _signature,
            Requests = requestList,
            FirstSeen = firstSeen,
            LastSeen = lastSeen,
            RequestCount = requestList.Count,
            AverageInterval = avgInterval,
            PathEntropy = pathEntropy,
            TimingCoefficient = timingCV,
            AverageBotProbability = avgBotProb,
            AberrationScore = aberrationScore,
            IsAberrant = isAberrant
        };
    }

    /// <summary>
    ///     Compute aberration score from behavioral metrics.
    ///     This is where cross-request intelligence happens.
    /// </summary>
    private double ComputeAberrationScore(
        int requestCount,
        double avgBotProb,
        double pathEntropy,
        double timingCV,
        double avgInterval)
    {
        double score = 0;

        // High average bot probability across requests
        if (avgBotProb > 0.6)
            score += 0.3 * avgBotProb;

        // High path entropy (scanning)
        if (pathEntropy > 3.0)
            score += 0.25;

        // Too-regular timing (CV < 0.15)
        if (timingCV < 0.15 && requestCount > 5)
            score += 0.25;

        // Rapid requests (< 2 second average)
        if (avgInterval < 2.0 && requestCount > 5)
            score += 0.2;

        return Math.Min(score, 1.0);
    }
}
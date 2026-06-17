using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.KeyedSequential;
using Mostlylucid.Ephemeral.Atoms.SlidingCache;
using CacheStats = Mostlylucid.Ephemeral.Atoms.SlidingCache.CacheStats;
using DetectionContribution = Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution;

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
    /// <summary>Bytes sent in response; updated asynchronously after response completes.</summary>
    public long ResponseBytes { get; set; }

    /// <summary>
    ///     Contributions list from the pipeline pass that produced this request, when
    ///     available. Null on Skip-path observations (no fresh pipeline pass) and on the
    ///     warmup-rehydration path (snapshot of behavior only, not per-contribution
    ///     detail). The atom captures the most recent non-null value into the cached
    ///     verdict so the Skip path can rebuild detector_contributions on the dashboard
    ///     without re-running the pipeline.
    /// </summary>
    public IReadOnlyList<DetectionContribution>? Contributions { get; init; }
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
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? ContinentCode { get; init; }
    public string? RegionCode { get; init; }
    public bool IsVpn { get; init; }

    /// <summary>Cumulative response bytes sent to this signature in the tracking window.</summary>
    public long TotalResponseBytes { get; init; }

    /// <summary>Latest non-zero threat (intent) score observed across pipeline-running requests.</summary>
    public double LatestThreatScore { get; init; }

    /// <summary>True if a pipeline-running request marked this signature as a confirmed bad actor.</summary>
    public bool IsConfirmedBad { get; init; }

    /// <summary>
    ///     Contributions from the most recent pipeline-running observation. Snapshot
    ///     carried so the verdict gate's Skip path can rebuild the dashboard's
    ///     detector_contributions chips on a cache hit. Null until at least one
    ///     pipeline-running pass has populated them.
    /// </summary>
    public IReadOnlyList<DetectionContribution>? LatestContributions { get; init; }

    /// <summary>
    ///     Signals snapshot from the most recent pipeline-running observation. Lets the
    ///     verdict gate's Skip path rebuild important_signals on a cache hit without
    ///     re-running the pipeline.
    /// </summary>
    public IReadOnlyDictionary<string, object>? LatestSignals { get; init; }

    /// <summary>
    ///     True if a pipeline-running request marked this signature as a confirmed friendly
    ///     bot. Carries the result of FediverseDomainContributor's NodeInfo verification or
    ///     the commercial vendor-IP verifier -- i.e. one of FriendlyDomainVerified or
    ///     FriendlyIpVerified came back true on at least one request. Used by
    ///     BotClusterService to recognise Safe clusters (fediverse fanouts, verified
    ///     search-engine crawl bursts) so they can be classified separately from
    ///     BotProduct / BotNetwork even when they share behavioural shape with hostile
    ///     bots.
    /// </summary>
    public bool IsConfirmedFriendly { get; init; }
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
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? ContinentCode { get; init; }
    public string? RegionCode { get; init; }
    public bool IsVpn { get; init; }
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;
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

    private readonly ConcurrentQueue<IdentityRotationEvent> _rotationEvents = new();
    private readonly object _rotationEventsLock = new();
    private const int MaxRotationEvents = 100;

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
    ///     Prune shadow indexes when they grow larger than 2x the cache capacity.
    ///     The SlidingCacheAtom handles its own LRU eviction, but these indexes
    ///     aren't notified of evictions, so they need periodic bounding.
    /// </summary>
    private void PruneShadowIndexesIfNeeded()
    {
        var maxSize = _options.MaxSignaturesInWindow * 2;
        if (_knownSignatures.Count <= maxSize) return;

        // Prune _knownSignatures by oldest LastSeen
        var toEvict = _knownSignatures
            .OrderBy(kvp => kvp.Value.LastSeen)
            .Take(_knownSignatures.Count - _options.MaxSignaturesInWindow)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toEvict)
        {
            _knownSignatures.TryRemove(key, out _);
            if (_signatureToFamily.TryRemove(key, out var familyId))
            {
                // If family has no more members, remove it
                if (_families.TryGetValue(familyId, out var family) &&
                    family.MemberSignatures.Count <= 1)
                    _families.TryRemove(familyId, out _);
            }
        }

        // Prune _ipIndex: remove entries with no remaining signatures
        foreach (var kvp in _ipIndex)
        {
            foreach (var sig in kvp.Value.Keys.ToList())
                if (!_knownSignatures.ContainsKey(sig))
                    kvp.Value.TryRemove(sig, out _);

            if (kvp.Value.IsEmpty)
                _ipIndex.TryRemove(kvp.Key, out _);
        }

        _logger.LogDebug("Pruned {Count} stale shadow index entries (remaining: {Remaining})",
            toEvict.Count, _knownSignatures.Count);
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
        string? ipHash = null,
        DateTime? timestampUtc = null,
        IReadOnlyList<DetectionContribution>? contributions = null)
    {
        // Create request record
        var request = new SignatureRequest
        {
            RequestId = requestId,
            Timestamp = timestampUtc ?? DateTime.UtcNow,
            Path = path,
            BotProbability = botProbability,
            Signals = signals,
            DetectorsRan = detectorsRan,
            Escalated = false,
            Contributions = contributions
        };

        // Extract enriched geo context from signals dict
        var latitude = signals.TryGetValue("geo.latitude", out var latVal) && latVal is double lat ? lat : (double?)null;
        var longitude = signals.TryGetValue("geo.longitude", out var lonVal) && lonVal is double lon ? lon : (double?)null;
        var continentCode = signals.TryGetValue("geo.continent_code", out var ccVal2) ? ccVal2 as string : null;
        var regionCode = signals.TryGetValue("geo.region_code", out var rcVal) ? rcVal as string : null;
        var isVpn = signals.TryGetValue("geo.is_vpn", out var vpnVal) && vpnVal is true;

        // Track geo context for cluster analysis
        _knownSignatures.AddOrUpdate(
            signature,
            _ => new SignatureGeoContext
            {
                CountryCode = countryCode, Asn = asn, IsDatacenter = isDatacenter,
                Latitude = latitude, Longitude = longitude,
                ContinentCode = continentCode, RegionCode = regionCode, IsVpn = isVpn
            },
            (_, existing) => new SignatureGeoContext
            {
                CountryCode = countryCode ?? existing.CountryCode,
                Asn = asn ?? existing.Asn,
                IsDatacenter = isDatacenter || existing.IsDatacenter,
                Latitude = latitude ?? existing.Latitude,
                Longitude = longitude ?? existing.Longitude,
                ContinentCode = continentCode ?? existing.ContinentCode,
                RegionCode = regionCode ?? existing.RegionCode,
                IsVpn = isVpn || existing.IsVpn,
                LastSeen = DateTimeOffset.UtcNow
            });

        // Bound shadow indexes to prevent unbounded growth
        PruneShadowIndexesIfNeeded();

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
    ///     Lightweight observation hook used by the middleware on the Skip path. Bumps
    ///     the per-signature atom's last-seen time and request count, and appends the
    ///     observation to its in-window history, so clustering / drift analysis still
    ///     see Skip traffic. Implemented as a thin wrapper over <see cref="RecordRequestAsync"/>
    ///     with empty signals and no detector list: there is no separate state path,
    ///     and per-signature aberration detection keeps running on the Skip path. The
    ///     pipeline-running paths (Miss / Bias) still call <see cref="RecordRequestAsync"/>
    ///     directly with full signals.
    /// </summary>
    public Task NotifyObservationAsync(
        string signature,
        string path,
        double lastKnownBotProbability,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(signature))
            return Task.CompletedTask;

        // Skip-path race tolerance: the gate just saw the verdict, but the atom may
        // have been evicted before we got here. Silently drop in that case; the next
        // Bias/Miss request will repopulate.
        if (!_signatureCache.TryGet(signature, out var atom) || atom == null)
            return Task.CompletedTask;

        return atom.RecordRequestAsync(new SignatureRequest
        {
            RequestId = _skipObservationRequestId,
            Timestamp = DateTime.UtcNow,
            Path = path,
            BotProbability = lastKnownBotProbability,
            Signals = _emptySignals,
            DetectorsRan = _emptyDetectors,
            Escalated = false
        }, cancellationToken);
    }

    // Shared empties so the per-Skip observation allocates only one SignatureRequest
    // (down from: SignatureRequest + Guid + Dictionary + HashSet + SignatureGeoContext +
    // SignatureUpdateRequest on the original RecordRequestAsync path).
    private static readonly IReadOnlyDictionary<string, object> _emptySignals =
        new Dictionary<string, object>(0);
    private static readonly HashSet<string> _emptyDetectors = new();
    private const string _skipObservationRequestId = "skip";

    /// <summary>
    ///     Record response bytes for a specific request.
    ///     Called from OnCompleted after the response finishes, so the request was already recorded.
    /// </summary>
    public async Task RecordResponseBytesAsync(
        string signature,
        string requestId,
        long responseBytes,
        CancellationToken cancellationToken = default)
    {
        if (!_signatureCache.TryGet(signature, out var atom) || atom == null)
            return;

        await atom.UpdateResponseBytesAsync(requestId, responseBytes, cancellationToken);
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
    ///     Snapshot the per-signature state currently held in the sliding window. Returns
    ///     null when the signature is not in the window (cold or evicted). This is the
    ///     live verdict source consumed by SignatureVerdictGate; there is no parallel
    ///     cache. Async because the underlying tracking atom serialises field access
    ///     behind a SemaphoreSlim, the same way GetSignatureBehaviorAsync does.
    /// </summary>
    public async Task<SignatureVerdict?> TryGetVerdictAsync(
        string signatureId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(signatureId))
            return null;

        // Direct hit on the requesting signature is the common case. On miss, fall through
        // to the entity's canonical signature (if this signature is a member of a merged
        // family) so a rotating fingerprint inherits its merge sibling's verdict without
        // paying for a fresh pipeline pass.
        // Forgetting is implicit: the canonical-signature atom is itself subject to the
        // sliding-window TTL, so once the family-anchor goes cold the lookup naturally
        // misses and the request falls through to Miss. Splits drop the _signatureToFamily
        // entry, so post-split signatures stop inheriting.
        if (!_signatureCache.TryGet(signatureId, out var atom) || atom == null)
        {
            if (!_signatureToFamily.TryGetValue(signatureId, out var familyId) ||
                !_families.TryGetValue(familyId, out var family) ||
                string.Equals(family.CanonicalSignature, signatureId, StringComparison.OrdinalIgnoreCase) ||
                !_signatureCache.TryGet(family.CanonicalSignature, out atom) || atom == null)
            {
                return null;
            }
        }

        var behavior = await atom.GetBehaviorAsync(cancellationToken);

        // Confidence is not surfaced directly by the tracking atom yet, so derive it
        // from sample size: full confidence at 10+ requests, linear ramp below. This
        // mirrors the convention used in DetectorAggregator and keeps the verdict
        // self-contained until a future task extends the atom.
        var confidence = Math.Min(1.0, behavior.RequestCount / 10.0);

        // Cached verdicts never run AI; reuse the canonical band logic so a Skip-served
        // verdict classifies into the same band a fresh pipeline pass would have produced
        // given the same probability / threat / persistence inputs.
        var (riskBand, _, _) = DetectionLedgerExtensions.DetermineRiskBand(
            botProbability: behavior.AverageBotProbability,
            confidence: confidence,
            aiRan: false,
            threatScore: behavior.LatestThreatScore,
            isConfirmedBad: behavior.IsConfirmedBad,
            sessionRequestCount: behavior.RequestCount);

        return new SignatureVerdict
        {
            // Report the verdict under the *requested* signature ID so the gate / cache
            // header / dashboard see a continuous identity. The resolution path
            // (signatureId → family canonical) is internal and not surfaced.
            SignatureId = signatureId,
            BotProbability = behavior.AverageBotProbability,
            Confidence = confidence,
            RiskBand = riskBand,
            ThreatScore = behavior.LatestThreatScore,
            RequestCount = behavior.RequestCount,
            LastSeenUtc = behavior.LastSeen,
            LatestContributions = behavior.LatestContributions,
            LatestSignals = behavior.LatestSignals
        };
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
    ///     Record an identity rotation event (bounded ring buffer, max 100 entries).
    /// </summary>
    public void RecordRotationEvent(IdentityRotationEvent ev)
    {
        lock (_rotationEventsLock)
        {
            _rotationEvents.Enqueue(ev);
            while (_rotationEvents.Count > MaxRotationEvents)
                _rotationEvents.TryDequeue(out _);
        }
    }

    /// <summary>
    ///     Get a snapshot of recent identity rotation events.
    /// </summary>
    public IReadOnlyList<IdentityRotationEvent> GetRotationEvents()
        => _rotationEvents.ToArray();

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
                IsDatacenter = geo.IsDatacenter,
                Latitude = geo.Latitude,
                Longitude = geo.Longitude,
                ContinentCode = geo.ContinentCode,
                RegionCode = geo.RegionCode,
                IsVpn = geo.IsVpn
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
                        IsDatacenter = geoContext.IsDatacenter,
                        Latitude = geoContext.Latitude,
                        Longitude = geoContext.Longitude,
                        ContinentCode = geoContext.ContinentCode,
                        RegionCode = geoContext.RegionCode,
                        IsVpn = geoContext.IsVpn
                    });
                }
            }
            else
            {
                // Cache miss: atom not yet created (async processing still in flight) or evicted.
                // Only clean up known signatures — PruneShadowIndexesIfNeeded owns _ipIndex cleanup
                // to avoid a race where a newly-registered sig is removed before its atom is cached.
                _knownSignatures.TryRemove(key, out _);

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
    private long _totalResponseBytes;

    // Latest pipeline-derived risk inputs. Skip-path observations carry empty signals
    // and intentionally do NOT overwrite these, so the band stays representative of
    // the most recent full-detection pass.
    private double _latestThreatScore;
    private bool _isConfirmedBad;
    private bool _isConfirmedFriendly;

    // Latest pipeline-pass snapshot of contributions + signals. The verdict gate's
    // Skip path reads these off the surfaced SignatureBehavior so dashboard
    // detector_contributions + important_signals chips survive a cache hit (without
    // this every Skip-served row renders blank panels). Updated only by pipeline-
    // running observations -- Skip-path NotifyObservationAsync calls leave them
    // alone so the snapshot stays representative of the most recent fresh pass.
    private IReadOnlyList<DetectionContribution>? _latestContributions;
    private IReadOnlyDictionary<string, object>? _latestSignals;

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

    public async Task UpdateResponseBytesAsync(
        string requestId,
        long responseBytes,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            foreach (var req in _requests)
            {
                if (req.RequestId == requestId)
                {
                    req.ResponseBytes = responseBytes;
                    _totalResponseBytes += responseBytes;
                    break;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
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

            // Capture latest pipeline-derived risk inputs from this observation. Skip-path
            // observations pass an empty signals dict; we deliberately keep the most recent
            // non-empty values rather than clobbering them with zeros.
            if (request.Signals.Count > 0)
            {
                // Snapshot the latest signals dict so the verdict gate's Skip path can
                // rebuild important_signals on a cache hit. The dict passed to
                // RecordRequestAsync is already a defensive copy (the orchestrator
                // copies before queueing because the source pool resets after the
                // request returns), so we can hold the reference directly.
                _latestSignals = request.Signals;
                if (request.Contributions is { Count: > 0 })
                    _latestContributions = request.Contributions;

                if (request.Signals.TryGetValue(SignalKeys.IntentThreatScore, out var rawThreat)
                    && TryReadDouble(rawThreat, out var threat))
                {
                    _latestThreatScore = threat;
                }

                if ((request.Signals.TryGetValue(SignalKeys.ReputationCanAbort, out var canAbort) && canAbort is true) ||
                    (request.Signals.TryGetValue(SignalKeys.ReputationFastAbortActive, out var abortActive) && abortActive is true))
                {
                    _isConfirmedBad = true;
                }

                // Latch confirmed-friendly when either verification axis came back true.
                // FediverseDomainContributor writes FriendlyDomainVerified after the
                // NodeInfo lookup succeeds; the commercial vendor-IP verifier writes
                // FriendlyIpVerified after matching a Google / Bing / etc. published
                // range. A negative result (the bool is false) is NOT a latch -- that
                // means verification ran but failed, which is a spoof signal that
                // belongs on the IsConfirmedBad side via the existing reputation path.
                if ((request.Signals.TryGetValue(SignalKeys.FriendlyDomainVerified, out var fdv) && fdv is true) ||
                    (request.Signals.TryGetValue(SignalKeys.FriendlyIpVerified, out var fipv) && fipv is true))
                {
                    _isConfirmedFriendly = true;
                }
            }

            // Recompute behavior
            _cachedBehavior = ComputeBehavior();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool TryReadDouble(object? raw, out double value)
    {
        switch (raw)
        {
            case double d: value = d; return true;
            case float f:  value = f; return true;
            case int i:    value = i; return true;
            case long l:   value = l; return true;
            default:       value = 0.0; return false;
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
                IsAberrant = false,
                TotalResponseBytes = _totalResponseBytes,
                LatestThreatScore = _latestThreatScore,
                IsConfirmedBad = _isConfirmedBad,
                IsConfirmedFriendly = _isConfirmedFriendly,
                LatestContributions = _latestContributions,
                LatestSignals = _latestSignals
            };

        // ComputeBehavior runs inside the per-signature lock on every
        // RecordRequestAsync. Squash four LINQ passes (intervals.Average,
        // intervals.Average(delegate), GroupBy+ToDictionary, requests.Average)
        // into one loop over the LinkedList: variance can be computed
        // online via Welford, path entropy from a single Dictionary<string,int>,
        // bot-prob average from a running sum. Removes the requestList copy
        // (LinkedList -> List), the intermediate intervals List<double>, the
        // two LINQ delegates, the GroupBy enumerator, and the
        // requests-to-paths-dictionary copy. The returned
        // SignatureBehavior.Requests still needs a snapshot list -- consumers
        // iterate it independently; allocate it once at the end.
        var requestCount = _requests.Count;
        var requestList = new List<SignatureRequest>(requestCount);
        DateTime firstSeen = default;
        DateTime lastSeen = default;
        DateTime? prevTimestamp = null;
        var intervalCount = 0;
        var intervalSum = 0.0;
        var intervalSqSum = 0.0;
        var botProbSum = 0.0;
        var pathCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var r in _requests)
        {
            requestList.Add(r);
            if (prevTimestamp is null)
                firstSeen = r.Timestamp;
            else
            {
                var dt = (r.Timestamp - prevTimestamp.Value).TotalSeconds;
                intervalSum += dt;
                intervalSqSum += dt * dt;
                intervalCount++;
            }
            lastSeen = r.Timestamp;
            prevTimestamp = r.Timestamp;
            botProbSum += r.BotProbability;
            pathCounts[r.Path] = pathCounts.TryGetValue(r.Path, out var existing) ? existing + 1 : 1;
        }

        var avgInterval = intervalCount > 0 ? intervalSum / intervalCount : 0;
        var timingCV = 0.0;
        if (intervalCount > 1 && avgInterval > 0)
        {
            var variance = (intervalSqSum / intervalCount) - (avgInterval * avgInterval);
            if (variance < 0) variance = 0; // floating-point edge
            timingCV = Math.Sqrt(variance) / avgInterval;
        }

        var pathEntropy = 0.0;
        if (pathCounts.Count > 1)
        {
            var total = (double)requestCount;
            foreach (var count in pathCounts.Values)
            {
                var p = count / total;
                if (p > 0) pathEntropy -= p * Math.Log2(p);
            }
        }

        var avgBotProb = requestCount > 0 ? botProbSum / requestCount : 0;

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
            IsAberrant = isAberrant,
            TotalResponseBytes = _totalResponseBytes,
            LatestThreatScore = _latestThreatScore,
            IsConfirmedBad = _isConfirmedBad,
            IsConfirmedFriendly = _isConfirmedFriendly,
            LatestContributions = _latestContributions,
            LatestSignals = _latestSignals
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

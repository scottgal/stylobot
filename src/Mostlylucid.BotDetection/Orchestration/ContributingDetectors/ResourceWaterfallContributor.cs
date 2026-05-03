using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Detects scrapers that fetch HTML documents but never load sub-resources.
///     Real browsers loading a page will subsequently request CSS, JS, images, and fonts.
///     Scrapers that only fetch HTML but never load sub-resources are clearly not rendering.
///
///     Tracks per-signature:
///     - Document-to-asset ratio (real browsers load 3-5+ assets per page)
///     - Font loading absence (real browsers load WOFF2 fonts)
///     - Favicon absence (real browsers request /favicon.ico)
///
///     Configuration loaded from: resourcewaterfall.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:ResourceWaterfallContributor:*
/// </summary>
public class ResourceWaterfallContributor : ConfiguredContributorBase
{
    private readonly ILogger<ResourceWaterfallContributor> _logger;
    private readonly IMemoryCache _cache;

    private const string CachePrefix = "resourcewaterfall:";

    // Configurable via YAML
    private int MinDocumentsForAnalysis => GetParam("min_documents_for_analysis", 3);
    private double NoAssetsConfidence => GetParam("no_assets_confidence", 0.5);
    private double NoAssetsWeight => GetParam("no_assets_weight", 1.6);
    private double LowRatioThreshold => GetParam("low_ratio_threshold", 0.5);
    private double LowRatioConfidence => GetParam("low_ratio_confidence", 0.25);
    private double HealthyRatioThreshold => GetParam("healthy_ratio_threshold", 2.0);
    private double HealthyRatioHumanConfidence => GetParam("healthy_ratio_human_confidence", -0.15);
    private double NoFontsConfidence => GetParam("no_fonts_confidence", 0.1);
    private double FaviconHumanConfidence => GetParam("favicon_human_confidence", -0.05);
    private int CacheExpirationMinutes => GetParam("cache_expiration_minutes", 30);

    public ResourceWaterfallContributor(
        ILogger<ResourceWaterfallContributor> logger,
        IDetectorConfigProvider configProvider,
        IMemoryCache cache) : base(configProvider)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "ResourceWaterfall";
    public override int Priority => 22; // After behavioral (20), before periodicity (25)

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.TransportProtocolClass),
        SequenceGuardTrigger.Default
    ];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = new Dictionary<string, object>();

        var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        // Classify this request
        var request = state.HttpContext.Request;
        var secFetchDest = request.Headers["Sec-Fetch-Dest"].FirstOrDefault()?.ToLowerInvariant() ?? "";
        var accept = request.Headers["Accept"].FirstOrDefault() ?? "";
        var path = request.Path.Value?.ToLowerInvariant() ?? "";

        var isDocument = IsDocumentRequest(secFetchDest, accept);
        var isAsset = IsAssetRequest(secFetchDest, path);
        var isFont = IsFontRequest(secFetchDest, path);
        var isFavicon = path is "/favicon.ico" or "/favicon.svg";

        // Update per-signature tracking
        var tracker = GetOrCreateTracker(signature);
        if (isDocument) tracker.DocumentCount++;
        if (isAsset) tracker.AssetCount++;
        if (isFont) tracker.FontRequested = true;
        if (isFavicon) tracker.FaviconRequested = true;
        SaveTracker(signature, tracker);

        // Write signals to blackboard
        signals[SignalKeys.ResourceDocumentCount] = tracker.DocumentCount;
        signals[SignalKeys.ResourceAssetCount] = tracker.AssetCount;

        var assetRatio = tracker.DocumentCount > 0
            ? (double)tracker.AssetCount / tracker.DocumentCount
            : 0.0;
        signals[SignalKeys.ResourceAssetRatio] = assetRatio;
        signals[SignalKeys.ResourceFontRequested] = tracker.FontRequested;
        signals[SignalKeys.ResourceFaviconRequested] = tracker.FaviconRequested;

        foreach (var (key, value) in signals)
            state.WriteSignal(key, value);

        // === Scoring ===

        // Not enough data yet
        if (tracker.DocumentCount < MinDocumentsForAnalysis)
        {
            contributions.Add(NeutralContribution("ResourceWaterfall", "Insufficient document requests for analysis"));
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        // Strong bot signal: multiple documents, zero assets (no rendering at all)
        // Skip if sequence.cache_warm — browser has warm CDN cache, no static asset requests expected
        var cacheWarm = state.Signals.TryGetValue(SignalKeys.SequenceCacheWarm, out var cwObj) && cwObj is bool cw && cw;
        if (tracker.AssetCount == 0 && !cacheWarm)
        {
            contributions.Add(BotContribution(
                "NoAssets",
                $"Fetched {tracker.DocumentCount} documents with zero sub-resource requests - not rendering",
                confidenceOverride: NoAssetsConfidence,
                botType: BotType.Scraper.ToString()));

            _logger.LogDebug("ResourceWaterfall: {Sig} has {Docs} documents, 0 assets",
                signature[..Math.Min(8, signature.Length)], tracker.DocumentCount);
        }
        // Moderate bot signal: low asset-to-document ratio
        else if (tracker.DocumentCount >= 5 && assetRatio < LowRatioThreshold)
        {
            contributions.Add(BotContribution(
                "LowAssetRatio",
                $"Low asset/document ratio ({assetRatio:F2}) - partial rendering or selective scraping",
                confidenceOverride: LowRatioConfidence,
                botType: BotType.Scraper.ToString()));
        }
        // Human signal: healthy asset-to-document ratio (browsers load multiple assets per page)
        else if (assetRatio >= HealthyRatioThreshold)
        {
            contributions.Add(HumanContribution(
                "HealthyAssetRatio",
                $"Healthy asset/document ratio ({assetRatio:F2}) - normal browser rendering"));
        }

        // Weak bot signal: no fonts loaded after many documents
        if (!tracker.FontRequested && tracker.DocumentCount >= 5)
        {
            contributions.Add(BotContribution(
                "NoFonts",
                $"No font requests after {tracker.DocumentCount} documents - browsers typically load web fonts",
                confidenceOverride: NoFontsConfidence,
                botType: BotType.Scraper.ToString()));
        }

        // Weak human signal: favicon requested (browsers do this automatically)
        if (tracker.FaviconRequested)
        {
            contributions.Add(HumanContribution(
                "FaviconRequested",
                "Favicon requested - typical browser behavior"));
        }

        // If no contributions beyond neutral, emit neutral
        if (contributions.Count == 0)
            contributions.Add(NeutralContribution("ResourceWaterfall", "Resource loading pattern inconclusive"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private static bool IsDocumentRequest(string secFetchDest, string accept)
    {
        // Sec-Fetch-Dest is the authoritative signal from modern browsers
        if (secFetchDest is "document" or "iframe")
            return true;

        // Fallback: Accept header contains text/html (older browsers, non-Fetch-Metadata clients)
        if (string.IsNullOrEmpty(secFetchDest) && accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsAssetRequest(string secFetchDest, string path)
    {
        // Sec-Fetch-Dest for sub-resources
        if (secFetchDest is "script" or "style" or "image" or "font" or "audio" or "video")
            return true;

        // Fallback: file extension matching for clients that don't send Sec-Fetch-Dest
        if (string.IsNullOrEmpty(secFetchDest) &&
            (path.EndsWith(".css") || path.EndsWith(".js") ||
             path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg") ||
             path.EndsWith(".gif") || path.EndsWith(".svg") || path.EndsWith(".ico") ||
             path.EndsWith(".woff2") || path.EndsWith(".woff") || path.EndsWith(".ttf") ||
             path.EndsWith(".webp") || path.EndsWith(".avif")))
            return true;

        return false;
    }

    private static bool IsFontRequest(string secFetchDest, string path)
    {
        if (secFetchDest == "font")
            return true;

        if (string.IsNullOrEmpty(secFetchDest) &&
            (path.EndsWith(".woff2") || path.EndsWith(".woff") || path.EndsWith(".ttf") ||
             path.EndsWith(".otf") || path.EndsWith(".eot")))
            return true;

        return false;
    }

    private ResourceTracker GetOrCreateTracker(string signature)
    {
        var key = $"{CachePrefix}{signature}";
        return _cache.Get<ResourceTracker>(key) ?? new ResourceTracker();
    }

    private void SaveTracker(string signature, ResourceTracker tracker)
    {
        var key = $"{CachePrefix}{signature}";
        _cache.Set(key, tracker, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(CacheExpirationMinutes)
        });
    }

    /// <summary>
    ///     Per-signature resource loading tracker.
    ///     Stored in IMemoryCache with sliding expiration.
    /// </summary>
    private class ResourceTracker
    {
        public int DocumentCount { get; set; }
        public int AssetCount { get; set; }
        public bool FontRequested { get; set; }
        public bool FaviconRequested { get; set; }
    }
}
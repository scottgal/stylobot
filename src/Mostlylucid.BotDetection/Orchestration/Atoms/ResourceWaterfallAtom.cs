using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that detects scrapers that fetch
///     HTML documents but never load sub-resources. Real browsers loading a
///     page subsequently request CSS, JS, images, and fonts.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ResourceWaterfallContributor</c>. Priority 22.
///     </para>
///     <para>
///         Per-signature <see cref="ResourceTracker"/> lives in
///         <see cref="IMemoryCache"/> under the primary signature -- a hash
///         same identifier grade as fingerprint IDs already resident in the
///         sink. The tracker's counts / booleans replay to the sink as
///         Model-2 hints so downstream atoms can read them without touching
///         the cache directly.
///     </para>
///     <para>
///         Inline <c>SequenceGuardTrigger.Default</c> port + TransportProtocolClass
///         required signal, matching the legacy trigger set.
///     </para>
/// </remarks>
public sealed class ResourceWaterfallAtom : DetectorAtomBase
{
    private const string CachePrefix = "resourcewaterfall:";
    private const int SequenceMinPosition = 3;

    private readonly ILogger<ResourceWaterfallAtom> _logger;
    private readonly IMemoryCache _cache;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ResourceWaterfallAtom(
        ILogger<ResourceWaterfallAtom> logger,
        IDetectorConfigProvider configProvider,
        IMemoryCache cache,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "ResourceWaterfall", category: "ResourceWaterfall")
    {
        _logger = logger;
        _cache = cache;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 22;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.TransportProtocolClass };

    private int MinDocumentsForAnalysis => _configProvider.GetParameter(Name, "min_documents_for_analysis", 3);
    private double NoAssetsConfidence => _configProvider.GetParameter(Name, "no_assets_confidence", 0.5);
    private double NoAssetsWeight => _configProvider.GetParameter(Name, "no_assets_weight", 1.6);
    private double LowRatioThreshold => _configProvider.GetParameter(Name, "low_ratio_threshold", 0.5);
    private double LowRatioConfidence => _configProvider.GetParameter(Name, "low_ratio_confidence", 0.25);
    private double HealthyRatioThreshold => _configProvider.GetParameter(Name, "healthy_ratio_threshold", 2.0);
    private double HealthyRatioHumanConfidence => _configProvider.GetParameter(Name, "healthy_ratio_human_confidence", -0.15);
    private double NoFontsConfidence => _configProvider.GetParameter(Name, "no_fonts_confidence", 0.1);
    private double FaviconHumanConfidence => _configProvider.GetParameter(Name, "favicon_human_confidence", -0.05);
    private int CacheExpirationMinutes => _configProvider.GetParameter(Name, "cache_expiration_minutes", 30);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        if (!ShouldRunUnderSequenceGuard(sink))
            return Task.FromResult(None());

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var signature = sink.ReadHint(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return Task.FromResult(None());

        sink.Raise("resource.waterfall.ran", sessionId);

        var request = context.Request;
        var secFetchDest = request.Headers["Sec-Fetch-Dest"].FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
        var accept = request.Headers["Accept"].FirstOrDefault() ?? string.Empty;
        var path = request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        var isDocument = IsDocumentRequest(secFetchDest, accept);
        var isAsset = IsAssetRequest(secFetchDest, path);
        var isFont = IsFontRequest(secFetchDest, path);
        var isFavicon = path is "/favicon.ico" or "/favicon.svg";
        var isApiCall = secFetchDest == "empty"
            || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

        var tracker = GetOrCreateTracker(signature);
        if (isDocument) tracker.DocumentCount++;
        if (isAsset) tracker.AssetCount++;
        if (isFont) tracker.FontRequested = true;
        if (isFavicon) tracker.FaviconRequested = true;
        if (isApiCall) tracker.HasApiCalls = true;
        SaveTracker(signature, tracker);

        var assetRatio = tracker.DocumentCount > 0
            ? (double)tracker.AssetCount / tracker.DocumentCount
            : 0.0;

        sink.Raise($"{SignalKeys.ResourceDocumentCount}:{tracker.DocumentCount}", sessionId);
        sink.Raise($"{SignalKeys.ResourceAssetCount}:{tracker.AssetCount}", sessionId);
        sink.Raise($"{SignalKeys.ResourceAssetRatio}:{assetRatio.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.ResourceFontRequested}:{(tracker.FontRequested ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.ResourceFaviconRequested}:{(tracker.FaviconRequested ? "true" : "false")}", sessionId);

        var contributions = new List<DetectionContribution>();

        if (tracker.DocumentCount < MinDocumentsForAnalysis)
        {
            contributions.Add(DetectionContribution.Info(
                Name, Category, "Insufficient document requests for analysis"));
            return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
        }

        var cacheWarm = sink.ReadBoolHint(SignalKeys.SequenceCacheWarm);

        // Strong bot: multiple documents, zero assets, no cache-warm, no API calls
        if (tracker.AssetCount == 0 && !cacheWarm && !tracker.HasApiCalls)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "NoAssets",
                ConfidenceDelta = NoAssetsConfidence,
                Weight = NoAssetsWeight,
                Reason = $"Fetched {tracker.DocumentCount} documents with zero sub-resource requests - not rendering",
                BotType = BotType.Scraper.ToString()
            });

            _logger.LogDebug("ResourceWaterfall: {Sig} has {Docs} documents, 0 assets",
                signature[..Math.Min(8, signature.Length)], tracker.DocumentCount);
        }
        else if (tracker.DocumentCount >= 5 && assetRatio < LowRatioThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "LowAssetRatio",
                ConfidenceDelta = LowRatioConfidence,
                Weight = 1.0,
                Reason = $"Low asset/document ratio ({assetRatio:F2}) - partial rendering or selective scraping",
                BotType = BotType.Scraper.ToString()
            });
        }
        else if (assetRatio >= HealthyRatioThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "HealthyAssetRatio",
                ConfidenceDelta = HealthyRatioHumanConfidence,
                Weight = 1.0,
                Reason = $"Healthy asset/document ratio ({assetRatio:F2}) - normal browser rendering"
            });
        }

        if (!tracker.FontRequested && tracker.DocumentCount >= 5)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "NoFonts",
                ConfidenceDelta = NoFontsConfidence,
                Weight = 1.0,
                Reason = $"No font requests after {tracker.DocumentCount} documents - browsers typically load web fonts",
                BotType = BotType.Scraper.ToString()
            });
        }

        if (tracker.FaviconRequested)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "FaviconRequested",
                ConfidenceDelta = FaviconHumanConfidence,
                Weight = 1.0,
                Reason = "Favicon requested - typical browser behavior"
            });
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "Resource loading pattern inconclusive"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private static bool ShouldRunUnderSequenceGuard(SignalSink sink)
    {
        var positionHint = sink.ReadHint(SignalKeys.SequencePosition);
        if (positionHint is null) return true;
        if (!sink.ReadBoolHint(SignalKeys.SequenceOnTrack, fallback: true)) return true;
        if (sink.ReadBoolHint(SignalKeys.SequenceDiverged)) return true;
        return int.TryParse(positionHint, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos)
               && pos >= SequenceMinPosition;
    }

    private static bool IsDocumentRequest(string secFetchDest, string accept)
    {
        if (secFetchDest is "document" or "iframe") return true;
        if (string.IsNullOrEmpty(secFetchDest) && accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool IsAssetRequest(string secFetchDest, string path)
    {
        if (secFetchDest is "script" or "style" or "image" or "font" or "audio" or "video")
            return true;

        if (string.IsNullOrEmpty(secFetchDest) &&
            (path.EndsWith(".css") || path.EndsWith(".js")
             || path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg")
             || path.EndsWith(".gif") || path.EndsWith(".svg") || path.EndsWith(".ico")
             || path.EndsWith(".woff2") || path.EndsWith(".woff") || path.EndsWith(".ttf")
             || path.EndsWith(".webp") || path.EndsWith(".avif")))
            return true;

        return false;
    }

    private static bool IsFontRequest(string secFetchDest, string path)
    {
        if (secFetchDest == "font") return true;
        if (string.IsNullOrEmpty(secFetchDest)
            && (path.EndsWith(".woff2") || path.EndsWith(".woff") || path.EndsWith(".ttf")
                || path.EndsWith(".otf") || path.EndsWith(".eot")))
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

    private sealed class ResourceTracker
    {
        public int DocumentCount { get; set; }
        public int AssetCount { get; set; }
        public bool FontRequested { get; set; }
        public bool FaviconRequested { get; set; }
        public bool HasApiCalls { get; set; }
    }
}

using System.IO.Hashing;
using System.Text;
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
///     ConstrainerAtom (per Taxonomy.md) that catches attackers hiding
///     behind streaming traffic. Detects handshake storms, cross-endpoint
///     mixing, SSE reconnect abuse, and concurrent stream endpoint probing.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>StreamAbuseContributor</c>. Priority 35.
///     </para>
///     <para>
///         Per-signature <see cref="StreamActivityWindow"/> lives in
///         <see cref="IMemoryCache"/>. Sink learns aggregate booleans /
///         counts / rates -- no timestamp series leaked.
///     </para>
///     <para>
///         Triggers: <see cref="SignalKeys.TransportProtocol"/> AND
///         <see cref="SignalKeys.PrimarySignature"/> both required. The
///         SignalR-expected carve-out (which the contributor expressed as a
///         <c>SignalNotExistsTrigger</c>) is inlined here since RequiredSignals
///         is intersection-only.
///     </para>
/// </remarks>
public sealed class StreamAbuseAtom : DetectorAtomBase
{
    private const string CacheKeyPrefix = "stream:";

    private readonly IMemoryCache _cache;
    private readonly ILogger<StreamAbuseAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public StreamAbuseAtom(
        ILogger<StreamAbuseAtom> logger,
        IMemoryCache cache,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "StreamAbuse", category: "StreamAbuse")
    {
        _logger = logger;
        _cache = cache;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 35;
    public override IReadOnlyList<string> RequiredSignals => new[]
    {
        SignalKeys.TransportProtocol,
        SignalKeys.PrimarySignature
    };

    private int HandshakeStormThreshold => _configProvider.GetParameter(Name, "handshake_storm_threshold", 10);
    private int HandshakeStormWindowSeconds => _configProvider.GetParameter(Name, "handshake_storm_window_seconds", 60);
    private double HandshakeStormConfidence => _configProvider.GetParameter(Name, "handshake_storm_confidence", 0.65);
    private double HandshakeStormWeight => _configProvider.GetParameter(Name, "handshake_storm_weight", 1.8);
    private int CrossEndpointMinStreamRequests => _configProvider.GetParameter(Name, "cross_endpoint_mixing_min_stream_requests", 3);
    private int CrossEndpointMinPageRequests => _configProvider.GetParameter(Name, "cross_endpoint_mixing_min_page_requests", 5);
    private double CrossEndpointMaxAssetRatio => _configProvider.GetParameter(Name, "cross_endpoint_mixing_max_asset_ratio", 0.2);
    private double CrossEndpointConfidence => _configProvider.GetParameter(Name, "cross_endpoint_mixing_confidence", 0.6);
    private double CrossEndpointWeight => _configProvider.GetParameter(Name, "cross_endpoint_mixing_weight", 2.0);
    private int SseReconnectRateThreshold => _configProvider.GetParameter(Name, "sse_reconnect_rate_threshold", 20);
    private int SseReconnectWindowSeconds => _configProvider.GetParameter(Name, "sse_reconnect_rate_window_seconds", 60);
    private double SseReconnectConfidence => _configProvider.GetParameter(Name, "sse_reconnect_confidence", 0.5);
    private double SseReconnectWeight => _configProvider.GetParameter(Name, "sse_reconnect_weight", 1.5);
    private int ConcurrentStreamsThreshold => _configProvider.GetParameter(Name, "concurrent_streams_threshold", 5);
    private double ConcurrentStreamsConfidence => _configProvider.GetParameter(Name, "concurrent_streams_confidence", 0.45);
    private double ConcurrentStreamsWeight => _configProvider.GetParameter(Name, "concurrent_streams_weight", 1.3);
    private int CacheSlidingExpirationSeconds => _configProvider.GetParameter(Name, "cache_sliding_expiration_seconds", 300);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // SignalR-expected carve-out: legacy contributor used a
        // SignalNotExistsTrigger on SequenceSignalRExpected. Inline here
        // since RequiredSignals is intersection-only.
        if (sink.ReadHint(SignalKeys.SequenceSignalRExpected) is not null)
            return Task.FromResult(None());

        var signature = sink.ReadHint(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature)) return Task.FromResult(None());

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());
        sink.Raise($"{SignalKeys.StreamAbuseChecked}:true", sessionId);

        var contributions = new List<DetectionContribution>();

        try
        {
            var isStreaming = sink.ReadBoolHint(SignalKeys.TransportIsStreaming);

            if (!isStreaming)
            {
                var window = GetOrCreateWindow(signature);
                RecordNonStreamingRequest(context, window);
                WriteWindowSignals(sink, sessionId, window);
                CheckCrossEndpointMixing(sink, sessionId, window, contributions);

                if (contributions.Count == 0)
                    contributions.Add(DetectionContribution.Info(Name, Category, "Stream abuse check - non-streaming request"));
                return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
            }

            var activityWindow = GetOrCreateWindow(signature);
            var now = DateTimeOffset.UtcNow;
            var protocol = sink.ReadHint(SignalKeys.TransportProtocol) ?? "http";
            var path = context.Request.Path.Value ?? "/";
            var pathHash = GetPathHash(path);

            activityWindow.StreamRequests++;
            activityWindow.StreamEndpoints.Add(pathHash);

            if (protocol == "websocket") activityWindow.WsUpgrades.Add(now);

            if (sink.ReadBoolHint(SignalKeys.TransportSseReconnect))
                activityWindow.SseReconnects.Add(now);

            PruneTimestamps(activityWindow.WsUpgrades, now, TimeSpan.FromSeconds(HandshakeStormWindowSeconds));
            PruneTimestamps(activityWindow.SseReconnects, now, TimeSpan.FromSeconds(SseReconnectWindowSeconds));
            WriteWindowSignals(sink, sessionId, activityWindow);

            CheckHandshakeStorm(activityWindow, contributions);
            CheckSseReconnectRate(activityWindow, contributions);
            CheckConcurrentStreams(activityWindow, contributions);
            CheckCrossEndpointMixing(sink, sessionId, activityWindow, contributions);

            if (contributions.Count == 0)
                contributions.Add(DetectionContribution.Info(Name, Category, "Stream abuse check - normal streaming activity"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in stream abuse analysis");
            contributions.Add(DetectionContribution.Info(Name, Category, "Stream abuse analysis error"));
        }

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private void CheckHandshakeStorm(StreamActivityWindow window, List<DetectionContribution> contributions)
    {
        if (window.WsUpgrades.Count < HandshakeStormThreshold) return;
        contributions.Add(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = HandshakeStormConfidence,
            Weight = HandshakeStormWeight,
            Reason = $"WebSocket handshake storm: {window.WsUpgrades.Count} upgrades in {HandshakeStormWindowSeconds}s window",
            BotType = BotType.MaliciousBot.ToString()
        });
    }

    private void CheckSseReconnectRate(StreamActivityWindow window, List<DetectionContribution> contributions)
    {
        if (window.SseReconnects.Count < SseReconnectRateThreshold) return;
        var reconnectRate = ComputeReconnectRate(window);
        contributions.Add(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = SseReconnectConfidence,
            Weight = SseReconnectWeight,
            Reason = $"SSE reconnect abuse: {window.SseReconnects.Count} reconnects in {SseReconnectWindowSeconds}s window ({reconnectRate:F1}/min)",
            BotType = BotType.MaliciousBot.ToString()
        });
    }

    private void CheckConcurrentStreams(StreamActivityWindow window, List<DetectionContribution> contributions)
    {
        if (window.StreamEndpoints.Count < ConcurrentStreamsThreshold) return;
        contributions.Add(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = ConcurrentStreamsConfidence,
            Weight = ConcurrentStreamsWeight,
            Reason = $"Streaming to {window.StreamEndpoints.Count} distinct endpoints (probing for open streams)",
            BotType = BotType.Scraper.ToString()
        });
    }

    private void CheckCrossEndpointMixing(
        SignalSink sink, string sessionId, StreamActivityWindow window, List<DetectionContribution> contributions)
    {
        if (window.StreamRequests < CrossEndpointMinStreamRequests) return;
        if (window.PageRequests < CrossEndpointMinPageRequests) return;

        var total = window.PageRequests + window.AssetRequests + window.StreamRequests;
        if (total == 0) return;

        var assetRatio = (double)window.AssetRequests / total;
        if (assetRatio >= CrossEndpointMaxAssetRatio) return;

        sink.Raise($"{SignalKeys.StreamCrossEndpointMixing}:true", sessionId);
        contributions.Add(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = CrossEndpointConfidence,
            Weight = CrossEndpointWeight,
            Reason = $"Cross-endpoint mixing: {window.StreamRequests} stream + {window.PageRequests} page requests with low asset ratio ({assetRatio:P0}) - scraping behind streaming cover",
            BotType = BotType.Scraper.ToString()
        });
    }

    private static void RecordNonStreamingRequest(HttpContext context, StreamActivityWindow window)
    {
        var path = context.Request.Path.Value ?? "/";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".js" or ".css" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico"
            or ".woff" or ".woff2" or ".ttf" or ".eot" or ".webp" or ".avif")
            window.AssetRequests++;
        else if (string.IsNullOrEmpty(ext) || ext is ".html" or ".htm")
            window.PageRequests++;
    }

    private void WriteWindowSignals(SignalSink sink, string sessionId, StreamActivityWindow window)
    {
        if (window.WsUpgrades.Count >= HandshakeStormThreshold)
            sink.Raise($"{SignalKeys.StreamHandshakeStorm}:true", sessionId);
        if (window.StreamEndpoints.Count > 1)
            sink.Raise($"{SignalKeys.StreamConcurrentStreams}:{window.StreamEndpoints.Count}", sessionId);

        var reconnectRate = ComputeReconnectRate(window);
        if (reconnectRate > 0)
            sink.Raise($"{SignalKeys.StreamReconnectRate}:{reconnectRate.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);
    }

    private StreamActivityWindow GetOrCreateWindow(string signature)
    {
        var cacheKey = CacheKeyPrefix + signature;
        return _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromSeconds(CacheSlidingExpirationSeconds);
            return new StreamActivityWindow();
        })!;
    }

    private static void PruneTimestamps(List<DateTimeOffset> timestamps, DateTimeOffset now, TimeSpan window)
    {
        var cutoff = now - window;
        timestamps.RemoveAll(t => t < cutoff);
    }

    private double ComputeReconnectRate(StreamActivityWindow window)
    {
        if (window.SseReconnects.Count == 0 || SseReconnectWindowSeconds <= 0) return 0.0;
        return window.SseReconnects.Count * 60.0 / SseReconnectWindowSeconds;
    }

    private static string GetPathHash(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        return XxHash32.HashToUInt32(bytes).ToString("X8");
    }

    private sealed class StreamActivityWindow
    {
        public List<DateTimeOffset> WsUpgrades { get; } = new();
        public List<DateTimeOffset> SseReconnects { get; } = new();
        public HashSet<string> StreamEndpoints { get; } = new();
        public int PageRequests { get; set; }
        public int AssetRequests { get; set; }
        public int StreamRequests { get; set; }
    }
}

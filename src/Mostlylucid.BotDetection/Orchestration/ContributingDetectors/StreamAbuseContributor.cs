using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Stream abuse detection contributor - catches attackers hiding behind streaming traffic.
///     Tracks per-signature streaming activity via IMemoryCache to detect:
///     - Handshake storms (excessive WS upgrades)
///     - Cross-endpoint mixing (streaming + page-scraping from same signature)
///     - SSE reconnect rate abuse
///     - Concurrent stream endpoint probing
///
///     Runs in Wave 1+ (after TransportProtocol and BehavioralWaveform emit signals).
///     Configuration loaded from: stream-abuse.detector.yaml
/// </summary>
public class StreamAbuseContributor : ConfiguredContributorBase
{
    private const string CacheKeyPrefix = "stream:";
    private readonly IMemoryCache _cache;
    private readonly ILogger<StreamAbuseContributor> _logger;

    public StreamAbuseContributor(
        ILogger<StreamAbuseContributor> logger,
        IMemoryCache cache,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "StreamAbuse";
    public override int Priority => 35; // After behavioral detectors

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.TransportProtocol),
        new SignalExistsTrigger(SignalKeys.PrimarySignature),
        // Skip if this is an expected SignalR continuation from a known-good document sequence
        new SignalNotExistsTrigger(SignalKeys.SequenceSignalRExpected)
    ];

    // Config-driven parameters from YAML - no magic numbers
    private int HandshakeStormThreshold => GetParam("handshake_storm_threshold", 10);
    private int HandshakeStormWindowSeconds => GetParam("handshake_storm_window_seconds", 60);
    private double HandshakeStormConfidence => GetParam("handshake_storm_confidence", 0.65);
    private double HandshakeStormWeight => GetParam("handshake_storm_weight", 1.8);

    private int CrossEndpointMinStreamRequests => GetParam("cross_endpoint_mixing_min_stream_requests", 3);
    private int CrossEndpointMinPageRequests => GetParam("cross_endpoint_mixing_min_page_requests", 5);
    private double CrossEndpointMaxAssetRatio => GetParam("cross_endpoint_mixing_max_asset_ratio", 0.2);
    private double CrossEndpointConfidence => GetParam("cross_endpoint_mixing_confidence", 0.6);
    private double CrossEndpointWeight => GetParam("cross_endpoint_mixing_weight", 2.0);

    private int SseReconnectRateThreshold => GetParam("sse_reconnect_rate_threshold", 20);
    private int SseReconnectWindowSeconds => GetParam("sse_reconnect_rate_window_seconds", 60);
    private double SseReconnectConfidence => GetParam("sse_reconnect_confidence", 0.5);
    private double SseReconnectWeight => GetParam("sse_reconnect_weight", 1.5);

    private int ConcurrentStreamsThreshold => GetParam("concurrent_streams_threshold", 5);
    private double ConcurrentStreamsConfidence => GetParam("concurrent_streams_confidence", 0.45);
    private double ConcurrentStreamsWeight => GetParam("concurrent_streams_weight", 1.3);

    private int CacheSlidingExpirationSeconds => GetParam("cache_sliding_expiration_seconds", 300);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var isStreaming = state.GetSignal<bool?>(SignalKeys.TransportIsStreaming) ?? false;
            state.WriteSignal(SignalKeys.StreamAbuseChecked, true);

            if (!isStreaming)
            {
                // Not a streaming request - still check cross-endpoint mixing
                // (attacker doing page scraping alongside their streaming)
                var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
                if (!string.IsNullOrEmpty(signature))
                {
                    var window = GetOrCreateWindow(signature);
                    RecordNonStreamingRequest(state, window);
                    WriteWindowSignals(state, window);
                    CheckCrossEndpointMixing(state, window, contributions);
                }

                if (contributions.Count == 0)
                    contributions.Add(NeutralContribution("StreamAbuse", "Stream abuse check - non-streaming request"));

                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            var sig = state.GetSignal<string>(SignalKeys.PrimarySignature);
            if (string.IsNullOrEmpty(sig))
            {
                contributions.Add(NeutralContribution("StreamAbuse", "Stream abuse check - no signature available"));
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            var activityWindow = GetOrCreateWindow(sig);
            var now = DateTimeOffset.UtcNow;
            var protocol = state.GetSignal<string>(SignalKeys.TransportProtocol) ?? "http";
            var path = state.HttpContext.Request.Path.Value ?? "/";
            var pathHash = GetPathHash(path);

            // Record current streaming request
            activityWindow.StreamRequests++;
            activityWindow.StreamEndpoints.Add(pathHash);

            if (protocol == "websocket")
                activityWindow.WsUpgrades.Add(now);

            var isSseReconnect = state.GetSignal<bool?>(SignalKeys.TransportSseReconnect) ?? false;
            if (isSseReconnect)
                activityWindow.SseReconnects.Add(now);

            // Prune timestamps outside window
            PruneTimestamps(activityWindow.WsUpgrades, now, TimeSpan.FromSeconds(HandshakeStormWindowSeconds));
            PruneTimestamps(activityWindow.SseReconnects, now, TimeSpan.FromSeconds(SseReconnectWindowSeconds));
            WriteWindowSignals(state, activityWindow);

            // Check each abuse pattern
            CheckHandshakeStorm(activityWindow, contributions);
            CheckSseReconnectRate(activityWindow, contributions);
            CheckConcurrentStreams(activityWindow, contributions);
            CheckCrossEndpointMixing(state, activityWindow, contributions);

            if (contributions.Count == 0)
                contributions.Add(NeutralContribution("StreamAbuse", "Stream abuse check - normal streaming activity"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in stream abuse analysis");
            contributions.Add(NeutralContribution("StreamAbuse", "Stream abuse analysis error"));
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private void CheckHandshakeStorm(StreamActivityWindow window, List<DetectionContribution> contributions)
    {
        if (window.WsUpgrades.Count >= HandshakeStormThreshold)
        {
            contributions.Add(BotContribution(
                "StreamAbuse",
                $"WebSocket handshake storm: {window.WsUpgrades.Count} upgrades in {HandshakeStormWindowSeconds}s window",
                confidenceOverride: HandshakeStormConfidence,
                weightMultiplier: HandshakeStormWeight,
                botType: BotType.MaliciousBot.ToString()));
        }
    }

    private void CheckSseReconnectRate(StreamActivityWindow window, List<DetectionContribution> contributions)
    {
        if (window.SseReconnects.Count >= SseReconnectRateThreshold)
        {
            var reconnectRate = ComputeReconnectRate(window);
            contributions.Add(BotContribution(
                "StreamAbuse",
                $"SSE reconnect abuse: {window.SseReconnects.Count} reconnects in {SseReconnectWindowSeconds}s window ({reconnectRate:F1}/min)",
                confidenceOverride: SseReconnectConfidence,
                weightMultiplier: SseReconnectWeight,
                botType: BotType.MaliciousBot.ToString()));
        }
    }

    private void CheckConcurrentStreams(StreamActivityWindow window, List<DetectionContribution> contributions)
    {
        if (window.StreamEndpoints.Count >= ConcurrentStreamsThreshold)
        {
            contributions.Add(BotContribution(
                "StreamAbuse",
                $"Streaming to {window.StreamEndpoints.Count} distinct endpoints (probing for open streams)",
                confidenceOverride: ConcurrentStreamsConfidence,
                weightMultiplier: ConcurrentStreamsWeight,
                botType: BotType.Scraper.ToString()));
        }
    }

    private void CheckCrossEndpointMixing(BlackboardState state, StreamActivityWindow window,
        List<DetectionContribution> contributions)
    {
        // Only flag when BOTH stream traffic AND scrape-pattern page traffic exist
        if (window.StreamRequests < CrossEndpointMinStreamRequests)
            return;
        if (window.PageRequests < CrossEndpointMinPageRequests)
            return;

        var total = window.PageRequests + window.AssetRequests + window.StreamRequests;
        if (total == 0) return;

        var assetRatio = (double)window.AssetRequests / total;
        if (assetRatio >= CrossEndpointMaxAssetRatio)
            return; // Has enough assets - looks like normal browsing

        state.WriteSignal(SignalKeys.StreamCrossEndpointMixing, true);
        contributions.Add(BotContribution(
            "StreamAbuse",
            $"Cross-endpoint mixing: {window.StreamRequests} stream + {window.PageRequests} page requests with low asset ratio ({assetRatio:P0}) - scraping behind streaming cover",
            confidenceOverride: CrossEndpointConfidence,
            weightMultiplier: CrossEndpointWeight,
            botType: BotType.Scraper.ToString()));
    }

    private void RecordNonStreamingRequest(BlackboardState state, StreamActivityWindow window)
    {
        var path = state.HttpContext.Request.Path.Value ?? "/";
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        // Classify as page or asset based on extension
        if (ext is ".js" or ".css" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico"
            or ".woff" or ".woff2" or ".ttf" or ".eot" or ".webp" or ".avif")
            window.AssetRequests++;
        else if (string.IsNullOrEmpty(ext) || ext is ".html" or ".htm")
            window.PageRequests++;
    }

    private void WriteWindowSignals(BlackboardState state, StreamActivityWindow window)
    {
        if (window.WsUpgrades.Count >= HandshakeStormThreshold)
            state.WriteSignal(SignalKeys.StreamHandshakeStorm, true);

        if (window.StreamEndpoints.Count > 1)
            state.WriteSignal(SignalKeys.StreamConcurrentStreams, window.StreamEndpoints.Count);

        var reconnectRate = ComputeReconnectRate(window);
        if (reconnectRate > 0)
            state.WriteSignal(SignalKeys.StreamReconnectRate, reconnectRate);
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
        if (window.SseReconnects.Count == 0 || SseReconnectWindowSeconds <= 0)
            return 0.0;

        return window.SseReconnects.Count * 60.0 / SseReconnectWindowSeconds;
    }

    private static string GetPathHash(string path)
    {
        // Use a simple hash for path deduplication - we only need uniqueness, not reversibility
        var bytes = System.Text.Encoding.UTF8.GetBytes(path);
        return System.IO.Hashing.XxHash32.HashToUInt32(bytes).ToString("X8");
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

using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Hot-path benchmarks for the metastable fingerprint identity layer added by the
///     composite-browser-mode series (commits dbd7a5e4 → 232bc462). Covers the four
///     per-request operations that run for every visitor when Identity is enabled:
///
///     1. WeightedCosine on the layout-dim vector — fires once for L1 confirm and up to
///        TopK times during L2 candidate evaluation in FingerprintMatchContributor.
///     2. IdentityVectorEncoder.Encode — composes the request vector from the raw-values
///        dict, runs once per request when no upstream contributor cached it.
///     3. BrowserModePredicate.Matches + BrowserModeRegistry.Classify — the per-request
///        mode-id selection that gates per-mode absorb.
///     4. Cache-hit path on SqliteFingerprintBrowserModeStore — the expected steady-state
///        for the per-mode absorb write.
///
///     Run: dotnet run --project Mostlylucid.BotDetection.Benchmarks -c Release -- --filter *IdentityHotPath*
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class IdentityHotPathBenchmarks
{
    private IdentityVectorEncoder _encoder = null!;
    private IdentityVectorLayout _layout = null!;
    private Dictionary<string, object?> _chromeRawValues = null!;
    private Dictionary<string, object?> _xhrRawValues = null!;
    private float[] _vectorA = null!;
    private float[] _vectorB = null!;
    private float[] _weights = null!;
    private float[][] _l2Candidates = null!;
    private BrowserModeRegistry _registry = null!;
    private BrowserModePredicate _navigationPredicate = null!;
    private BrowserModePredicate _xhrPredicate = null!;
    private IRequestProbe _navigationProbe = null!;
    private IRequestProbe _xhrProbe = null!;
    private EncoderResultCache _cache = null!;
    private const string PrimarySigForCache = "bench-primary-sig";

    [GlobalSetup]
    public void Setup()
    {
        _layout = IdentityVectorLayout.DefaultV1();
        _encoder = new IdentityVectorEncoder(_layout);

        _chromeRawValues = BuildChromeNavigationRaw();
        _xhrRawValues = BuildChromeXhrRaw();

        _vectorA = _encoder.Encode(_chromeRawValues);
        _vectorB = _encoder.Encode(_xhrRawValues);

        // Uniform weights — represents the steady-state matched fingerprint after weight
        // calibration has converged. Variance in weights changes the per-element work
        // negligibly (one extra mul) so this is the representative case.
        _weights = new float[_layout.Dimension];
        for (var i = 0; i < _weights.Length; i++) _weights[i] = 1.0f;

        // Build TopK=5 candidate centroids by jittering vectorA so they're distinct but
        // share the same dimension. Mirrors L2 SearchAsync returning 5 neighbours.
        var rng = new Random(42);
        _l2Candidates = new float[5][];
        for (var k = 0; k < 5; k++)
        {
            var v = new float[_layout.Dimension];
            for (var i = 0; i < v.Length; i++)
                v[i] = _vectorA[i] + (float)((rng.NextDouble() - 0.5) * 0.1);
            _l2Candidates[k] = v;
        }

        _registry = new BrowserModeRegistry(NullLogger<BrowserModeRegistry>.Instance, "unknown");

        // Pull two representative predicates directly from the registry so the benchmark
        // measures the same Compile() output the runtime uses.
        var navigation = _registry.All.FirstOrDefault(m => m.ModeId == "navigation")
                         ?? throw new InvalidOperationException("navigation mode missing");
        var xhr = _registry.All.FirstOrDefault(m => m.ModeId == "xhr")
                  ?? throw new InvalidOperationException("xhr mode missing");
        _navigationPredicate = navigation.Predicate;
        _xhrPredicate = xhr.Predicate;

        _navigationProbe = new StaticProbe("GET", "/", new Dictionary<string, string>
        {
            ["Sec-Fetch-Dest"] = "document",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Site"] = "none",
            ["Upgrade-Insecure-Requests"] = "1",
        });
        _xhrProbe = new StaticProbe("GET", "/api/users", new Dictionary<string, string>
        {
            ["Sec-Fetch-Dest"] = "empty",
            ["Sec-Fetch-Mode"] = "cors",
            ["Sec-Fetch-Site"] = "same-origin",
            ["X-Requested-With"] = "XMLHttpRequest",
        });

        _cache = new EncoderResultCache(Options.Create(new BotDetectionOptions
        {
            Identity = new IdentityOptions
            {
                Enabled = true,
                Vector = new IdentityVectorOptions
                {
                    EncoderCacheEnabled = true,
                    EncoderCacheTtlMs = 60_000,
                    EncoderCacheMaxEntries = 16,
                }
            }
        }));
        _cache.Set(PrimarySigForCache, _vectorA);
    }

    [Benchmark(Description = "WeightedCosine L1 confirm (1 call, layout-dim)")]
    public double WeightedCosine_L1Confirm()
        => BruteForceIdentityAnchorIndex.WeightedCosine(_vectorA, _vectorB, _weights);

    [Benchmark(Description = "WeightedCosine L2 walk (TopK=5)")]
    public double WeightedCosine_L2Walk()
    {
        double best = double.NegativeInfinity;
        for (var k = 0; k < _l2Candidates.Length; k++)
        {
            var s = BruteForceIdentityAnchorIndex.WeightedCosine(_vectorA, _l2Candidates[k], _weights);
            if (s > best) best = s;
        }
        return best;
    }

    [Benchmark(Description = "Encode Chrome navigation (full layout)")]
    public float[] Encode_Navigation() => _encoder.Encode(_chromeRawValues);

    [Benchmark(Description = "Encode Chrome XHR (full layout)")]
    public float[] Encode_Xhr() => _encoder.Encode(_xhrRawValues);

    [Benchmark(Description = "EncoderResultCache hit (sub-resource amortisation)")]
    public float[] Cache_Hit()
    {
        _cache.TryGet(PrimarySigForCache, out var v);
        return v!;
    }

    [Benchmark(Description = "EncoderResultCache miss + populate")]
    public void Cache_MissPopulate()
    {
        _cache.TryGet("missing-sig", out _);
        _cache.Set("missing-sig-populate", _vectorA);
    }

    [Benchmark(Description = "BrowserModePredicate.Matches navigation (hit)")]
    public bool Predicate_NavigationHit()
        => _navigationPredicate.Matches(_chromeRawValues, _navigationProbe);

    [Benchmark(Description = "BrowserModePredicate.Matches xhr (hit)")]
    public bool Predicate_XhrHit()
        => _xhrPredicate.Matches(_xhrRawValues, _xhrProbe);

    [Benchmark(Description = "BrowserModeRegistry.Classify (navigation request, full walk)")]
    public string Registry_ClassifyNavigation()
        => _registry.Classify(_chromeRawValues, _navigationProbe);

    [Benchmark(Description = "BrowserModeRegistry.Classify (xhr request, full walk)")]
    public string Registry_ClassifyXhr()
        => _registry.Classify(_xhrRawValues, _xhrProbe);

    /// <summary>
    ///     Chrome on a top-level navigation request. The raw-values keys mirror what
    ///     IdentityVectorContributor publishes per CLAUDE.md's foundation contract.
    /// </summary>
    private static Dictionary<string, object?> BuildChromeNavigationRaw() => new()
    {
        ["network.asn"] = "AS15169",
        ["network.ip_subnet"] = "203.0.113.0/24",
        ["network.country"] = "GB",
        ["network.region"] = "London",
        ["network.city"] = "London",
        ["network.is_datacenter"] = false,
        ["network.is_vpn"] = false,
        ["network.is_tor"] = false,
        ["locale.accept_language_primary"] = "en-GB",
        ["locale.accept_language_count"] = 2,
        ["locale.save_data"] = false,
        ["hdr.accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
        ["hdr.accept_encoding_ordered"] = "gzip, deflate, br",
        ["hdr.sec_ch_ua_brands_ordered"] = "\"Chromium\";v=\"127\", \"Not(A:Brand\";v=\"24\"",
        ["hdr.sec_ch_ua_mobile"] = false,
        ["hdr.sec_ch_ua_platform"] = "macOS",
        ["hdr.sec_fetch_pattern"] = 0b1110, // document + none + navigate
        ["hdr.header_order_hash"] = "9af1c2d8",
        ["hdr.header_case_pattern"] = "title",
        ["hdr.upgrade_insecure_requests"] = true,
        ["hdr.dnt"] = false,
        ["hdr.sec_gpc"] = false,
        ["hdr.priority"] = true,
        ["hdr.cache_control_pragma"] = "no-cache",
        ["hdr.te"] = "trailers",
        ["hdr.connection_pattern"] = "keep-alive",
        ["transport.tls_ja4"] = "t13d1517h2_8daaf6152771_b1ff8ab2d16f",
        ["transport.h2_settings_hash"] = "akamai-fingerprint-1",
        ["transport.alpn"] = "h2",
        ["transport.tcp_p0f"] = "macOS 14",
        ["session.cookie_count"] = 5,
        ["session.has_returning_cookie"] = true,
        ["session.entry_page_family"] = "/",
        ["session.referer_host_family"] = "stylobot.net",
        ["session.request_rate"] = 0.5,
        ["session.session_age"] = 120,
        ["session.method_pattern"] = "GET",
        ["session.path_entropy"] = 0.3,
        ["hdr.ua_family"] = "Chrome",
    };

    private static Dictionary<string, object?> BuildChromeXhrRaw() => new()
    {
        ["network.asn"] = "AS15169",
        ["network.ip_subnet"] = "203.0.113.0/24",
        ["network.country"] = "GB",
        ["network.region"] = "London",
        ["network.city"] = "London",
        ["network.is_datacenter"] = false,
        ["network.is_vpn"] = false,
        ["network.is_tor"] = false,
        ["locale.accept_language_primary"] = "en-GB",
        ["locale.accept_language_count"] = 2,
        ["locale.save_data"] = false,
        ["hdr.accept"] = "application/json, text/plain, */*",
        ["hdr.accept_encoding_ordered"] = "gzip, deflate, br",
        ["hdr.sec_ch_ua_brands_ordered"] = "\"Chromium\";v=\"127\", \"Not(A:Brand\";v=\"24\"",
        ["hdr.sec_ch_ua_mobile"] = false,
        ["hdr.sec_ch_ua_platform"] = "macOS",
        ["hdr.sec_fetch_pattern"] = 0b0101, // empty + cors + same-origin
        ["hdr.header_order_hash"] = "9af1c2d8",
        ["hdr.header_case_pattern"] = "title",
        ["hdr.upgrade_insecure_requests"] = false,
        ["hdr.dnt"] = false,
        ["hdr.sec_gpc"] = false,
        ["hdr.priority"] = true,
        ["hdr.cache_control_pragma"] = "no-cache",
        ["hdr.te"] = "trailers",
        ["hdr.connection_pattern"] = "keep-alive",
        ["tool.x_requested_with"] = true,
        ["transport.tls_ja4"] = "t13d1517h2_8daaf6152771_b1ff8ab2d16f",
        ["transport.h2_settings_hash"] = "akamai-fingerprint-1",
        ["transport.alpn"] = "h2",
        ["transport.tcp_p0f"] = "macOS 14",
        ["session.cookie_count"] = 5,
        ["session.has_returning_cookie"] = true,
        ["session.entry_page_family"] = "/api",
        ["session.referer_host_family"] = "stylobot.net",
        ["session.request_rate"] = 2.1,
        ["session.session_age"] = 130,
        ["session.method_pattern"] = "GET",
        ["session.path_entropy"] = 0.45,
        ["hdr.ua_family"] = "Chrome",
    };

    private sealed class StaticProbe : IRequestProbe
    {
        private readonly Dictionary<string, string> _headers;
        public StaticProbe(string method, string path, Dictionary<string, string> headers)
        {
            Method = method;
            Path = path;
            _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        }
        public string Method { get; }
        public string Path { get; }
        public bool HasHeader(string name) => _headers.ContainsKey(name);
        public string HeaderOrDefault(string name, string fallback)
            => _headers.TryGetValue(name, out var v) ? v : fallback;
    }
}
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ExtractorAtom (per Taxonomy.md) that composes the per-request identity
///     feature vector from sink hints + HttpContext-derived raw headers, then
///     hands the vector off for downstream fingerprint matching.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>IdentityVectorContributor</c>. Priority 5 -- foundation wave,
///         must land before FingerprintMatch reads it.
///     </para>
///     <para>
///         <b>Atom-holds-rich-state.</b> The encoded vector, raw vector, and
///         raw values dictionary are large per-request structures unsuited to
///         sink storage. They live on <see cref="HttpContext.Items"/> under
///         <see cref="VectorKey"/> / <see cref="RawVectorKey"/> /
///         <see cref="RawValuesKey"/>, following the pattern
///         <see cref="SignatureAtom"/> established. The sink still learns the
///         numeric quality score and IP subnet -- values, not vectors.
///     </para>
///     <para>
///         Cross-request amortisation via <see cref="EncoderResultCache"/>
///         (keyed on primary signature, short TTL) preserves the 30-asset
///         page-load optimisation the contributor introduced. On cache hit
///         we intentionally skip re-composing the raw values dictionary --
///         downstream atoms self-recompose when needed to avoid stale
///         path/method carryover.
///     </para>
/// </remarks>
public sealed class IdentityVectorAtom : DetectorAtomBase
{
    /// <summary>Key on <see cref="HttpContext.Items"/> for the encoded identity vector (float[]).</summary>
    public const string VectorKey = "stylobot.identity.vector";

    /// <summary>Key on <see cref="HttpContext.Items"/> for the raw-domain vector (float[]).</summary>
    public const string RawVectorKey = "stylobot.identity.vector_raw";

    /// <summary>Key on <see cref="HttpContext.Items"/> for the raw values dictionary (Dictionary&lt;string, object?&gt;).</summary>
    public const string RawValuesKey = "stylobot.identity.raw_values";

    private readonly ILogger<IdentityVectorAtom> _logger;
    private readonly IdentityVectorEncoder _encoder;
    private readonly EncoderResultCache _cache;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly bool _enabled;

    public IdentityVectorAtom(
        ILogger<IdentityVectorAtom> logger,
        IdentityVectorEncoder encoder,
        EncoderResultCache cache,
        IHttpContextAccessor httpContextAccessor,
        IOptions<BotDetectionOptions> options)
        : base(name: "IdentityVector", category: "Identity")
    {
        _logger = logger;
        _encoder = encoder;
        _cache = cache;
        _httpContextAccessor = httpContextAccessor;
        _enabled = options.Value.Identity.Enabled;
    }

    public override int Priority => 5;
    public override bool IsEnabled => _enabled;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var primarySig = sink.ReadHint(SignalKeys.PrimarySignature) ?? string.Empty;

        // Cache hit: reuse the encoded vector, skip raw-values composition.
        if (_cache.TryGet(primarySig, out var cached) && cached is not null)
        {
            context.Items[VectorKey] = cached;
            var cachedPresenceSlot = _encoder.Layout.FindSlot("quality.dimension_presence_ratio");
            if (cachedPresenceSlot is not null)
            {
                var q = (double)cached[cachedPresenceSlot.Offset];
                sink.Raise(
                    $"{SignalKeys.IdentityVectorQuality}:{q.ToString("F4", CultureInfo.InvariantCulture)}",
                    sessionId);
            }
            return Task.FromResult(None());
        }

        var raw = ComposeRawValues(context, sink);
        var vector = _encoder.Encode(raw);
        var rawVector = _encoder.EncodeRaw(raw);

        context.Items[VectorKey] = vector;
        context.Items[RawVectorKey] = rawVector;
        context.Items[RawValuesKey] = raw;

        var presenceSlot = _encoder.Layout.FindSlot("quality.dimension_presence_ratio");
        if (presenceSlot is not null)
        {
            var q = (double)vector[presenceSlot.Offset];
            sink.Raise(
                $"{SignalKeys.IdentityVectorQuality}:{q.ToString("F4", CultureInfo.InvariantCulture)}",
                sessionId);
        }

        if (!string.IsNullOrEmpty(primarySig))
            _cache.Set(primarySig, vector);

        return Task.FromResult(None());
    }

    /// <summary>
    ///     Retrieves the encoded identity vector this atom wrote for the
    ///     current request. Returns <c>null</c> if the atom did not run
    ///     (disabled) or if HttpContext.Items has been evicted.
    /// </summary>
    public static float[]? TryGetVector(HttpContext context)
        => context.Items.TryGetValue(VectorKey, out var v) && v is float[] arr ? arr : null;

    /// <summary>Retrieves the raw-domain vector written by this atom.</summary>
    public static float[]? TryGetRawVector(HttpContext context)
        => context.Items.TryGetValue(RawVectorKey, out var v) && v is float[] arr ? arr : null;

    /// <summary>Retrieves the raw values dictionary written by this atom.</summary>
    public static IReadOnlyDictionary<string, object?>? TryGetRawValues(HttpContext context)
        => context.Items.TryGetValue(RawValuesKey, out var v) && v is IReadOnlyDictionary<string, object?> dict
            ? dict
            : null;

    /// <summary>
    ///     Pull every dimension's raw input from either an upstream sink hint
    ///     (preferred) or directly from the request when no hint exists yet.
    ///     Anything missing stays absent and the encoder leaves its slot at 0.
    /// </summary>
    internal static Dictionary<string, object?> ComposeRawValues(HttpContext ctx, SignalSink sink)
    {
        var headers = ctx.Request.Headers;
        var values = new Dictionary<string, object?>(capacity: 64, StringComparer.OrdinalIgnoreCase);

        // Network -- from sink hints, IP subnet computed inline from the
        // connection address since no upstream atom produces it.
        values["network.asn"] = sink.ReadHint(SignalKeys.IpAsn);
        var ipSubnet = ComputeIpSubnet(ctx.Connection.RemoteIpAddress);
        values["network.ip_subnet"] = ipSubnet;
        // Also publish subnet as a top-level hint so the dashboard visitor
        // cache, the behavioural grouper, and future operator analytics can
        // read it without going through the IdentityVector payload.
        if (ipSubnet is not null)
            sink.Raise($"ip.subnet:{ipSubnet}", ctx.TraceIdentifier);

        values["network.country"] = sink.ReadHint(SignalKeys.GeoCountryCode);
        values["network.region"] = sink.ReadHint("geo.region_code");
        values["network.city"] = sink.ReadHint("geo.city");
        values["network.is_datacenter"] = sink.ReadBoolHint(SignalKeys.IpIsDatacenter);
        values["network.is_vpn"] = sink.ReadBoolHint("geo.is_vpn");
        values["network.is_tor"] = sink.ReadBoolHint("geo.is_tor");

        // Locale
        var acceptLanguage = (string?)headers.AcceptLanguage;
        var langPrimary = acceptLanguage?.Split(',', 2)[0].Split(';', 2)[0].Trim();
        values["locale.accept_language_primary"] = langPrimary;
        values["locale.accept_language_count"] = acceptLanguage?.Split(',').Length ?? 0;
        values["locale.save_data"] = headers.ContainsKey("Save-Data");

        // Header bag
        values["hdr.accept"] = (string?)headers.Accept;
        values["hdr.accept_encoding_ordered"] = (string?)headers.AcceptEncoding;
        values["hdr.sec_ch_ua_brands_ordered"] = headers["Sec-CH-UA"].ToString();
        values["hdr.sec_ch_ua_mobile"] = headers["Sec-CH-UA-Mobile"].ToString() == "?1";
        values["hdr.sec_ch_ua_platform"] = headers["Sec-CH-UA-Platform"].ToString();
        values["hdr.sec_ch_ua_arch"] = headers["Sec-CH-UA-Arch"].ToString();
        values["hdr.sec_ch_ua_bitness"] = headers["Sec-CH-UA-Bitness"].ToString();
        values["hdr.sec_ch_ua_model"] = headers["Sec-CH-UA-Model"].ToString();
        values["hdr.sec_ch_ua_full_version"] = headers["Sec-CH-UA-Full-Version-List"].ToString();
        values["hdr.sec_fetch_pattern"] = SecFetchBitmask(headers);

        // Header-hashes rich-state now on HttpContext.Items via SignatureAtom.
        // Serialise to JSON to preserve the encoder's expectation of the same
        // string shape the legacy contributor produced from HeaderHashCollector.
        var headerHashes = SignatureAtom.TryGetHeaderHashes(ctx);
        values["hdr.header_order_hash"] = headerHashes is null || headerHashes.Count == 0
            ? null
            : JsonSerializer.Serialize(
                headerHashes.ToDictionary(kv => kv.Key, kv => kv.Value),
                BotDetectionJsonSerializerContext.Default.DictionaryStringString);

        values["hdr.header_case_pattern"] = HeaderCasePattern(headers);
        values["hdr.upgrade_insecure_requests"] = headers["Upgrade-Insecure-Requests"].ToString() == "1";
        values["hdr.dnt"] = headers.ContainsKey("DNT");
        values["hdr.sec_gpc"] = headers.ContainsKey("Sec-GPC");
        values["hdr.priority"] = headers.ContainsKey("Priority");
        values["hdr.cache_control_pragma"] = $"{headers.CacheControl}|{headers.Pragma}";
        values["hdr.te"] = (string?)headers["TE"];
        values["hdr.connection_pattern"] = (string?)headers.Connection;

        // UA family
        var ua = ctx.Request.Headers.UserAgent.ToString();
        values["hdr.ua_family"] = string.IsNullOrEmpty(ua)
            ? null
            : Mostlylucid.BotDetection.Helpers.UserAgentParser.Parse(ua).Family;

        // HTTP-library tells
        values["tool.x_requested_with"] = headers.ContainsKey("X-Requested-With");
        values["tool.custom_header_signature"] = string.Join("|",
            headers.Keys.Where(k => k.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
                                    && !k.Equals("X-Requested-With", StringComparison.OrdinalIgnoreCase)
                                    && !k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                                    && !k.StartsWith("X-Real-", StringComparison.OrdinalIgnoreCase))
                          .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        // Transport -- only present when TLS terminates here.
        values["transport.tls_ja4"] = sink.ReadHint("tls.ja4");
        values["transport.h2_settings_hash"] = sink.ReadHint("h2.settings_hash");
        values["transport.alpn"] = sink.ReadHint("tls.alpn");
        values["transport.tcp_p0f"] = sink.ReadHint("tcp.p0f");

        // Session / behavioural
        values["session.cookie_count"] = ctx.Request.Cookies.Count;
        values["session.has_returning_cookie"] = ctx.Request.Cookies.Count > 0;
        values["session.entry_page_family"] = sink.ReadHint("session.entry_page_family");
        values["session.referer_host_family"] = RefererHost(headers);
        values["session.request_rate"] = sink.ReadDoubleHint("behavioral.average_rps");
        values["session.session_age"] = sink.ReadDoubleHint("session.age_seconds");
        values["session.method_pattern"] = ctx.Request.Method;
        values["session.path_entropy"] = sink.ReadDoubleHint("behavioral.path_entropy");

        // Quality
        var hasTransport = values["transport.tls_ja4"] is string s && !string.IsNullOrEmpty(s);
        values["quality.transport_quality"] = hasTransport ? 1.0 : 0.0;
        values["quality.cleartext_flag"] = !hasTransport;

        return values;
    }

    private static int SecFetchBitmask(IHeaderDictionary headers)
    {
        var mask = 0;
        if (headers.ContainsKey("Sec-Fetch-Dest")) mask |= 1;
        if (headers.ContainsKey("Sec-Fetch-Mode")) mask |= 2;
        if (headers.ContainsKey("Sec-Fetch-Site")) mask |= 4;
        if (headers.ContainsKey("Sec-Fetch-User")) mask |= 8;
        return mask;
    }

    private static string HeaderCasePattern(IHeaderDictionary headers)
    {
        int title = 0, lower = 0, upper = 0, mixed = 0;
        foreach (var name in headers.Keys)
        {
            var first = name[0];
            var rest = name.Length > 1 ? name[1..] : string.Empty;
            if (char.IsUpper(first) && rest.All(c => char.IsLower(c) || !char.IsLetter(c))) title++;
            else if (name.All(c => char.IsLower(c) || !char.IsLetter(c))) lower++;
            else if (name.All(c => char.IsUpper(c) || !char.IsLetter(c))) upper++;
            else mixed++;
        }
        return $"t{title}l{lower}u{upper}m{mixed}";
    }

    private static string? RefererHost(IHeaderDictionary headers)
    {
        var referer = (string?)headers.Referer;
        if (string.IsNullOrEmpty(referer)) return null;
        return Uri.TryCreate(referer, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static string? ComputeIpSubnet(IPAddress? ip)
    {
        if (ip is null) return null;
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork when bytes.Length >= 3
                => $"{bytes[0]}.{bytes[1]}.{bytes[2]}",
            AddressFamily.InterNetworkV6 when bytes.Length >= 6
                => $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}",
            _ => null
        };
    }
}

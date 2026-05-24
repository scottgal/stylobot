using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Foundation Match-step contributor that composes the per-request identity feature vector
///     from signals already on the blackboard plus raw headers it pulls directly. Writes
///     <see cref="SignalKeys.IdentityVector"/> and <see cref="SignalKeys.IdentityVectorQuality"/>
///     so <see cref="FingerprintMatchContributor"/> (Priority just after this) can match against
///     the fingerprint store.
///
///     Returns no contributions; this is pure identity context, not a verdict signal.
///     Dormant unless <c>BotDetectionOptions.Identity.Enabled</c> is true.
///
///     See docs/architecture/fingerprint-match.md.
/// </summary>
public sealed class IdentityVectorContributor : ContributingDetectorBase, IFoundationContributor
{
    private readonly ILogger<IdentityVectorContributor> _logger;
    private readonly IdentityVectorEncoder _encoder;
    private readonly bool _enabled;

    public IdentityVectorContributor(
        ILogger<IdentityVectorContributor> logger,
        IdentityVectorEncoder encoder,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _encoder = encoder;
        _enabled = options.Value.Identity.Enabled;
    }

    public override string Name => "IdentityVector";
    public override int Priority => 5;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();
    public override bool IsEnabled => _enabled;

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var raw = ComposeRawValues(state);
        var vector = _encoder.Encode(raw);
        state.WriteSignal(SignalKeys.IdentityVector, vector);

        var presenceSlot = _encoder.Layout.FindSlot("quality.dimension_presence_ratio");
        if (presenceSlot is not null)
            state.WriteSignal(SignalKeys.IdentityVectorQuality, (double)vector[presenceSlot.Offset]);

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());
    }

    /// <summary>
    ///     Pull every dimension's raw input from either an upstream signal (preferred) or directly
    ///     from the request when no signal exists yet. Anything missing stays absent and the encoder
    ///     leaves its slot at 0. Internal so FingerprintMatchContributor can self-compute the
    ///     vector as a fallback when the orchestrator schedules it in the same wave and the
    ///     signal isn't there yet (avoids a "1/N requests had no identity.fingerprint_id" race).
    /// </summary>
    internal static Dictionary<string, object?> ComposeRawValues(BlackboardState state)
    {
        var ctx = state.HttpContext;
        var headers = ctx.Request.Headers;
        var signals = state.Signals;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Network — taken from existing signals / GeoLocation context where possible.
        // IP subnet computed inline from the connection address since no standalone signal exists.
        values["network.asn"] = SignalString(signals, SignalKeys.IpAsn);
        var ipSubnet = ComputeIpSubnet(ctx.Connection.RemoteIpAddress);
        values["network.ip_subnet"] = ipSubnet;
        // Also publish as a top-level signal so consumers (dashboard
        // visitor cache, the behavioural grouper's subnet-rotation tier,
        // any future operator analytics) can read it without going
        // through the IdentityVector payload.
        if (ipSubnet is not null) state.WriteSignal("ip.subnet", ipSubnet);
        values["network.country"] = SignalString(signals, SignalKeys.GeoCountryCode);
        values["network.region"] = SignalString(signals, "geo.region_code");
        values["network.city"] = SignalString(signals, "geo.city");
        values["network.is_datacenter"] = SignalBool(signals, SignalKeys.IpIsDatacenter);
        values["network.is_vpn"] = SignalBool(signals, "geo.is_vpn");
        values["network.is_tor"] = SignalBool(signals, "geo.is_tor");

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
        values["hdr.header_order_hash"] = SignalString(signals, SignalKeys.HeaderHashes);
        values["hdr.header_case_pattern"] = HeaderCasePattern(headers);
        values["hdr.upgrade_insecure_requests"] = headers["Upgrade-Insecure-Requests"].ToString() == "1";
        values["hdr.dnt"] = headers.ContainsKey("DNT");
        values["hdr.sec_gpc"] = headers.ContainsKey("Sec-GPC");
        values["hdr.priority"] = headers.ContainsKey("Priority");
        values["hdr.cache_control_pragma"] = $"{headers.CacheControl}|{headers.Pragma}";
        values["hdr.te"] = (string?)headers["TE"];
        values["hdr.connection_pattern"] = (string?)headers.Connection;

        // HTTP-library tells
        values["tool.x_requested_with"] = headers.ContainsKey("X-Requested-With");
        values["tool.custom_header_signature"] = string.Join("|",
            headers.Keys.Where(k => k.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
                                    && !k.Equals("X-Requested-With", StringComparison.OrdinalIgnoreCase)
                                    && !k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                                    && !k.StartsWith("X-Real-", StringComparison.OrdinalIgnoreCase))
                          .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        // Transport — only present when TLS terminates here. On plaintext these stay null and the
        // quality.cleartext_flag slot records the absence.
        values["transport.tls_ja4"] = SignalString(signals, "tls.ja4");
        values["transport.h2_settings_hash"] = SignalString(signals, "h2.settings_hash");
        values["transport.alpn"] = SignalString(signals, "tls.alpn");
        values["transport.tcp_p0f"] = SignalString(signals, "tcp.p0f");

        // Session / behavioural
        values["session.cookie_count"] = ctx.Request.Cookies.Count;
        values["session.has_returning_cookie"] = ctx.Request.Cookies.Count > 0;
        values["session.entry_page_family"] = SignalString(signals, "session.entry_page_family");
        values["session.referer_host_family"] = RefererHost(headers);
        values["session.request_rate"] = SignalDouble(signals, "behavioral.average_rps");
        values["session.session_age"] = SignalDouble(signals, "session.age_seconds");
        values["session.method_pattern"] = ctx.Request.Method;
        values["session.path_entropy"] = SignalDouble(signals, "behavioral.path_entropy");

        // Quality
        var hasTransport = values["transport.tls_ja4"] is string s && !string.IsNullOrEmpty(s);
        values["quality.transport_quality"] = hasTransport ? 1.0 : 0.0;
        values["quality.cleartext_flag"] = !hasTransport;

        return values;
    }

    private static string? SignalString(IReadOnlyDictionary<string, object> signals, string key)
        => signals.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static bool SignalBool(IReadOnlyDictionary<string, object> signals, string key)
        => signals.TryGetValue(key, out var v) && v is bool b && b;

    private static double SignalDouble(IReadOnlyDictionary<string, object> signals, string key)
        => signals.TryGetValue(key, out var v) && v is IConvertible c
            ? c.ToDouble(System.Globalization.CultureInfo.InvariantCulture)
            : 0.0;

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
        // A coarse fingerprint of how the client cases its header names. Different libraries
        // canonicalise differently; this catches Title-Case vs lower-case vs UPPERCASE.
        int title = 0, lower = 0, upper = 0, mixed = 0;
        foreach (var name in headers.Keys)
        {
            var first = name[0];
            var rest = name.Length > 1 ? name[1..] : "";
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

    private static string? ComputeIpSubnet(System.Net.IPAddress? ip)
    {
        if (ip is null) return null;
        var bytes = ip.GetAddressBytes();
        // /24 for v4, /48 for v6 — coarse enough to survive within-ISP rotation, fine enough to discriminate.
        return ip.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork when bytes.Length >= 3
                => $"{bytes[0]}.{bytes[1]}.{bytes[2]}",
            System.Net.Sockets.AddressFamily.InterNetworkV6 when bytes.Length >= 6
                => $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}",
            _ => null
        };
    }
}

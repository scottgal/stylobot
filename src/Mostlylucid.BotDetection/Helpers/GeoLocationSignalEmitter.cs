using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Emits the base geographic + network-anonymizer signals (<c>geo.country_code</c>,
///     <c>geo.country_name</c>, <c>geo.is_vpn</c>, <c>geo.is_proxy</c>, <c>geo.is_tor</c>,
///     <c>geo.is_hosting</c>) from the <c>GeoLocation</c> object that
///     <c>GeoRoutingMiddleware</c> stores on <see cref="HttpContext.Items"/>["GeoLocation"].
///     <para>
///     These signals are consumed by <c>YarpExtensions</c> (network-flag header),
///     <c>BotTypeFilter</c>, <c>UrlSignalProjection</c>, <c>SignatureCoordinator</c>, and
///     <c>GeoChangeAtom</c> (whose <c>RequiredSignals</c> includes <c>geo.country_code</c> --
///     i.e. it never runs until this emits). The v8 atom refactor removed the geo
///     <c>IContributingDetector</c> that used to write these; nothing replaced its emit,
///     so every consumer read <c>false</c>/absent. This restores the emit on the sink,
///     mirroring how <c>IpAtom</c> raises <c>ip.is_datacenter:true|false</c> (a colon-
///     suffixed key that <c>SignalSink.ReadBoolHint</c> / the ledger→evidence projection
///     parse back to a boolean).
///     </para>
///     <para>
///     Core takes no compile reference on <c>Mostlylucid.GeoDetection</c>, so the
///     <c>GeoLocation</c> model is read reflectively (duck-typed) -- the same pattern
///     <c>MultiFactorSignatureService.ExtractCountryCode</c> and
///     <c>DetectionBroadcastMiddleware.GetCountryCodeAccessor</c> already use. When the geo
///     middleware isn't loaded, <c>Items["GeoLocation"]</c> is absent and this is a no-op.
///     </para>
/// </summary>
internal static class GeoLocationSignalEmitter
{
    private const string GeoLocationItemKey = "GeoLocation";

    /// <summary>
    ///     Cached per-type property accessors so the reflective lookup happens once per
    ///     concrete <c>GeoLocation</c> type, not per request. <c>PropertyInfo.GetValue</c>
    ///     is used directly (no <c>Expression.Compile</c>) so the path is AOT-safe without
    ///     the interpreter fallback; the enclosing method is marked
    ///     <see cref="RequiresUnreferencedCodeAttribute"/> to opt the reflective read out
    ///     of trim analysis, matching the existing GeoLocation duck-typing sites.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, GeoAccessors> AccessorCache = new();

    private sealed record GeoAccessors(
        PropertyInfo? CountryCode,
        PropertyInfo? CountryName,
        PropertyInfo? IsVpn,
        PropertyInfo? IsProxy,
        PropertyInfo? IsTor,
        PropertyInfo? IsHosting);

    /// <summary>
    ///     Reads <c>Items["GeoLocation"]</c> and raises the geo.* signals onto the sink.
    ///     Absent/empty values are not raised (mirrors <c>IpAtom</c>'s conditional emits);
    ///     the boolean anonymizer flags are always raised as <c>:true</c>/<c>:false</c> when
    ///     the geo object is present so downstream consumers get an explicit value.
    /// </summary>
    [RequiresUnreferencedCode(
        "Duck-types HttpContext.Items[\"GeoLocation\"] via reflection to read the GeoLocation " +
        "model without a hard reference on Mostlylucid.GeoDetection. No-op when the geo " +
        "middleware isn't loaded (the item is absent).")]
    public static void Emit(SignalSink sink, HttpContext context, string sessionId)
    {
        if (!context.Items.TryGetValue(GeoLocationItemKey, out var geo) || geo is null)
            return;

        var acc = GetAccessors(geo.GetType());

        var countryCode = acc.CountryCode?.GetValue(geo) as string;
        if (!string.IsNullOrEmpty(countryCode) && countryCode != "XX")
            sink.Raise($"{SignalKeys.GeoCountryCode}:{countryCode}", sessionId);

        var countryName = acc.CountryName?.GetValue(geo) as string;
        if (!string.IsNullOrEmpty(countryName))
            sink.Raise($"geo.country_name:{countryName}", sessionId);

        RaiseBool(sink, SignalKeys.GeoIsVpn, acc.IsVpn, geo, sessionId);
        RaiseBool(sink, SignalKeys.GeoIsProxy, acc.IsProxy, geo, sessionId);
        RaiseBool(sink, SignalKeys.GeoIsTor, acc.IsTor, geo, sessionId);
        RaiseBool(sink, SignalKeys.GeoIsHosting, acc.IsHosting, geo, sessionId);
    }

    private static void RaiseBool(SignalSink sink, string key, PropertyInfo? prop, object geo, string sessionId)
    {
        if (prop?.GetValue(geo) is bool b)
            sink.Raise($"{key}:{(b ? "true" : "false")}", sessionId);
    }

    [RequiresUnreferencedCode("Reflects over the GeoLocation model's public properties.")]
    private static GeoAccessors GetAccessors(Type type)
        => AccessorCache.GetOrAdd(type, static t => new GeoAccessors(
            CountryCode: BoolOrStringProp(t, "CountryCode", typeof(string)),
            CountryName: BoolOrStringProp(t, "CountryName", typeof(string)),
            IsVpn: BoolOrStringProp(t, "IsVpn", typeof(bool)),
            IsProxy: BoolOrStringProp(t, "IsProxy", typeof(bool)),
            IsTor: BoolOrStringProp(t, "IsTor", typeof(bool)),
            IsHosting: BoolOrStringProp(t, "IsHosting", typeof(bool))));

    private static PropertyInfo? BoolOrStringProp(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
        Type type,
        string name,
        Type expected)
    {
        var prop = type.GetProperty(name);
        return prop is not null && prop.CanRead && prop.PropertyType == expected ? prop : null;
    }
}

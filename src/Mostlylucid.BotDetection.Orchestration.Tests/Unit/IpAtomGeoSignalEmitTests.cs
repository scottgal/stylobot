using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Regression guard for the dropped geo emitter. The v8 atom refactor removed the geo
///     <c>IContributingDetector</c> that raised <c>geo.is_vpn</c> / <c>geo.is_proxy</c> /
///     <c>geo.is_tor</c> / <c>geo.is_hosting</c> / <c>geo.country_code</c>; nothing replaced
///     the emit, so every consumer (YarpExtensions network-flag header, BotTypeFilter,
///     UrlSignalProjection, SignatureCoordinator, and GeoChangeAtom's
///     <c>RequiredSignals=[geo.country_code]</c>) read false/absent in production.
///     <para>
///         <see cref="IpAtom"/> now projects those signals from the <c>GeoLocation</c> object
///         <c>GeoRoutingMiddleware</c> stores on <c>HttpContext.Items["GeoLocation"]</c>. Core
///         takes no reference on Mostlylucid.GeoDetection, so the object is duck-typed; this
///         test uses a local double with the same property names, exercising the reflective read.
///     </para>
/// </summary>
public sealed class IpAtomGeoSignalEmitTests
{
    /// <summary>Duck-typed stand-in for Mostlylucid.GeoDetection.Models.GeoLocation.</summary>
    private sealed class GeoLocationDouble
    {
        public string CountryCode { get; init; } = "";
        public string CountryName { get; init; } = "";
        public bool IsVpn { get; init; }
        public bool IsProxy { get; init; }
        public bool IsTor { get; init; }
        public bool IsHosting { get; init; }
    }

    private static IpAtom NewAtom(HttpContext http) => new(
        NullLogger<IpAtom>.Instance,
        new PassthroughConfigProvider(),
        new SingleHttpContextAccessor(http),
        botListDatabase: null,
        asnLookup: null,
        proxyEnvironment: null);

    [Fact]
    public async Task Raises_geo_anonymizer_flags_and_country_from_GeoLocation_context()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7"); // public, non-local
        http.Items["GeoLocation"] = new GeoLocationDouble
        {
            CountryCode = "US",
            CountryName = "United States",
            IsVpn = true,
            IsProxy = true,
            IsTor = false,
            IsHosting = true
        };

        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        await NewAtom(http).DetectAsync(sink, sessionId: "geo-emit");

        // The four anonymizer flags the audit found defined + read but never emitted.
        Assert.True(sink.ReadBoolHint(SignalKeys.GeoIsVpn, fallback: false),
            "geo.is_vpn must be emitted from GeoLocation.IsVpn");
        Assert.True(sink.ReadBoolHint(SignalKeys.GeoIsProxy, fallback: false),
            "geo.is_proxy must be emitted from GeoLocation.IsProxy");
        Assert.True(sink.ReadBoolHint(SignalKeys.GeoIsHosting, fallback: false),
            "geo.is_hosting must be emitted from GeoLocation.IsHosting");
        // Explicit false, sourced from the provider value (not a hardcoded default):
        // fallback:true proves an explicit "false" landed on the sink.
        Assert.False(sink.ReadBoolHint(SignalKeys.GeoIsTor, fallback: true),
            "geo.is_tor must reflect the provider value (false here)");

        // country_code un-defers GeoChangeAtom (RequiredSignals = [geo.country_code]).
        Assert.Equal("US", sink.ReadHint(SignalKeys.GeoCountryCode));
    }

    [Fact]
    public async Task Emits_no_geo_flags_when_no_GeoLocation_in_context()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        // No Items["GeoLocation"] -> geo middleware not loaded -> emitter is a no-op.

        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        await NewAtom(http).DetectAsync(sink, sessionId: "geo-absent");

        Assert.False(sink.ReadBoolHint(SignalKeys.GeoIsVpn, fallback: false));
        Assert.Null(sink.ReadHint(SignalKeys.GeoCountryCode));
    }

    /// <summary>Returns the caller-supplied default for every lookup (code-default behaviour).</summary>
    private sealed class PassthroughConfigProvider : IDetectorConfigProvider
    {
        public DetectorManifest? GetManifest(string detectorName) => null;
        public DetectorDefaults GetDefaults(string detectorName) => new();
        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue) => defaultValue;

        public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
            ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
            => Task.FromResult(defaultValue);

        public void InvalidateCache(string? detectorName = null) { }

        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }

    private sealed class SingleHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
}

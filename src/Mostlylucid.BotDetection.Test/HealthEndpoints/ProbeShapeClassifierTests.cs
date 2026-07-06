using Mostlylucid.BotDetection.HealthEndpoints;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.HealthEndpoints;

/// <summary>
///     Unit tests for <see cref="ProbeShapeClassifier.IsProbeShape"/>.
///
///     The classifier returns true only when:
///     - The User-Agent contains a recognised probe family (case-insensitive), AND
///     - The request does NOT carry browser-navigation shape (Sec-Fetch-Mode != "navigate").
///
///     This ensures that shape confirmation requires a POSITIVE probe-UA match,
///     not merely the absence of browser signals.
/// </summary>
public sealed class ProbeShapeClassifierTests
{
    private static readonly IReadOnlyList<string> DefaultUas =
        HealthEndpointOptions.DefaultProbeUserAgents;

    // ---- probe-UA positive matches ----

    [Theory]
    [InlineData("kube-probe/1.28")]
    [InlineData("Go-http-client/2.0")]
    [InlineData("Go-http-client/1.1")]
    [InlineData("curl/8.5.0")]
    [InlineData("curl/7.88.1")]
    [InlineData("Wget/1.21")]
    [InlineData("Docker/1.0 check")]
    [InlineData("KUBE-PROBE/1.28")]         // case-insensitive
    [InlineData("CURL/8.0")]                // case-insensitive
    public void Probe_UA_without_browser_navigation_returns_true(string ua)
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = ua,
        };
        Assert.True(ProbeShapeClassifier.IsProbeShape(signals, sink: null, DefaultUas));
    }

    // ---- browser-navigation shape guard ----

    [Theory]
    [InlineData("curl/8.5.0", "navigate")]           // probe UA but browser navigation -> false
    [InlineData("kube-probe/1.28", "navigate")]      // probe UA but browser navigation -> false
    [InlineData("Mozilla/5.0 Chrome/120", "navigate")]
    [InlineData("Mozilla/5.0 Safari/17", "navigate")]
    public void SecFetchMode_navigate_returns_false_regardless_of_ua(string ua, string secFetchMode)
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = ua,
            [SignalKeys.HeaderSecFetchMode] = secFetchMode,
        };
        Assert.False(ProbeShapeClassifier.IsProbeShape(signals, sink: null, DefaultUas));
    }

    // ---- non-probe UAs without browser signals ----

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36")]
    [InlineData("python-requests/2.28.0")]
    [InlineData("Prometheus/2.45")]
    [InlineData("")]
    public void Non_probe_UA_without_browser_navigation_returns_false(string ua)
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = ua,
        };
        Assert.False(ProbeShapeClassifier.IsProbeShape(signals, sink: null, DefaultUas));
    }

    // ---- non-navigate Sec-Fetch-Mode values still allow probe match ----

    [Theory]
    [InlineData("cors")]
    [InlineData("no-cors")]
    [InlineData("same-origin")]
    [InlineData("")]
    public void Non_navigate_sec_fetch_mode_does_not_block_probe_ua(string secFetchMode)
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = "curl/8.5.0",
            [SignalKeys.HeaderSecFetchMode] = secFetchMode,
        };
        Assert.True(ProbeShapeClassifier.IsProbeShape(signals, sink: null, DefaultUas));
    }

    // ---- empty / null UA family list ----

    [Fact]
    public void Empty_probe_ua_list_returns_false_for_any_ua()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = "curl/8.5.0",
        };
        Assert.False(ProbeShapeClassifier.IsProbeShape(signals, sink: null, probeUserAgents: []));
    }

    // ---- custom probe UA family list ----

    [Fact]
    public void Custom_ua_family_is_matched_when_configured()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = "Prometheus/2.45",
        };
        Assert.True(ProbeShapeClassifier.IsProbeShape(signals, sink: null, probeUserAgents: ["Prometheus"]));
    }
}

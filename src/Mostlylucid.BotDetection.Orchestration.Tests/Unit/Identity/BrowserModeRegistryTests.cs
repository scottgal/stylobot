using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Drives <see cref="BrowserModeRegistry"/> against the YAML inventory shipped
///     in <c>Definitions/BrowserModes/*.yaml</c>. The point of these tests is the
///     YAML contract: every mode the spec claims to ship must actually load, every
///     archetypal request shape must classify to the expected mode, and ordering
///     must hold (specific predicates beat the <c>unknown</c> fallback).
/// </summary>
public sealed class BrowserModeRegistryTests
{
    private readonly ITestOutputHelper _output;
    private readonly BrowserModeRegistry _registry;

    public BrowserModeRegistryTests(ITestOutputHelper output)
    {
        _output = output;
        _registry = new BrowserModeRegistry(NullLogger<BrowserModeRegistry>.Instance, fallbackModeId: "unknown");
    }

    [Fact]
    public void Registry_loads_every_mode_declared_in_the_spec()
    {
        var expectedIds = new[]
        {
            "navigation", "xhr", "sub-resource", "signalr-negotiate",
            "websocket-upgrade", "prefetch", "bot-raw", "unknown"
        };
        var actualIds = _registry.All.Select(m => m.ModeId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in expectedIds)
            Assert.True(actualIds.Contains(id), $"BrowserModes/{id}.yaml did not load. Got: {string.Join(",", actualIds)}");
    }

    [Fact]
    public void Unknown_is_walked_last_so_specific_modes_win()
    {
        var modes = _registry.All;
        Assert.Equal("unknown", modes[^1].ModeId);
    }

    [Theory]
    [MemberData(nameof(ShapeCases))]
    public void Classify_returns_expected_mode_for_archetypal_request_shape(
        string scenarioName,
        Dictionary<string, object?> raw,
        FakeProbe probe,
        string expectedModeId)
    {
        var actual = _registry.Classify(raw, probe);
        _output.WriteLine($"{scenarioName}: expected={expectedModeId} actual={actual}");
        Assert.Equal(expectedModeId, actual);
    }

    public static IEnumerable<object[]> ShapeCases()
    {
        // Chrome navigation: full Sec-Fetch + UIR + text/html Accept.
        yield return new object[]
        {
            "chrome navigation GET /",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["session.method_pattern"] = "GET",
                ["hdr.sec_fetch_pattern"] = 15,
                ["hdr.upgrade_insecure_requests"] = true,
                ["hdr.accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
            },
            new FakeProbe("GET", "/", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sec-Fetch-Dest"] = "document",
            }),
            "navigation",
        };

        // Chrome XHR: sec_fetch=7, UIR=false, Accept=*/*.
        yield return new object[]
        {
            "chrome xhr /api/me",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["session.method_pattern"] = "GET",
                ["hdr.sec_fetch_pattern"] = 7,
                ["hdr.upgrade_insecure_requests"] = false,
                ["hdr.accept"] = "*/*",
            },
            new FakeProbe("GET", "/dashboard/api/me", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sec-Fetch-Dest"] = "empty",
            }),
            "xhr",
        };

        // Chrome sub-resource (image): sec_fetch=7, Sec-Fetch-Dest=image.
        yield return new object[]
        {
            "chrome sub-resource GET /static/logo.png",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["session.method_pattern"] = "GET",
                ["hdr.sec_fetch_pattern"] = 7,
                ["hdr.upgrade_insecure_requests"] = false,
                ["hdr.accept"] = "image/avif,image/webp,*/*;q=0.8",
            },
            new FakeProbe("GET", "/static/logo.png", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sec-Fetch-Dest"] = "image",
            }),
            "sub-resource",
        };

        // SignalR negotiate: POST sec_fetch=7 to /hub/negotiate.
        yield return new object[]
        {
            "signalr POST /stylobot/hub/negotiate",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["session.method_pattern"] = "POST",
                ["hdr.sec_fetch_pattern"] = 7,
                ["hdr.upgrade_insecure_requests"] = false,
                ["hdr.accept"] = "*/*",
            },
            new FakeProbe("POST", "/stylobot/hub/negotiate?negotiateVersion=1", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sec-Fetch-Dest"] = "empty",
            }),
            "signalr-negotiate",
        };

        // WebSocket upgrade: Upgrade header + Sec-WebSocket-Key. Beats navigation
        // even when sec_fetch happens to be unset.
        yield return new object[]
        {
            "websocket upgrade",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["session.method_pattern"] = "GET",
                ["hdr.sec_fetch_pattern"] = 0,
                ["hdr.upgrade_insecure_requests"] = false,
            },
            new FakeProbe("GET", "/stylobot/hub", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Upgrade"] = "websocket",
                ["Connection"] = "Upgrade",
                ["Sec-WebSocket-Key"] = "abc==",
            }),
            "websocket-upgrade",
        };

        // Prefetch: Sec-Purpose=prefetch wins over navigation despite the
        // text/html-ish Accept.
        yield return new object[]
        {
            "prefetch /docs",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["session.method_pattern"] = "GET",
                ["hdr.sec_fetch_pattern"] = 15,
                ["hdr.upgrade_insecure_requests"] = true,
                ["hdr.accept"] = "text/html,*/*;q=0.8",
            },
            new FakeProbe("GET", "/docs", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sec-Purpose"] = "prefetch",
                ["Sec-Fetch-Dest"] = "document",
            }),
            "prefetch",
        };

        // Googlebot: no Sec-Fetch-* at all.
        yield return new object[]
        {
            "googlebot raw GET /",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["session.method_pattern"] = "GET",
                ["hdr.sec_fetch_pattern"] = 0,
                ["hdr.upgrade_insecure_requests"] = false,
                ["hdr.accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            },
            new FakeProbe("GET", "/", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            "bot-raw",
        };

        // Unknown fallback: request with no shape hints at all.
        yield return new object[]
        {
            "unclassifiable",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["session.method_pattern"] = "PUT",
                ["hdr.sec_fetch_pattern"] = 3,
                ["hdr.upgrade_insecure_requests"] = false,
                ["hdr.accept"] = "application/octet-stream",
            },
            new FakeProbe("PUT", "/api/blob", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            "unknown",
        };
    }

    public sealed class FakeProbe : IRequestProbe
    {
        private readonly Dictionary<string, string> _headers;

        public FakeProbe(string method, string path, Dictionary<string, string> headers)
        {
            Method = method;
            Path = path;
            _headers = headers;
        }

        public string Method { get; }
        public string Path { get; }
        public bool HasHeader(string name) => _headers.ContainsKey(name);
        public string HeaderOrDefault(string name, string fallback)
            => _headers.TryGetValue(name, out var v) ? v : fallback;
    }
}
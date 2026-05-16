using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Extensions;

/// <summary>
///     Tests for the URL-rewrite signal-projection helper.
/// </summary>
public class UrlSignalProjectionTests
{
    private static readonly byte[] TestKey = Encoding.UTF8.GetBytes("test-signing-key-do-not-use-in-prod");

    private static HttpContext BuildContext(
        Dictionary<string, object>? signals = null,
        double probability = 0.42,
        RiskBand band = RiskBand.Low,
        bool isBot = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[BotDetectionMiddleware.BotDetectionResultKey] = new BotDetectionResult
        {
            IsBot = isBot,
            ConfidenceScore = probability,
            BotType = isBot ? BotType.Unknown : null,
            BotName = isBot ? "test-bot" : null,
        };
        ctx.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = new AggregatedEvidence
        {
            BotProbability = probability,
            Confidence = probability,
            RiskBand = band,
            Signals = signals ?? new Dictionary<string, object>(),
        };
        return ctx;
    }

    [Fact]
    public void Disabled_options_emit_nothing()
    {
        var ctx = BuildContext();
        var opts = new UrlRewriteOptions { Enabled = false };
        var emitted = new Dictionary<string, string>();
        ctx.AddBotDetectionQueryParams(opts, null, (k, v) => emitted[k] = v);
        Assert.Empty(emitted);
    }

    [Fact]
    public void Projects_country_from_geo_signal()
    {
        var ctx = BuildContext(new Dictionary<string, object>
        {
            [SignalKeys.GeoCountryCode] = "GB",
        });
        var opts = new UrlRewriteOptions
        {
            Enabled = true,
            Signals = new() { "country" },
            Sign = false,
        };
        var emitted = new Dictionary<string, string>();
        ctx.AddBotDetectionQueryParams(opts, null, (k, v) => emitted[k] = v);
        Assert.Equal("GB", emitted["sb_country"]);
    }

    [Fact]
    public void Skips_unknown_country_sentinels()
    {
        var ctx = BuildContext(new Dictionary<string, object>
        {
            [SignalKeys.GeoCountryCode] = "LOCAL",
        });
        var opts = new UrlRewriteOptions
        {
            Enabled = true,
            Signals = new() { "country" },
            Sign = false,
        };
        var emitted = new Dictionary<string, string>();
        ctx.AddBotDetectionQueryParams(opts, null, (k, v) => emitted[k] = v);
        Assert.DoesNotContain("sb_country", emitted.Keys);
    }

    [Fact]
    public void Sign_true_emits_signature_param_and_round_trip_verifies()
    {
        var ctx = BuildContext(
            new Dictionary<string, object>
            {
                [SignalKeys.GeoCountryCode] = "US",
                [SignalKeys.GeoIsVpn] = true,
            },
            probability: 0.87,
            band: RiskBand.High);

        var opts = new UrlRewriteOptions
        {
            Enabled = true,
            Signals = new() { "country", "is_vpn", "probability", "risk_band" },
            Sign = true,
            Prefix = "sb_",
        };

        var emitted = new Dictionary<string, string>();
        ctx.AddBotDetectionQueryParams(opts, TestKey, (k, v) => emitted[k] = v);

        Assert.Contains("sb_sig", emitted.Keys);
        Assert.True(UrlSignalProjection.VerifySignedQueryParams(emitted, "sb_", TestKey));
    }

    [Fact]
    public void Tampered_param_fails_verification()
    {
        var ctx = BuildContext(
            new Dictionary<string, object>
            {
                [SignalKeys.GeoCountryCode] = "US",
            });
        var opts = new UrlRewriteOptions
        {
            Enabled = true,
            Signals = new() { "country" },
            Sign = true,
        };

        var emitted = new Dictionary<string, string>();
        ctx.AddBotDetectionQueryParams(opts, TestKey, (k, v) => emitted[k] = v);
        Assert.True(UrlSignalProjection.VerifySignedQueryParams(emitted, "sb_", TestKey));

        emitted["sb_country"] = "GB"; // attacker swap
        Assert.False(UrlSignalProjection.VerifySignedQueryParams(emitted, "sb_", TestKey));
    }

    [Fact]
    public void Sign_true_with_no_key_throws()
    {
        var ctx = BuildContext(
            new Dictionary<string, object> { [SignalKeys.GeoCountryCode] = "US" });
        var opts = new UrlRewriteOptions
        {
            Enabled = true,
            Signals = new() { "country" },
            Sign = true,
        };
        Assert.Throws<InvalidOperationException>(() =>
            ctx.AddBotDetectionQueryParams(opts, null, (_, _) => { }));
    }

    [Fact]
    public void Verify_rejects_missing_signature()
    {
        var bag = new Dictionary<string, string> { ["sb_country"] = "US" };
        Assert.False(UrlSignalProjection.VerifySignedQueryParams(bag, "sb_", TestKey));
    }

    [Theory]
    [InlineData("/api/users", "/api/*", true)]
    [InlineData("/api/users/42", "/api/*", true)]
    [InlineData("/page", "/api/*", false)]
    [InlineData("/page.html", "*.html", true)]
    [InlineData("/api/data.json", "*.html", false)]
    [InlineData("/exact", "/exact", true)]
    [InlineData("/exact/sub", "/exact", false)]
    public void Pattern_matching_handles_supported_shapes(string path, string pattern, bool expected)
    {
        var opts = new UrlRewriteOptions
        {
            Enabled = true,
            ApplyTo = UrlRewriteScope.Patterns,
            PathPatterns = new() { pattern },
        };
        Assert.Equal(expected, UrlSignalProjection.ShouldRewrite(new PathString(path), opts));
    }

    [Fact]
    public void Exclusions_take_precedence_over_inclusions()
    {
        var opts = new UrlRewriteOptions
        {
            Enabled = true,
            ApplyTo = UrlRewriteScope.Patterns,
            PathPatterns = new() { "/api/*" },
            PathExclusions = new() { "/api/webhook/*" },
        };
        Assert.True(UrlSignalProjection.ShouldRewrite(new PathString("/api/users"), opts));
        Assert.False(UrlSignalProjection.ShouldRewrite(new PathString("/api/webhook/stripe"), opts));
    }

    [Fact]
    public void Scope_all_matches_everything_except_exclusions()
    {
        var opts = new UrlRewriteOptions
        {
            Enabled = true,
            ApplyTo = UrlRewriteScope.All,
            PathExclusions = new() { "*.html" },
        };
        Assert.True(UrlSignalProjection.ShouldRewrite(new PathString("/anything/here"), opts));
        Assert.False(UrlSignalProjection.ShouldRewrite(new PathString("/index.html"), opts));
    }

    [Fact]
    public void Disabled_options_never_match()
    {
        var opts = new UrlRewriteOptions { Enabled = false, ApplyTo = UrlRewriteScope.All };
        Assert.False(UrlSignalProjection.ShouldRewrite(new PathString("/api/users"), opts));
    }

    [Fact]
    public async Task Middleware_mutates_query_string_with_signed_signals()
    {
        var ctx = BuildContext(
            new Dictionary<string, object> { [SignalKeys.GeoCountryCode] = "GB" });
        ctx.Request.Path = "/api/users";
        ctx.Request.QueryString = new QueryString("?page=2&sb_country=US"); // attacker-supplied sb_country

        var opts = new BotDetectionOptions
        {
            SignatureHashKey = "middleware-test-key",
            UrlRewrite = new UrlRewriteOptions
            {
                Enabled = true,
                ApplyTo = UrlRewriteScope.Patterns,
                PathPatterns = new() { "/api/*" },
                Signals = new() { "country" },
                Sign = true,
                StripExisting = true,
            },
        };

        var nextCalled = false;
        var mw = new UrlRewriteSignalsMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            new StaticOptionsMonitor<BotDetectionOptions>(opts),
            NullLogger<UrlRewriteSignalsMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);

        // Original non-prefixed param survives.
        Assert.Equal("2", ctx.Request.Query["page"].ToString());
        // Attacker-supplied sb_country was stripped and replaced with the real value.
        Assert.Equal("GB", ctx.Request.Query["sb_country"].ToString());
        // Signature is present and valid.
        Assert.True(UrlSignalProjection.VerifySignedQueryParams(
            ctx.Request.Query.Select(q => new KeyValuePair<string, string>(q.Key, q.Value.ToString())),
            "sb_",
            Encoding.UTF8.GetBytes(opts.SignatureHashKey!)));
    }

    [Fact]
    public async Task Middleware_skips_paths_outside_include_patterns()
    {
        var ctx = BuildContext(
            new Dictionary<string, object> { [SignalKeys.GeoCountryCode] = "GB" });
        ctx.Request.Path = "/static/image.png";
        ctx.Request.QueryString = new QueryString("?v=1");

        var opts = new BotDetectionOptions
        {
            UrlRewrite = new UrlRewriteOptions
            {
                Enabled = true,
                ApplyTo = UrlRewriteScope.Patterns,
                PathPatterns = new() { "/api/*" },
                Signals = new() { "country" },
                Sign = false,
            },
        };

        var mw = new UrlRewriteSignalsMiddleware(
            _ => Task.CompletedTask,
            new StaticOptionsMonitor<BotDetectionOptions>(opts),
            NullLogger<UrlRewriteSignalsMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        // Query untouched on a non-matching path.
        Assert.Equal("?v=1", ctx.Request.QueryString.Value);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}

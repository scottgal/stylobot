using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Task 1 of the out-of-request materialization plan: the content envelope is
///     the cache key. The request path and the tick materializer must derive an
///     IDENTICAL envelope from the same (manifest, window) — a mismatch is a
///     permanent cache miss (every request recomputes). These tests pin the
///     normalization: sub-bucket time differences collapse to one envelope; any
///     filter dimension that changes the result set produces a distinct envelope;
///     widget-key ordering is irrelevant.
/// </summary>
public sealed class DashboardContentEnvelopeTests
{
    private static readonly DashboardPageManifest Traffic =
        new("dashboard.traffic", new[] { "summary", "top-bots" });

    private static DashboardPageWindow Window(
        DateTime? start = null, DateTime? end = null, string? audience = "all",
        double? probMin = null, IReadOnlyList<string>? domains = null,
        int topN = 500, int bucketMinutes = 60)
        => new(start, end, audience, probMin, domains, topN, bucketMinutes);

    [Fact]
    public void Sub_bucket_start_times_normalize_to_the_same_envelope()
    {
        var w1 = Window(start: new DateTime(2026, 7, 8, 12, 0, 5, DateTimeKind.Utc),
                        end: new DateTime(2026, 7, 8, 13, 0, 0, DateTimeKind.Utc));
        // same 60-minute bucket, 50 seconds later
        var w2 = w1 with { StartTime = new DateTime(2026, 7, 8, 12, 0, 55, DateTimeKind.Utc) };

        Assert.Equal(DashboardContentEnvelope.From(Traffic, w1),
                     DashboardContentEnvelope.From(Traffic, w2));
    }

    [Fact]
    public void Widget_key_order_does_not_change_the_envelope()
    {
        var reordered = new DashboardPageManifest("dashboard.traffic", new[] { "top-bots", "summary" });
        Assert.Equal(DashboardContentEnvelope.From(Traffic, Window()),
                     DashboardContentEnvelope.From(reordered, Window()));
    }

    [Theory]
    [InlineData("bots")]
    [InlineData("humans")]
    public void Different_audience_produces_a_distinct_envelope(string audience)
    {
        Assert.NotEqual(DashboardContentEnvelope.From(Traffic, Window(audience: "all")),
                        DashboardContentEnvelope.From(Traffic, Window(audience: audience)));
    }

    [Fact]
    public void Different_domains_probMin_topN_bucket_each_distinguish_the_envelope()
    {
        var baseEnv = DashboardContentEnvelope.From(Traffic, Window());
        Assert.NotEqual(baseEnv, DashboardContentEnvelope.From(Traffic, Window(domains: new[] { "a.com" })));
        Assert.NotEqual(baseEnv, DashboardContentEnvelope.From(Traffic, Window(probMin: 0.5)));
        Assert.NotEqual(baseEnv, DashboardContentEnvelope.From(Traffic, Window(topN: 100)));
        Assert.NotEqual(baseEnv, DashboardContentEnvelope.From(Traffic, Window(bucketMinutes: 15)));
    }

    [Fact]
    public void Domain_order_does_not_change_the_envelope()
    {
        Assert.Equal(DashboardContentEnvelope.From(Traffic, Window(domains: new[] { "a.com", "b.com" })),
                     DashboardContentEnvelope.From(Traffic, Window(domains: new[] { "b.com", "a.com" })));
    }
}

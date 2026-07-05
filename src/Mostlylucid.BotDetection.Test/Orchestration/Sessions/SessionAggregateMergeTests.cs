using FluentAssertions;
using Mostlylucid.BotDetection.Orchestration.Sessions;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     Pins <see cref="SessionAggregateMerge"/> behaviour -- the load-bearing
///     merge policy that turns per-response samples into per-fingerprint
///     aggregates. Retention priority is the most consequential formula in
///     the whole session model (drives shaped eviction under pressure); it
///     needs strong coverage.
/// </summary>
public class SessionAggregateMergeTests
{
    private static SessionSample NewSample(
        string fingerprintId = "fp-1",
        string siteId = "site-1",
        double botProbability = 0.5,
        double confidence = 0.5,
        int statusCode = 200,
        bool fromUpstream = true,
        bool honeypot = false,
        string? clientType = null)
        => new()
        {
            FingerprintId = fingerprintId,
            SiteId = siteId,
            Timestamp = DateTimeOffset.UtcNow,
            BotProbability = botProbability,
            Confidence = confidence,
            StatusCode = statusCode,
            FromUpstream = fromUpstream,
            Honeypot = honeypot,
            ClientType = clientType,
        };

    // ── FromFirstSample ─────────────────────────────────────────────

    [Fact]
    public void FromFirstSample_creates_aggregate_with_one_sample()
    {
        var sample = NewSample(botProbability: 0.75, confidence: 0.8);
        var agg = SessionAggregateMerge.FromFirstSample(sample);

        agg.SampleCount.Should().Be(1);
        agg.MeanBotProbability.Should().Be(0.75);
        agg.MaxBotProbability.Should().Be(0.75);
        agg.LatestConfidence.Should().Be(0.8);
        agg.HoneypotHits.Should().Be(0);
    }

    [Fact]
    public void FromFirstSample_records_upstream_status_only()
    {
        var upstream = SessionAggregateMerge.FromFirstSample(NewSample(statusCode: 404, fromUpstream: true));
        upstream.UpstreamStatusCounts.Should().ContainKey(404).WhoseValue.Should().Be(1);

        var stylobotOwn = SessionAggregateMerge.FromFirstSample(NewSample(statusCode: 403, fromUpstream: false));
        stylobotOwn.UpstreamStatusCounts.Should().BeEmpty(
            "stylobot's own enforcement codes never count (closed-loop guard)");
    }

    [Fact]
    public void FromFirstSample_counts_honeypot_hit()
    {
        var agg = SessionAggregateMerge.FromFirstSample(NewSample(honeypot: true));
        agg.HoneypotHits.Should().Be(1);
    }

    // ── Merge ───────────────────────────────────────────────────────

    [Fact]
    public void Merge_updates_rolling_mean_across_samples()
    {
        var first = SessionAggregateMerge.FromFirstSample(NewSample(botProbability: 0.2));
        var next = SessionAggregateMerge.Merge(first, NewSample(botProbability: 0.8));

        next.SampleCount.Should().Be(2);
        next.MeanBotProbability.Should().Be(0.5);
    }

    [Fact]
    public void Merge_tracks_max_botProbability_across_samples()
    {
        var agg = SessionAggregateMerge.FromFirstSample(NewSample(botProbability: 0.3));
        agg = SessionAggregateMerge.Merge(agg, NewSample(botProbability: 0.9));
        agg = SessionAggregateMerge.Merge(agg, NewSample(botProbability: 0.4));

        agg.MaxBotProbability.Should().Be(0.9, "peak survives even after subsequent lower samples");
    }

    [Fact]
    public void Merge_accumulates_honeypot_hits()
    {
        var agg = SessionAggregateMerge.FromFirstSample(NewSample(honeypot: true));
        agg = SessionAggregateMerge.Merge(agg, NewSample(honeypot: false));
        agg = SessionAggregateMerge.Merge(agg, NewSample(honeypot: true));

        agg.HoneypotHits.Should().Be(2);
    }

    [Fact]
    public void Merge_accumulates_upstream_status_counts()
    {
        var agg = SessionAggregateMerge.FromFirstSample(NewSample(statusCode: 200));
        agg = SessionAggregateMerge.Merge(agg, NewSample(statusCode: 404));
        agg = SessionAggregateMerge.Merge(agg, NewSample(statusCode: 404));
        agg = SessionAggregateMerge.Merge(agg, NewSample(statusCode: 403, fromUpstream: false));

        agg.UpstreamStatusCounts[200].Should().Be(1);
        agg.UpstreamStatusCounts[404].Should().Be(2);
        agg.UpstreamStatusCounts.Should().NotContainKey(403,
            "stylobot's own block response is FromUpstream=false and never counts");
    }

    [Fact]
    public void Merge_latest_client_type_wins()
    {
        var agg = SessionAggregateMerge.FromFirstSample(NewSample(clientType: "Browser"));
        agg = SessionAggregateMerge.Merge(agg, NewSample(clientType: "Scraper"));

        agg.DominantClientType.Should().Be("Scraper");
    }

    [Fact]
    public void Merge_preserves_client_type_when_new_sample_omits()
    {
        var agg = SessionAggregateMerge.FromFirstSample(NewSample(clientType: "Browser"));
        agg = SessionAggregateMerge.Merge(agg, NewSample(clientType: null));

        agg.DominantClientType.Should().Be("Browser",
            "null client type on a sample must not blank the aggregate's dominant type");
    }

    // ── ComputeRetentionPriority ────────────────────────────────────

    [Fact]
    public void RetentionPriority_peaks_on_maximally_ambiguous_low_confidence()
    {
        // p=0.5, confidence=0 -> ambiguity=1.0, confidenceGap=1.0 -> priority=1.0
        var priority = SessionAggregateMerge.ComputeRetentionPriority(
            meanBotProbability: 0.5, latestConfidence: 0.0, honeypotHits: 0);
        priority.Should().Be(1.0);
    }

    [Fact]
    public void RetentionPriority_drops_to_zero_at_confident_extremes()
    {
        // Confident bot -- we know what this is, evict first.
        var confidentBot = SessionAggregateMerge.ComputeRetentionPriority(
            meanBotProbability: 1.0, latestConfidence: 1.0, honeypotHits: 0);
        confidentBot.Should().Be(0.0);

        // Confident human -- same story.
        var confidentHuman = SessionAggregateMerge.ComputeRetentionPriority(
            meanBotProbability: 0.0, latestConfidence: 1.0, honeypotHits: 0);
        confidentHuman.Should().Be(0.0);
    }

    [Fact]
    public void RetentionPriority_ambiguous_high_confidence_drops()
    {
        // p=0.5 but with confidence=1.0 -- we do not need to keep it,
        // the pipeline is sure it is 0.5-shaped ambiguous behaviour.
        var priority = SessionAggregateMerge.ComputeRetentionPriority(
            meanBotProbability: 0.5, latestConfidence: 1.0, honeypotHits: 0);
        priority.Should().Be(0.0);
    }

    [Fact]
    public void RetentionPriority_honeypot_hit_forces_high_priority()
    {
        // Even a very confident bot verdict -- if a honeypot fired we keep
        // the session in the store because that trail is worth more than
        // the aggregate probability alone.
        var priority = SessionAggregateMerge.ComputeRetentionPriority(
            meanBotProbability: 0.95, latestConfidence: 0.99, honeypotHits: 2);
        priority.Should().BeGreaterThanOrEqualTo(1.0,
            "honeypotHits >= 2 pins the retention floor above the ambiguity signal");
    }

    [Fact]
    public void RetentionPriority_takes_max_of_learning_and_honeypot_floor()
    {
        // Learning score is very high (perfectly ambiguous, no confidence).
        // Honeypot floor is much smaller (0.5 for one hit). Priority should
        // stay at 1.0 (the learning value), not drop to the honeypot floor.
        var priority = SessionAggregateMerge.ComputeRetentionPriority(
            meanBotProbability: 0.5, latestConfidence: 0.0, honeypotHits: 1);
        priority.Should().Be(1.0);
    }
}
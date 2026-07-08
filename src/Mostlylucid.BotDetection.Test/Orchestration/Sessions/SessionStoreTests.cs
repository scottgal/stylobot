using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration.Sessions;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     Pins <see cref="SessionStore"/> behaviour: per-site partitioning,
///     upsert merges into existing aggregate, and the change stream fires
///     on every upsert. Shaped eviction under pressure is exercised
///     indirectly via <see cref="SessionAggregateMerge.ComputeRetentionPriority"/>
///     (covered by its own test file).
/// </summary>
public class SessionStoreTests
{
    private static SessionStore NewStore(int maxAggregatesPerSite = 1000, TimeSpan? cleanup = null)
    {
        var opts = new SessionStoreOptions
        {
            MaxAggregatesPerSite = maxAggregatesPerSite,
            CleanupInterval = cleanup ?? TimeSpan.FromHours(1), // suppress the background loop for unit tests
        };
        return new SessionStore(Options.Create(opts), NullLogger<SessionStore>.Instance);
    }

    private static SessionSample NewSample(
        string fingerprintId = "fp-1",
        string siteId = "site-1",
        double botProbability = 0.5,
        double confidence = 0.5,
        bool honeypot = false)
        => new()
        {
            FingerprintId = fingerprintId,
            SiteId = siteId,
            Timestamp = DateTimeOffset.UtcNow,
            BotProbability = botProbability,
            Confidence = confidence,
            StatusCode = 200,
            FromUpstream = true,
            Honeypot = honeypot,
        };

    [Fact]
    public void Upsert_first_sample_creates_aggregate()
    {
        using var store = NewStore();
        var aggregate = store.Upsert(NewSample());

        aggregate.SampleCount.Should().Be(1);
        aggregate.FingerprintId.Should().Be("fp-1");
        store.TryGet("site-1", "fp-1").Should().NotBeNull();
    }

    [Fact]
    public void Upsert_subsequent_sample_merges_into_existing_aggregate()
    {
        using var store = NewStore();
        store.Upsert(NewSample(botProbability: 0.2));
        var updated = store.Upsert(NewSample(botProbability: 0.8));

        updated.SampleCount.Should().Be(2);
        updated.MeanBotProbability.Should().Be(0.5);
    }

    [Fact]
    public void Upsert_partitions_by_site()
    {
        using var store = NewStore();
        store.Upsert(NewSample(fingerprintId: "fp-1", siteId: "site-a"));
        store.Upsert(NewSample(fingerprintId: "fp-1", siteId: "site-b"));

        store.TryGet("site-a", "fp-1").Should().NotBeNull();
        store.TryGet("site-b", "fp-1").Should().NotBeNull(
            "same fingerprint on a different site must land in its own partition");
        store.TryGet("site-a", "fp-1")!.SiteId.Should().Be("site-a");
    }

    [Fact]
    public void TryGet_returns_null_for_missing_site()
    {
        using var store = NewStore();
        store.TryGet("unknown-site", "fp-1").Should().BeNull();
    }

    [Fact]
    public void TryGet_returns_null_for_missing_fingerprint_in_known_site()
    {
        using var store = NewStore();
        store.Upsert(NewSample(fingerprintId: "fp-1"));

        store.TryGet("site-1", "fp-missing").Should().BeNull();
    }

    [Fact]
    public void Upsert_raises_on_changes_sink_every_time()
    {
        using var store = NewStore();
        var received = new List<SessionAggregate>();
        store.Changes.TypedSignalRaised += evt => received.Add(evt.Payload);

        store.Upsert(NewSample(botProbability: 0.2));
        store.Upsert(NewSample(botProbability: 0.8));

        received.Should().HaveCount(2, "both upserts must fan out to observers");
        received[1].SampleCount.Should().Be(2, "the second raise carries the merged aggregate");
    }

    [Fact]
    public void TryGet_returns_null_for_unknown_site_with_no_sessions()
    {
        using var store = NewStore();
        store.TryGet("nowhere", "fp-any").Should().BeNull();
    }

    [Fact]
    public void Upsert_bumps_retention_priority_when_first_honeypot_hit_lands()
    {
        using var store = NewStore();
        var before = store.Upsert(NewSample(botProbability: 0.95, confidence: 0.99, honeypot: false));
        before.RetentionPriority.Should().BeApproximately(0.0, 0.01,
            "high-confidence stable identity has near-zero retention priority");

        var after = store.Upsert(NewSample(botProbability: 0.95, confidence: 0.99, honeypot: true));
        after.RetentionPriority.Should().BeGreaterThanOrEqualTo(0.5,
            "honeypot floor kicks in even against a confidently classified aggregate");
    }
}
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
    public async Task Remote_fold_is_silent_and_idempotent_by_source_cursor()
    {
        using var store = NewStore();
        var changes = 0;
        var observations = new List<SessionFoldObservation>();
        store.Changes.TypedSignalRaised += _ => changes++;
        store.Observations.TypedSignalRaised += evt => observations.Add(evt.Payload);
        var remote = NewSample(fingerprintId: "fp-remote", botProbability: 0.9);
        var source = new SessionFoldCursor("node-a", "epoch-1", 1, 2);
        var contribution = SessionAggregateMerge.FromFirstSample(remote);

        await store.ApplyRemoteFoldAsync(contribution, source);
        await store.ApplyRemoteFoldAsync(contribution, source);
        var stale = source with { Sequence = 1 };
        await store.ApplyRemoteFoldAsync(contribution with { MeanBotProbability = 0.1 }, stale);

        changes.Should().Be(0);
        observations.Should().ContainSingle();
        observations.Should().OnlyContain(x => x.Origin == SessionFoldOrigin.RemoteCanonical);
        observations.Where(x => x.Origin == SessionFoldOrigin.LocalFragment).Should().BeEmpty();
        store.TryGet("site-1", "fp-remote")!.SampleCount.Should().Be(1);
        store.TryGet("site-1", "fp-remote")!.MeanBotProbability.Should().Be(0.9);
    }

    [Fact]
    public async Task Local_observation_after_remote_fold_is_source_pure()
    {
        using var store = NewStore();
        var observations = new List<SessionFoldObservation>();
        store.Observations.TypedSignalRaised += evt => observations.Add(evt.Payload);

        var remote = SessionAggregateMerge.FromFirstSample(
            NewSample(fingerprintId: "fp-source-purity", botProbability: 0.9)) with
        {
            SampleCount = 3,
        };
        await store.ApplyRemoteFoldAsync(
            remote,
            new SessionFoldCursor("node-remote", "epoch-1", 1, 1));

        var returned = await store.UpsertAsync(
            NewSample(fingerprintId: "fp-source-purity", botProbability: 0.1));

        var local = observations.Single(x => x.Origin == SessionFoldOrigin.LocalFragment).Aggregate;
        local.SampleCount.Should().Be(1);
        local.MeanBotProbability.Should().BeApproximately(0.1, 0.0001);
        returned.SampleCount.Should().Be(4);
        returned.MeanBotProbability.Should().BeApproximately(0.7, 0.0001);
        store.TryGet("site-1", "fp-source-purity")!.SampleCount.Should().Be(4);
        observations.Should().ContainSingle(x => x.Origin == SessionFoldOrigin.RemoteCanonical);
        observations.Should().ContainSingle(x => x.Origin == SessionFoldOrigin.LocalFragment);
    }

    [Fact]
    public async Task Remote_fold_rejects_same_generation_different_source()
    {
        using var store = NewStore();
        var first = SessionAggregateMerge.FromFirstSample(NewSample(fingerprintId: "fp-epoch", botProbability: 0.2));
        await store.ApplyRemoteFoldAsync(first, new SessionFoldCursor("node-a", "epoch-a", 1, 1));

        var rejected = first with { MeanBotProbability = 0.9, SampleCount = 9 };
        await store.ApplyRemoteFoldAsync(rejected, new SessionFoldCursor("node-b", "epoch-a", 1, 2));

        var result = store.TryGet("site-1", "fp-epoch")!;
        result.SampleCount.Should().Be(1);
        result.MeanBotProbability.Should().BeApproximately(0.2, 0.0001);
    }

    [Fact]
    public async Task ApplyRemoteFold_validates_cursor_identity_and_progress()
    {
        using var store = NewStore();
        var aggregate = SessionAggregateMerge.FromFirstSample(NewSample(fingerprintId: "fp-validation"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ApplyRemoteFoldAsync(aggregate, new SessionFoldCursor(" ", "epoch", 1, 1)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ApplyRemoteFoldAsync(aggregate, new SessionFoldCursor("node", " ", 1, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ApplyRemoteFoldAsync(aggregate, new SessionFoldCursor("node", "epoch", 0, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ApplyRemoteFoldAsync(aggregate, new SessionFoldCursor("node", "epoch", 1, 0)));
    }

    [Fact]
    public void ApplyRemoteAtomically_retries_after_fold_exception_with_same_cursor()
    {
        var session = new Session("fp-retry");
        var aggregate = SessionAggregateMerge.FromFirstSample(NewSample(fingerprintId: "fp-retry"));
        var cursor = new SessionFoldCursor("node-a", "epoch-a", 1, 1);
        var attempts = 0;

        Func<IEnumerable<SessionAggregate>, SessionAggregate> fold = aggregates =>
        {
            if (++attempts == 1) throw new InvalidOperationException("test fold failure");
            return SessionAggregateMerge.Fold(aggregates);
        };

        Assert.Throws<InvalidOperationException>(() => session.ApplyRemoteAtomically(cursor, aggregate, fold));

        var retry = session.ApplyRemoteAtomically(cursor, aggregate, fold);
        retry.Applied.Should().BeTrue();
        retry.Aggregate.SampleCount.Should().Be(1);
        session.RemoteAggregate.Should().NotBeNull();
    }

    [Fact]
    public async Task Remote_cumulative_replacement_uses_incoming_snapshot_once()
    {
        using var store = NewStore();
        var first = SessionAggregateMerge.FromFirstSample(NewSample(fingerprintId: "fp-cumulative", botProbability: 0.2));
        var second = first with { SampleCount = 2, MeanBotProbability = 0.8, MaxBotProbability = 0.9 };
        await store.ApplyRemoteFoldAsync(first, new SessionFoldCursor("node-a", "e1", 1, 1));
        await store.ApplyRemoteFoldAsync(second, new SessionFoldCursor("node-a", "e1", 1, 2));

        var result = store.TryGet("site-1", "fp-cumulative")!;
        result.SampleCount.Should().Be(2);
        result.MeanBotProbability.Should().Be(0.8);
    }

    [Fact]
    public async Task Session_fence_rejects_stale_unseen_sources_and_resets_on_new_epoch()
    {
        using var store = NewStore();
        var first = SessionAggregateMerge.FromFirstSample(NewSample(fingerprintId: "fp-fence", botProbability: 0.2));
        await store.ApplyRemoteFoldAsync(first, new SessionFoldCursor("owner-a", "epoch-a", 2, 1));

        var stale = first with { MeanBotProbability = 0.9, SampleCount = 9 };
        await store.ApplyRemoteFoldAsync(stale, new SessionFoldCursor("unseen", "epoch-a", 1, 1));
        store.TryGet("site-1", "fp-fence")!.SampleCount.Should().Be(1);

        var newEpoch = first with { MeanBotProbability = 0.7, SampleCount = 3 };
        await store.ApplyRemoteFoldAsync(newEpoch, new SessionFoldCursor("owner-b", "epoch-b", 3, 1));
        var second = newEpoch with { MeanBotProbability = 0.8, SampleCount = 4 };
        await store.ApplyRemoteFoldAsync(second, new SessionFoldCursor("owner-b", "epoch-b", 3, 2));
        store.TryGet("site-1", "fp-fence")!.SampleCount.Should().Be(4);
        store.TryGet("site-1", "fp-fence")!.MeanBotProbability.Should().BeApproximately(0.8, 0.0001);
    }

    [Fact]
    public async Task Higher_generation_owner_handoff_replaces_old_canonical_snapshot()
    {
        using var store = NewStore();
        var old = SessionAggregateMerge.FromFirstSample(NewSample(fingerprintId: "fp-handoff", botProbability: 0.2)) with { SampleCount = 7 };
        var replacement = old with { MeanBotProbability = 0.8, SampleCount = 3 };
        await store.ApplyRemoteFoldAsync(old, new SessionFoldCursor("owner-a", "epoch-a", 1, 9));
        await store.ApplyRemoteFoldAsync(replacement, new SessionFoldCursor("owner-b", "epoch-b", 2, 1));

        var result = store.TryGet("site-1", "fp-handoff")!;
        result.SampleCount.Should().Be(3);
        result.MeanBotProbability.Should().BeApproximately(0.8, 0.0001);
    }

    [Fact]
    public async Task Local_observation_is_emitted_once_per_fold()
    {
        using var store = NewStore();
        var observations = new List<SessionFoldObservation>();
        store.Observations.TypedSignalRaised += evt => observations.Add(evt.Payload);

        store.Upsert(NewSample());
        store.Upsert(NewSample(botProbability: 0.8));

        observations.Should().HaveCount(2);
        observations.Should().OnlyContain(x => x.Origin == SessionFoldOrigin.LocalFragment);
        observations[^1].Aggregate.SampleCount.Should().Be(2);
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

    [Fact]
    public void Dispose_hands_each_live_bounded_session_to_the_canonical_lifecycle()
    {
        var store = NewStore();
        var finalizing = new List<SessionFinalizingSignal>();
        store.Lifecycle.TypedSignalRaised += evt => finalizing.Add(evt.Payload);

        store.Upsert(NewSample(fingerprintId: "fp-expiring", siteId: "site-expiring"));
        store.Dispose();

        var signal = Assert.Single(finalizing);
        signal.FingerprintId.Should().Be("fp-expiring");
        signal.SiteId.Should().Be("site-expiring");
        signal.Aggregate.SampleCount.Should().Be(1);
        signal.DeadlineUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(-1));
    }
}

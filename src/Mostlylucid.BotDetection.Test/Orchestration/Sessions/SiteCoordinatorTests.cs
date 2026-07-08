using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using Mostlylucid.BotDetection.Orchestration.Sessions.Molecules;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     Slice 1 of the session rearchitecture. Pins the load-bearing invariant that
///     the old partitioned-dict <c>SessionStore</c> violated: under a high-cardinality
///     flood the tracked session count stays BOUNDED by the configured cap (the
///     <see cref="SlidingCacheAtom{TKey,TResult}"/> evicts at-insert), instead of
///     growing linearly with distinct-fingerprint count.
/// </summary>
public sealed class SiteCoordinatorTests
{
    private static SessionContribution Contribution(int i) =>
        new($"req-{i}", DateTimeOffset.UtcNow, new[] { "signal.x" });

    [Fact]
    public async Task Bounded_under_cardinality_flood_session_count_never_exceeds_cap()
    {
        var opts = new SessionCoordinatorOptions { MaxSessions = 100 };
        await using var coord = new SiteCoordinator("example.com", opts);

        // Cardinality flood: 10,000 distinct fingerprints, one contribution each.
        for (var i = 0; i < 10_000; i++)
            await coord.ContributeAsync($"fp-{i}", Contribution(i));

        // THE invariant: bounded by the cap regardless of arrival cardinality.
        // Old SessionStore grew ~linearly with distinct-sig count -> the leak.
        coord.SessionCount.Should().BeLessThanOrEqualTo(opts.MaxSessions,
            "the sliding cache must evict at-insert so the session set is bounded under any flood");
    }

    [Fact]
    public async Task Same_fingerprint_same_kind_collapses_to_latest_in_one_session()
    {
        var opts = new SessionCoordinatorOptions { MaxSessions = 100 };
        await using var coord = new SiteCoordinator("example.com", opts);

        for (var i = 0; i < 5; i++)
            await coord.ContributeAsync("fp-1", Contribution(i));

        coord.TryGetSession("fp-1", out var session).Should().BeTrue();
        coord.SessionCount.Should().Be(1, "all requests for one fingerprint land in one session");
        // Bounded by construction: five same-kind contributions collapse to the latest,
        // so the session does not grow with request count (the memory-pressure guarantee).
        session!.ContributionCount.Should().Be(1, "same-kind contributions retain only the latest");
        session.SnapshotContributions().Should().ContainSingle()
            .Which.RequestId.Should().Be("req-4", "the latest same-kind contribution wins");
    }

    [Fact]
    public async Task Distinct_signal_kinds_are_retained_separately()
    {
        var opts = new SessionCoordinatorOptions { MaxSessions = 100 };
        await using var coord = new SiteCoordinator("example.com", opts);

        await coord.ContributeAsync("fp-1",
            new SessionContribution("r1", DateTimeOffset.UtcNow, new[] { "session.aggregate:snapshot" }));
        await coord.ContributeAsync("fp-1",
            new SessionContribution("r2", DateTimeOffset.UtcNow, new[] { "webbotauth.verdict:v" }));

        coord.TryGetSession("fp-1", out var session).Should().BeTrue();
        session!.ContributionCount.Should().Be(2,
            "different signal kinds (aggregate + verdict) are kept independently, not collapsed");
    }

    [Fact]
    public async Task Contribution_resets_activity_and_session_is_live()
    {
        var opts = new SessionCoordinatorOptions { MaxSessions = 100 };
        await using var coord = new SiteCoordinator("site", opts);

        await coord.ContributeAsync("fp-a", Contribution(0));
        coord.TryGetSession("fp-a", out var s1).Should().BeTrue();
        var firstActivity = s1!.LastActivityAt;

        await Task.Delay(5);
        await coord.ContributeAsync("fp-a", Contribution(1));
        coord.TryGetSession("fp-a", out var s2).Should().BeTrue();

        s2.Should().BeSameAs(s1, "the same live session instance stays writeable across requests");
        s2!.LastActivityAt.Should().BeOnOrAfter(firstActivity);
    }

    [Fact]
    public async Task SessionStore_Upsert_dual_writes_into_site_coordinator()
    {
        var coordOpts = Options.Create(new SessionCoordinatorOptions { MaxSessions = 100 });
        await using var registry = new SiteCoordinatorRegistry(coordOpts, NullLogger<SiteCoordinatorRegistry>.Instance);

        using var store = new SessionStore(
            Options.Create(new SessionStoreOptions { CleanupInterval = TimeSpan.FromHours(1) }),
            NullLogger<SessionStore>.Instance,
            initSignalBus: null,
            siteCoordinators: registry);

        store.Upsert(new SessionSample
        {
            SiteId = "example.com",
            FingerprintId = "fp-1",
            Timestamp = DateTimeOffset.UtcNow,
            BotProbability = 0.9,
            Confidence = 0.8,
            StatusCode = 200,
            FromUpstream = false,
            Honeypot = false,
        });

        // The bridge is fire-and-forget; poll until the contribution lands.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        Session? session = null;
        while (DateTime.UtcNow < deadline)
        {
            if (registry.TryGet("example.com", out var coord)
                && coord!.TryGetSession("fp-1", out session)
                && session!.ContributionCount > 0)
                break;
            await Task.Delay(20);
        }

        session.Should().NotBeNull("the aggregate Upsert must dual-write into the signals-native session layer");
        session!.ContributionCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Upsert_emits_aggregate_snapshot_projectable_by_molecule()
    {
        var coordOpts = Options.Create(new SessionCoordinatorOptions { MaxSessions = 100 });
        await using var registry = new SiteCoordinatorRegistry(coordOpts, NullLogger<SiteCoordinatorRegistry>.Instance);

        using var store = new SessionStore(
            Options.Create(new SessionStoreOptions { CleanupInterval = TimeSpan.FromHours(1) }),
            NullLogger<SessionStore>.Instance,
            initSignalBus: null,
            siteCoordinators: registry);

        store.Upsert(new SessionSample
        {
            SiteId = "example.com",
            FingerprintId = "fp-1",
            Timestamp = DateTimeOffset.UtcNow,
            BotProbability = 0.87,
            Confidence = 0.6,
            StatusCode = 403,
            FromUpstream = false,
            Honeypot = true,
        });

        // Upsert writes the full aggregate snapshot into the bounded session;
        // the molecule reconstructs it on read (no stored aggregate object).
        SessionAggregate? aggregate = null;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (registry.TryGet("example.com", out var coord)
                && coord!.TryGetSession("fp-1", out var session)
                && session is not null)
            {
                aggregate = SessionAggregateMolecule.FromSession(session, "fp-1", "example.com");
                if (aggregate is not null) break;
            }
            await Task.Delay(20);
        }

        aggregate.Should().NotBeNull("Upsert writes a session.aggregate snapshot the molecule can project");
        aggregate!.SampleCount.Should().Be(1);
        aggregate.MeanBotProbability.Should().BeApproximately(0.87, 1e-9);
        aggregate.MaxBotProbability.Should().BeApproximately(0.87, 1e-9);
        aggregate.LatestConfidence.Should().BeApproximately(0.6, 1e-9);
        aggregate.HoneypotHits.Should().Be(1);
    }
}

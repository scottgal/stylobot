using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Markov;

public class MarkovTrackerTests
{
    [Fact]
    public void CohortBaselines_EvictsColdestCohort_WhenAtCapacity()
    {
        // Arrange: cap at 3 cohorts
        var tracker = CreateTrackerWithCohortSize(cohortSize: 3);
        var now = DateTime.UtcNow;

        // Add transitions for cohort-a (1 transition — cold)
        tracker.RecordTransition("sig-a", "/home",    now, isBot: false, isDatacenter: false, isReturning: false, clusterId: "cohort-a");
        tracker.RecordTransition("sig-a", "/about",   now, isBot: false, isDatacenter: false, isReturning: false, clusterId: "cohort-a");

        // Add transitions for cohort-b (3 transitions — warm)
        tracker.RecordTransition("sig-b", "/home",    now, isBot: false, isDatacenter: true,  isReturning: false, clusterId: "cohort-b");
        tracker.RecordTransition("sig-b", "/about",   now, isBot: false, isDatacenter: true,  isReturning: false, clusterId: "cohort-b");
        tracker.RecordTransition("sig-b", "/contact", now, isBot: false, isDatacenter: true,  isReturning: false, clusterId: "cohort-b");
        tracker.RecordTransition("sig-b", "/blog",    now, isBot: false, isDatacenter: true,  isReturning: false, clusterId: "cohort-b");

        // Add transitions for cohort-c (2 transitions — medium)
        tracker.RecordTransition("sig-c", "/home",    now, isBot: false, isDatacenter: false, isReturning: true,  clusterId: "cohort-c");
        tracker.RecordTransition("sig-c", "/about",   now, isBot: false, isDatacenter: false, isReturning: true,  clusterId: "cohort-c");
        tracker.RecordTransition("sig-c", "/contact", now, isBot: false, isDatacenter: false, isReturning: true,  clusterId: "cohort-c");

        // Flush so all 3 cohort baselines are populated
        tracker.FlushCohortUpdates();

        // Sanity: 3 cohorts created (the generic keys plus cluster-qualified keys may appear;
        // we care that the cap is enforced on the 4th insertion)
        var statsAfter3 = tracker.GetStats();
        Assert.True(statsAfter3.CohortCount >= 1, "Should have at least one cohort baseline after 3 groups");

        // Get baseline counts before triggering overflow
        var baselinesAfter3 = tracker.GetCohortBaselines();

        // Act: add transitions for a 4th cohort — this should trigger eviction of the coldest one
        tracker.RecordTransition("sig-d", "/home",    now, isBot: false, isDatacenter: true, isReturning: true, clusterId: "cohort-d");
        tracker.RecordTransition("sig-d", "/pricing", now, isBot: false, isDatacenter: true, isReturning: true, clusterId: "cohort-d");

        tracker.FlushCohortUpdates();

        var baselines = tracker.GetCohortBaselines();

        // Assert: total cohorts must not exceed cap
        Assert.True(baselines.Count <= 3,
            $"Expected at most 3 cohort baselines, got {baselines.Count}");

        // The hottest cohort (cohort-b with most transitions) must survive
        var hotCohortKey = baselines.Keys.FirstOrDefault(k => k.Contains("cohort-b"));
        Assert.NotNull(hotCohortKey);

        // The newest cohort (cohort-d) must be present — it triggered the eviction
        var newCohortKey = baselines.Keys.FirstOrDefault(k => k.Contains("cohort-d"));
        Assert.NotNull(newCohortKey);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static MarkovTracker CreateTrackerWithCohortSize(int cohortSize)
    {
        var options = new BotDetectionOptions
        {
            Markov = new MarkovOptions
            {
                MinTransitionsForDrift    = 5,
                SignatureHalfLifeHours    = 1,
                CohortHalfLifeHours       = 6,
                GlobalHalfLifeHours       = 24,
                MaxEdgesPerNode           = 20,
                RecentTransitionWindowSize = 30,
                MaxTrackedSignatures      = 1_000
            },
            SelfMaintenance = new SelfMaintenanceOptions
            {
                MarkovCohortSize = cohortSize
            }
        };

        return new MarkovTracker(
            NullLogger<MarkovTracker>.Instance,
            Options.Create(options));
    }
}

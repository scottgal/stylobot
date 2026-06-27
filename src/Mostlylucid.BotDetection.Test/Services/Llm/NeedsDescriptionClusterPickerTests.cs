using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Services.Llm;

namespace Mostlylucid.BotDetection.Test.Services.Llm;

/// <summary>
///     EC6e tests pinning the contract of the picker that replaced the queue-based
///     BotClusterDescriptionService:
///       1. Pick excludes cluster ids already reserved in the in-flight set.
///       2. Pick skips clusters whose Description is non-empty (the "needs description"
///          gate, ported verbatim from the legacy filter).
/// </summary>
public class NeedsDescriptionClusterPickerTests
{
    private static SignatureBehavior MakeBehavior(string signature)
    {
        var now = DateTime.UtcNow;
        return new SignatureBehavior
        {
            Signature = signature,
            Requests = new List<SignatureRequest>(),
            FirstSeen = now.AddMinutes(-1),
            LastSeen = now,
            RequestCount = 1,
            AverageInterval = 1.0,
            PathEntropy = 0,
            TimingCoefficient = 0,
            AverageBotProbability = 0.9,
            AberrationScore = 0,
            IsAberrant = false
        };
    }

    private static BotCluster MakeCluster(string id, IEnumerable<string> members, string? description = null) =>
        new()
        {
            ClusterId = id,
            MemberSignatures = members.ToList(),
            Description = description
        };

    [Fact]
    public void Pick_excludes_cluster_ids_already_in_flight()
    {
        var inFlight = new ClusterInFlightSet();
        var picker = new NeedsDescriptionClusterPicker(inFlight);

        var behaviorA = MakeBehavior("sig-A");
        var behaviorB = MakeBehavior("sig-B");
        var clusterA = MakeCluster("cluster-A", new[] { "sig-A" });
        var clusterB = MakeCluster("cluster-B", new[] { "sig-B" });

        picker.TrackClusters(
            new[] { clusterA, clusterB },
            new[] { behaviorA, behaviorB });

        // Pre-reserve cluster-A so the picker should skip it.
        Assert.True(inFlight.TryReserve("cluster-A"));

        var picked = picker.Pick(maxCount: 10);

        Assert.DoesNotContain(picked, p => p.ClusterId == "cluster-A");
        Assert.Contains(picked, p => p.ClusterId == "cluster-B");
    }

    [Fact]
    public void Pick_skips_clusters_that_already_have_a_description()
    {
        var inFlight = new ClusterInFlightSet();
        var picker = new NeedsDescriptionClusterPicker(inFlight);

        var behavior = MakeBehavior("sig-X");
        var describedCluster = MakeCluster("cluster-described", new[] { "sig-X" }, description: "already named");
        var needsDescriptionCluster = MakeCluster("cluster-needs", new[] { "sig-X" }, description: null);

        picker.TrackClusters(
            new[] { describedCluster, needsDescriptionCluster },
            new[] { behavior });

        // TrackClusters itself should filter the already-described one out.
        Assert.Equal(1, picker.TrackedCount);

        var picked = picker.Pick(maxCount: 10);

        Assert.DoesNotContain(picked, p => p.ClusterId == "cluster-described");
        Assert.Contains(picked, p => p.ClusterId == "cluster-needs");
    }
}

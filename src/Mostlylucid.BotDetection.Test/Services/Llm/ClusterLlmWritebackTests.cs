using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Services.Llm;

namespace Mostlylucid.BotDetection.Test.Services.Llm;

/// <summary>
///     EC6e test pinning the cluster writeback contract: after ApplyAsync, the
///     in-flight reservation is always released — even when the descriptor
///     callback throws — so the picker can surface the cluster id again on the
///     next tick.
/// </summary>
public class ClusterLlmWritebackTests
{
    private static ClusterPickItem MakeItem(string clusterId)
    {
        var cluster = new BotCluster
        {
            ClusterId = clusterId,
            MemberSignatures = new List<string>()
        };
        return new ClusterPickItem(clusterId, cluster, Array.Empty<SignatureBehavior>());
    }

    private static ClusterNamingResult MakeResult() => new("CoolBot", "A bot");

    private static BotClusterService CreateClusterService()
    {
        var opts = new BotDetectionOptions();
        var coordinator = new SignatureCoordinator(
            NullLogger<SignatureCoordinator>.Instance,
            Options.Create(opts));
        return new BotClusterService(
            NullLogger<BotClusterService>.Instance,
            Options.Create(opts),
            coordinator,
            new FossLicenseState());
    }

    [Fact]
    public async Task ApplyAsync_releases_in_flight_after_success()
    {
        var inFlight = new ClusterInFlightSet();
        Assert.True(inFlight.TryReserve("cluster-1"));

        var picker = new NeedsDescriptionClusterPicker(inFlight);
        var clusterService = CreateClusterService();
        var writeback = new ClusterLlmWriteback(inFlight, picker, clusterService, clusterCallback: null);

        await writeback.ApplyAsync(MakeItem("cluster-1"), MakeResult(), CancellationToken.None);

        Assert.True(inFlight.TryReserve("cluster-1"));
    }

    [Fact]
    public async Task ApplyAsync_releases_in_flight_even_when_callback_throws()
    {
        var inFlight = new ClusterInFlightSet();
        Assert.True(inFlight.TryReserve("cluster-2"));

        var picker = new NeedsDescriptionClusterPicker(inFlight);
        var clusterService = CreateClusterService();
        var throwingCallback = new ThrowingClusterCallback();
        var writeback = new ClusterLlmWriteback(inFlight, picker, clusterService, throwingCallback);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writeback.ApplyAsync(MakeItem("cluster-2"), MakeResult(), CancellationToken.None));

        Assert.True(inFlight.TryReserve("cluster-2"));
    }

    private sealed class ThrowingClusterCallback : IClusterDescriptionCallback
    {
        public Task OnClusterDescriptionUpdatedAsync(
            string clusterId, string label, string description, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class VectorCompactionServiceTests
{
    // -----------------------------------------------------------------------
    // Tracking fakes for the three centroid stores
    // -----------------------------------------------------------------------

    private sealed class TrackingSignatureStore : ISignatureCentroidStore
    {
        public bool PruneCalled { get; private set; }
        public long? ReceivedCutoff { get; private set; }

        public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
        {
            PruneCalled = true;
            ReceivedCutoff = cutoffEpochSeconds;
            return Task.CompletedTask;
        }

        public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SignatureCentroidRow>>(Array.Empty<SignatureCentroidRow>());
    }

    private sealed class TrackingSessionCentroidStore : ISessionCentroidStore
    {
        public bool PruneCalled { get; private set; }
        public long? ReceivedCutoff { get; private set; }

        public Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
        {
            PruneCalled = true;
            ReceivedCutoff = cutoffEpochSeconds;
            return Task.CompletedTask;
        }

        public Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCentroidRow>>(Array.Empty<SessionCentroidRow>());
    }

    private sealed class TrackingIntentStore : IIntentCentroidStore
    {
        public bool PruneCalled { get; private set; }
        public long? ReceivedCutoff { get; private set; }

        public Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
        {
            PruneCalled = true;
            ReceivedCutoff = cutoffEpochSeconds;
            return Task.CompletedTask;
        }

        public Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IntentCentroidRow>>(Array.Empty<IntentCentroidRow>());
    }

    // -----------------------------------------------------------------------
    // Helper: build VectorCompactionService with the tracking stores
    // -----------------------------------------------------------------------

    private static VectorCompactionService Build(
        TrackingSignatureStore sigStore,
        TrackingSessionCentroidStore sessStore,
        TrackingIntentStore intentStore,
        int centroidRetentionDays = 30)
    {
        var options = new BotDetectionOptions
        {
            SelfMaintenance = new SelfMaintenanceOptions
            {
                CentroidRetentionDays = centroidRetentionDays
            }
        };

        // Use Moq for the large ISessionStore interface — only the compaction methods need stubs
        var sessionStoreMock = new Mock<ISessionStore>();
        sessionStoreMock
            .Setup(s => s.GetOverflowingSignaturesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Signature, int SessionCount)>());

        return new VectorCompactionService(
            sessionStoreMock.Object,
            Options.Create(options),
            NullLogger<VectorCompactionService>.Instance,
            sigStore,
            sessStore,
            intentStore);
    }

    // -----------------------------------------------------------------------
    // Test
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunCentroidPruningAsync_CallsPruneOnAllThreeStores()
    {
        // Arrange
        const int retentionDays = 30;
        var sigStore    = new TrackingSignatureStore();
        var sessStore   = new TrackingSessionCentroidStore();
        var intentStore = new TrackingIntentStore();

        var svc = Build(sigStore, sessStore, intentStore, retentionDays);

        // Record bounds around the expected cutoff epoch
        var beforeCall = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeSeconds();

        // Act — call RunCentroidPruningAsync via reflection (internal method)
        var method = typeof(VectorCompactionService).GetMethod(
            "RunCentroidPruningAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public)!;

        await (Task)method.Invoke(svc, [CancellationToken.None])!;

        var afterCall = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeSeconds();

        // Assert: all three stores received a Prune call
        Assert.True(sigStore.PruneCalled,    "ISignatureCentroidStore.PruneSignaturesOlderThanAsync was not called");
        Assert.True(sessStore.PruneCalled,   "ISessionCentroidStore.PruneSessionsOlderThanAsync was not called");
        Assert.True(intentStore.PruneCalled, "IIntentCentroidStore.PruneIntentsOlderThanAsync was not called");

        // Assert: cutoffs are within a 2-second window of the expected value
        Assert.InRange(sigStore.ReceivedCutoff!.Value,    beforeCall, afterCall + 2);
        Assert.InRange(sessStore.ReceivedCutoff!.Value,   beforeCall, afterCall + 2);
        Assert.InRange(intentStore.ReceivedCutoff!.Value, beforeCall, afterCall + 2);
    }
}

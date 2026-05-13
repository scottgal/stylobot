using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class SignatureCoordinatorVerdictTests
{
    private static SignatureCoordinator CreateCoordinator()
    {
        var opts = new BotDetectionOptions();
        return new SignatureCoordinator(
            NullLogger<SignatureCoordinator>.Instance,
            Options.Create(opts));
    }

    private static Task RecordAsync(
        SignatureCoordinator coord,
        string signature,
        double botProbability,
        string path = "/")
    {
        return coord.RecordRequestAsync(
            signature: signature,
            requestId: Guid.NewGuid().ToString("N"),
            path: path,
            botProbability: botProbability,
            signals: new Dictionary<string, object>(),
            detectorsRan: new HashSet<string> { "Heuristic" });
    }

    [Fact]
    public async Task TryGetVerdictAsync_UnknownSignature_ReturnsNull()
    {
        await using var coord = CreateCoordinator();
        var v = await coord.TryGetVerdictAsync("never-seen");
        Assert.Null(v);
    }

    [Fact]
    public async Task TryGetVerdictAsync_AfterRecordRequest_ReturnsSnapshot()
    {
        await using var coord = CreateCoordinator();
        await RecordAsync(coord, "sig-known", 0.42, "/api/x");

        // The coordinator processes updates via a KeyedSequentialAtom; allow a brief
        // window for the in-flight update to complete before reading the snapshot.
        SignatureVerdict? verdict = null;
        for (var attempt = 0; attempt < 50 && verdict is null; attempt++)
        {
            verdict = await coord.TryGetVerdictAsync("sig-known");
            if (verdict is null || verdict.RequestCount == 0)
            {
                verdict = null;
                await Task.Delay(20);
            }
        }

        Assert.NotNull(verdict);
        Assert.Equal("sig-known", verdict!.SignatureId);
        Assert.Equal(1, verdict.RequestCount);
        Assert.InRange(verdict.BotProbability, 0.0, 1.0);
        Assert.True(verdict.LastSeenUtc != default);
    }

    [Fact]
    public async Task TryGetVerdictAsync_MultipleRequests_ReflectsLatestAggregate()
    {
        await using var coord = CreateCoordinator();
        await RecordAsync(coord, "sig-multi", 0.1, "/");
        await RecordAsync(coord, "sig-multi", 0.2, "/api");
        await RecordAsync(coord, "sig-multi", 0.15, "/api/x");

        // Wait for all three updates to flow through the sequential update atom.
        SignatureVerdict? verdict = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            verdict = await coord.TryGetVerdictAsync("sig-multi");
            if (verdict is not null && verdict.RequestCount >= 3)
                break;
            await Task.Delay(20);
        }

        Assert.NotNull(verdict);
        Assert.Equal(3, verdict!.RequestCount);
    }
}

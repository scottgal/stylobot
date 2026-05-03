using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class HolodeckCoordinatorTests
{
    private static HolodeckCoordinator CreateCoordinator(int maxConcurrent = 10, int maxPerFp = 1)
    {
        var options = Options.Create(new HolodeckOptions
        {
            MaxConcurrentEngagements = maxConcurrent,
            MaxEngagementsPerFingerprint = maxPerFp,
            EngagementTimeoutMs = 5000
        });
        return new HolodeckCoordinator(options);
    }

    [Fact]
    public void TryEngage_FirstRequest_Succeeds()
    {
        var coordinator = CreateCoordinator();
        Assert.True(coordinator.TryEngage("fp-1", out var slot));
        Assert.NotNull(slot);
        slot.Dispose();
    }

    [Fact]
    public void TryEngage_SameFingerprint_Blocked()
    {
        var coordinator = CreateCoordinator();
        Assert.True(coordinator.TryEngage("fp-1", out var slot1));
        Assert.False(coordinator.TryEngage("fp-1", out _));
        slot1!.Dispose();
    }

    [Fact]
    public void TryEngage_AfterDispose_SameFingerprint_Succeeds()
    {
        var coordinator = CreateCoordinator();
        Assert.True(coordinator.TryEngage("fp-1", out var slot1));
        slot1!.Dispose();
        Assert.True(coordinator.TryEngage("fp-1", out var slot2));
        slot2!.Dispose();
    }

    [Fact]
    public void TryEngage_DifferentFingerprints_BothSucceed()
    {
        var coordinator = CreateCoordinator();
        Assert.True(coordinator.TryEngage("fp-1", out var slot1));
        Assert.True(coordinator.TryEngage("fp-2", out var slot2));
        slot1!.Dispose();
        slot2!.Dispose();
    }

    [Fact]
    public void TryEngage_GlobalCapacityExhausted_Blocked()
    {
        var coordinator = CreateCoordinator(maxConcurrent: 2);
        Assert.True(coordinator.TryEngage("fp-1", out var s1));
        Assert.True(coordinator.TryEngage("fp-2", out var s2));
        Assert.False(coordinator.TryEngage("fp-3", out _));
        s1!.Dispose();
        Assert.True(coordinator.TryEngage("fp-3", out var s3));
        s2!.Dispose();
        s3!.Dispose();
    }

    [Fact]
    public void ActiveEngagements_TracksCorrectly()
    {
        var coordinator = CreateCoordinator();
        Assert.Equal(0, coordinator.ActiveEngagements);
        coordinator.TryEngage("fp-1", out var s1);
        Assert.Equal(1, coordinator.ActiveEngagements);
        coordinator.TryEngage("fp-2", out var s2);
        Assert.Equal(2, coordinator.ActiveEngagements);
        s1!.Dispose();
        Assert.Equal(1, coordinator.ActiveEngagements);
        s2!.Dispose();
        Assert.Equal(0, coordinator.ActiveEngagements);
    }
}

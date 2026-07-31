using FluentAssertions;
using Mostlylucid.BotDetection.Definitions.RegistryClients;

namespace Mostlylucid.BotDetection.Test.Definitions.RegistryClients;

/// <summary>
///     Unit tests for <see cref="RegistryClientCorroborationTracker"/> - the bounded,
///     decaying, per-fingerprint "recently proved itself as a registry client" cache
///     that lets <see cref="Orchestration.Atoms.RegistryClientSensor"/> extend earned
///     trust from a real /v2/ OCI proof to a same-fingerprint Harbor management-API
///     (/api/v2.0/) call. It must never trust on its own - only <c>MarkCorroborated</c>
///     (called ONLY after a real OCI corroboration) can create trust.
/// </summary>
public sealed class RegistryClientCorroborationTrackerTests
{
    private static RegistryClientCorroborationTracker NewTracker(
        TimeSpan? slidingWindow = null, TimeSpan? maxLifetime = null) =>
        new(slidingWindow ?? TimeSpan.FromMinutes(10), maxLifetime ?? TimeSpan.FromMinutes(30), capacity: 100);

    [Fact]
    public void IsRecentlyCorroborated_returnsFalse_whenNeverMarked()
    {
        var tracker = NewTracker();

        tracker.IsRecentlyCorroborated("sig-never-seen").Should().BeFalse();
    }

    [Fact]
    public async Task IsRecentlyCorroborated_returnsTrue_afterMarkCorroborated()
    {
        var tracker = NewTracker();

        await tracker.MarkCorroboratedAsync("sig-abc");

        tracker.IsRecentlyCorroborated("sig-abc").Should().BeTrue();
    }

    [Fact]
    public async Task IsRecentlyCorroborated_isScopedToFingerprint_noLeakToDifferentFingerprint()
    {
        var tracker = NewTracker();

        await tracker.MarkCorroboratedAsync("sig-trusted");

        tracker.IsRecentlyCorroborated("sig-unrelated").Should()
            .BeFalse("trust earned by one fingerprint must never leak to a different one sharing IP/UA");
    }

    [Fact]
    public async Task IsRecentlyCorroborated_expiresAfterSlidingWindow()
    {
        var tracker = NewTracker(slidingWindow: TimeSpan.FromMilliseconds(50), maxLifetime: TimeSpan.FromMinutes(5));

        await tracker.MarkCorroboratedAsync("sig-short-lived");
        tracker.IsRecentlyCorroborated("sig-short-lived").Should().BeTrue();

        await Task.Delay(TimeSpan.FromMilliseconds(250));

        tracker.IsRecentlyCorroborated("sig-short-lived").Should()
            .BeFalse("this is bounded, decaying trust for a registry session - not persistent");
    }

    [Fact]
    public void MarkCorroboratedAsync_ignoresEmptyFingerprint()
    {
        var tracker = NewTracker();

        var act = async () => await tracker.MarkCorroboratedAsync("");

        act.Should().NotThrowAsync();
        tracker.IsRecentlyCorroborated("").Should().BeFalse();
    }
}

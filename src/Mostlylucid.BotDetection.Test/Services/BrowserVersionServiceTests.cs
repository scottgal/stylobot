using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="BrowserVersionService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     24-hour <c>Task.Delay</c> loop (note: never registered as a hosted
///     service in production, so the loop never ran); now subscribes to
///     <see cref="TickCadence.Tick1h"/>. Gates the remote fetch on
///     "last-success older than configured interval" so the cadence is
///     still honoured.
/// </summary>
public sealed class BrowserVersionServiceTests
{
    private static IOptions<BotDetectionOptions> Options(bool versionAgeEnabled = true)
    {
        var o = new BotDetectionOptions();
        o.VersionAge.Enabled = versionAgeEnabled;
        o.VersionAge.UpdateIntervalHours = 24;
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1h_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = new BrowserVersionService(
            NullLogger<BrowserVersionService>.Instance,
            Options(),
            new NullHttpClientFactory(),
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1h);
        sub.Name.Should().Be("BrowserVersionService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_is_noop_when_version_age_disabled()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = new BrowserVersionService(
            NullLogger<BrowserVersionService>.Instance,
            Options(versionAgeEnabled: false),
            new NullHttpClientFactory(),
            coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);
        // No throw; service stays subscribed.
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();

        var sut = new BrowserVersionService(
            NullLogger<BrowserVersionService>.Instance,
            Options(),
            new NullHttpClientFactory(),
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose is safe.
        sut.Dispose();
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Licensing;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="LicenseStateRefreshService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>PeriodicTimer(60s)</c> loop that did one-shot
///     <see cref="ILicenseGraceStore.InitializeAsync"/> + repeated
///     <c>RefreshAsync</c>. Now subscribes to <see cref="TickCadence.Tick1m"/>
///     and folds init-once into the first tick.
/// </summary>
public sealed class LicenseStateRefreshServiceTests
{
    private static IOptionsMonitor<BotDetectionOptions> OptionsMonitor(BotDetectionOptions opts)
        => new TestOptionsMonitor<BotDetectionOptions>(opts);

    [Fact]
    public void Constructor_subscribes_to_Tick1m_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var state = new LicenseState();
        var store = new RecordingLicenseGraceStore();

        using var sut = new LicenseStateRefreshService(
            state,
            OptionsMonitor(new BotDetectionOptions()),
            store,
            NullLogger<LicenseStateRefreshService>.Instance,
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1m);
        sub.Name.Should().Be("LicenseStateRefreshService");
        sub.Hint.Should().Be(CostHint.Low);
    }

    [Fact]
    public async Task First_tick_initializes_the_grace_store_then_refreshes()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var state = new LicenseState();
        var store = new RecordingLicenseGraceStore();

        using var sut = new LicenseStateRefreshService(
            state,
            OptionsMonitor(new BotDetectionOptions()),  // no licensing token -> Foss snapshot
            store,
            NullLogger<LicenseStateRefreshService>.Instance,
            coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);

        store.InitializeCalls.Should().Be(0);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
        store.InitializeCalls.Should().Be(1, "first tick performs the one-shot init");

        // Second tick must NOT re-init -- the Interlocked guard makes this
        // idempotent.
        await captured.Handler(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        store.InitializeCalls.Should().Be(1, "subsequent ticks are pure refresh");

        // With no token configured, RefreshAsync paints the state Foss.
        state.IsActive.Should().BeTrue();
        state.IsInGrace.Should().BeFalse();
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var state = new LicenseState();
        var store = new RecordingLicenseGraceStore();

        var sut = new LicenseStateRefreshService(
            state,
            OptionsMonitor(new BotDetectionOptions()),
            store,
            NullLogger<LicenseStateRefreshService>.Instance,
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose is safe.
        sut.Dispose();
    }

    private sealed class RecordingLicenseGraceStore : ILicenseGraceStore
    {
        public int InitializeCalls;

        public Task InitializeAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref InitializeCalls);
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetGraceStartedAtAsync(CancellationToken ct = default)
            => Task.FromResult<DateTimeOffset?>(null);

        public Task SetGraceStartedAtAsync(DateTimeOffset value, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearGraceStartedAtAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
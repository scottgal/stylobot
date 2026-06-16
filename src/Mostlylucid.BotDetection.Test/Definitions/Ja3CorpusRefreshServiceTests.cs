using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Definitions.TlsReference;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Definitions;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="Ja3CorpusRefreshService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(RefreshInterval)</c> loop; now subscribes to
///     <see cref="TickCadence.Tick1h"/> and gates the remote fetch on
///     "last-success older than configured interval". Same shape as the
///     pilot's three-fact template.
/// </summary>
public sealed class Ja3CorpusRefreshServiceTests
{
    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private sealed class StubOptionsMonitor : IOptionsMonitor<BotDetectionOptions>
    {
        public StubOptionsMonitor(BotDetectionOptions value) { CurrentValue = value; }
        public BotDetectionOptions CurrentValue { get; }
        public BotDetectionOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<BotDetectionOptions, string?> listener) => null;
    }

    private static StubOptionsMonitor Options()
    {
        // Default options leave TlsCorpus.RefreshUrl empty -- the tick handler
        // takes the idle path, which is the safest behaviour to exercise in a
        // unit test (no live HTTP, no signed-envelope verification).
        return new StubOptionsMonitor(new BotDetectionOptions());
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1h_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = new Ja3CorpusRefreshService(
            Options(),
            new Ja3ReferenceIndex(),
            new Ja3CorpusEnvelopeVerifier(),
            new NullHttpClientFactory(),
            NullLogger<Ja3CorpusRefreshService>.Instance,
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1h);
        sub.Name.Should().Be("Ja3CorpusRefreshService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_is_noop_when_refresh_url_empty()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = new Ja3CorpusRefreshService(
            Options(),
            new Ja3ReferenceIndex(),
            new Ja3CorpusEnvelopeVerifier(),
            new NullHttpClientFactory(),
            NullLogger<Ja3CorpusRefreshService>.Instance,
            coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();

        var sut = new Ja3CorpusRefreshService(
            Options(),
            new Ja3ReferenceIndex(),
            new Ja3CorpusEnvelopeVerifier(),
            new NullHttpClientFactory(),
            NullLogger<Ja3CorpusRefreshService>.Instance,
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose is safe.
        sut.Dispose();
    }
}
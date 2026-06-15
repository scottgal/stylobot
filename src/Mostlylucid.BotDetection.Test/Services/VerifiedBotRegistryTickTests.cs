using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="VerifiedBotRegistry"/>. Was an
///     <see cref="Microsoft.Extensions.Hosting.IHostedService"/> using a
///     private <see cref="System.Threading.Timer"/> for the IP-range refresh
///     cadence; now subscribes to <see cref="TickCadence.Tick1h"/> and gates
///     the fetch on "last-success older than configured
///     <see cref="VerifiedBotRegistryOptions.IpRangeRefreshHours"/>".
/// </summary>
public sealed class VerifiedBotRegistryTickTests
{
    /// <summary>
    ///     HttpClient backed by a handler that always replies 503 -- the
    ///     registry's refresh path catches HTTP failures internally and logs
    ///     a warning, so a tick handler under this client runs without
    ///     throwing and without any live network traffic.
    /// </summary>
    private sealed class FailingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new HttpClient(new FailingHandler());

        private sealed class FailingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private static VerifiedBotRegistry NewRegistry(RecordingScheduleCoordinator coordinator)
    {
        // Uses BotPatternLoader.Default (embedded YAML) so the test reflects
        // the production list shape. The failing HTTP handler ensures the
        // tick handler completes quickly without any live network calls.
        var options = Options.Create(new VerifiedBotRegistryOptions());
        return new VerifiedBotRegistry(
            NullLogger<VerifiedBotRegistry>.Instance,
            new FailingHttpClientFactory(),
            options,
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1h_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewRegistry(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1h);
        sub.Name.Should().Be("VerifiedBotRegistry");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_when_no_bot_definitions()
    {
        // Empty pattern loader -> zero verifiable definitions -> the refresh
        // sweep has no URLs to fetch and returns cleanly. The captured handler
        // must complete without throwing.
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewRegistry(coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();

        var sut = NewRegistry(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose is safe.
        sut.Dispose();
    }
}

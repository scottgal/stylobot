using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Test.Services;

/// <summary>
///     Wave 2 batch 1 regression coverage for <see cref="GeoLite2UpdateService"/>.
///     Was a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(_options.UpdateCheckInterval)</c> loop; now subscribes to
///     <see cref="TickCadence.Tick1h"/> and reuses the existing
///     "is the file older than 7 days?" gate.
/// </summary>
public sealed class GeoLite2UpdateServiceTickTests
{
    private static IOptions<GeoLite2Options> AutoUpdateOptions() => Options.Create(new GeoLite2Options
    {
        // IsAutoDownloadConfigured requires both AccountId and LicenseKey; the
        // subscription is only created when this gate is satisfied.
        AccountId = 12345,
        LicenseKey = "test-license-key",
        EnableAutoUpdate = true,
        DownloadOnStartup = false,
    });

    [Fact]
    public void Constructor_subscribes_to_Tick1h_with_service_name_when_autoupdate_is_configured()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var http = new Mock<IHttpClientFactory>();

        using var sut = new GeoLite2UpdateService(
            NullLogger<GeoLite2UpdateService>.Instance,
            AutoUpdateOptions(),
            http.Object,
            new SimpleGeoLocationService(),
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        Assert.Equal(TickCadence.Tick1h, sub.Cadence);
        Assert.Equal("GeoLite2UpdateService", sub.Name);
        Assert.Equal(CostHint.High, sub.Hint);
    }

    [Fact]
    public async Task OnTickAsync_does_not_throw_when_database_path_is_unreachable()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var http = new Mock<IHttpClientFactory>();
        // Force a DownloadDatabaseAsync attempt by pointing at a path that
        // doesn't exist; the HttpClient factory mock returns a default client
        // whose GET will throw, the handler must swallow and log.
        var options = Options.Create(new GeoLite2Options
        {
            AccountId = 12345,
            LicenseKey = "test-license-key",
            EnableAutoUpdate = true,
            DownloadOnStartup = false,
            DatabasePath = Path.Combine(Path.GetTempPath(), $"gl2-tick-test-{Guid.NewGuid():N}.mmdb"),
        });

        // Empty factory returns a real HttpClient whose request to MaxMind
        // will fail; the handler logs and swallows.
        http.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new ThrowingHandler()));

        using var sut = new GeoLite2UpdateService(
            NullLogger<GeoLite2UpdateService>.Instance,
            options,
            http.Object,
            new SimpleGeoLocationService(),
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);

        // Must not throw -- OnTickAsync wraps the entire body so a transient
        // network failure can't kill the coordinator's cadence loop.
        await sub.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Fact]
    public void LastSuccessfulFetchUtc_and_LastFailedFetchUtc_are_null_before_any_attempt()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var http = new Mock<IHttpClientFactory>();

        using var sut = new GeoLite2UpdateService(
            NullLogger<GeoLite2UpdateService>.Instance,
            AutoUpdateOptions(),
            http.Object,
            new SimpleGeoLocationService(),
            coordinator);

        // Neither field may default to "now" or any other sentinel that could read as
        // a real timestamp -- a source that has never attempted a fetch must be
        // distinguishable from one that fetched successfully long ago.
        Assert.Null(sut.LastSuccessfulFetchUtc);
        Assert.Null(sut.LastFailedFetchUtc);
    }

    [Fact]
    public async Task Failed_download_records_LastFailedFetchUtc_and_leaves_LastSuccessfulFetchUtc_null()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var http = new Mock<IHttpClientFactory>();
        var options = Options.Create(new GeoLite2Options
        {
            AccountId = 12345,
            LicenseKey = "test-license-key",
            EnableAutoUpdate = true,
            DownloadOnStartup = false,
            DatabasePath = Path.Combine(Path.GetTempPath(), $"gl2-tick-test-{Guid.NewGuid():N}.mmdb"),
        });

        http.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new ThrowingHandler()));

        using var sut = new GeoLite2UpdateService(
            NullLogger<GeoLite2UpdateService>.Instance,
            options,
            http.Object,
            new SimpleGeoLocationService(),
            coordinator);

        var before = DateTimeOffset.UtcNow;
        var ok = await sut.DownloadDatabaseAsync();

        Assert.False(ok);
        Assert.Null(sut.LastSuccessfulFetchUtc);
        Assert.NotNull(sut.LastFailedFetchUtc);
        Assert.True(sut.LastFailedFetchUtc >= before);
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var http = new Mock<IHttpClientFactory>();

        var sut = new GeoLite2UpdateService(
            NullLogger<GeoLite2UpdateService>.Instance,
            AutoUpdateOptions(),
            http.Object,
            new SimpleGeoLocationService(),
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        Assert.True(sub.Disposed);

        // Double-dispose is safe.
        sut.Dispose();
    }

    /// <summary>
    ///     In-test stand-in for the coordinator. GeoDetection.Test can't see
    ///     <c>Mostlylucid.BotDetection.Test.Scheduling.Helpers.RecordingScheduleCoordinator</c>
    ///     (it lives in a sibling test project), so the same shape is duplicated
    ///     locally here. Tiny and stateless enough that the duplication is
    ///     cheaper than a third "test helpers" project.
    /// </summary>
    private sealed class RecordingScheduleCoordinator : IScheduleCoordinator
    {
        private readonly List<Recorded> _subs = new();

        public IReadOnlyList<Recorded> Subscriptions => _subs;

        public IDisposable Subscribe(
            TickCadence cadence,
            string subscriberName,
            CostHint costHint,
            Func<DateTimeOffset, CancellationToken, Task> handler)
        {
            var rec = new Recorded(cadence, subscriberName, costHint, handler);
            _subs.Add(rec);
            return rec;
        }

        public IReadOnlyList<TickSubscriberMetadata> Snapshot()
            => _subs.Select(s => new TickSubscriberMetadata(s.Cadence, s.Name, s.Hint, null, null, 0, 0)).ToArray();

        public sealed class Recorded : IDisposable
        {
            public TickCadence Cadence { get; }
            public string Name { get; }
            public CostHint Hint { get; }
            public Func<DateTimeOffset, CancellationToken, Task> Handler { get; }
            public bool Disposed { get; private set; }

            internal Recorded(TickCadence c, string n, CostHint h, Func<DateTimeOffset, CancellationToken, Task> handler)
            {
                Cadence = c; Name = n; Hint = h; Handler = handler;
            }

            public void Dispose() => Disposed = true;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated network failure");
    }
}
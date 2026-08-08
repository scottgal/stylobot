using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Data.Sources;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Contributor;

/// <summary>
///     Subscribes to <see cref="GeoLite2UpdateService.FetchCompleted"/> and writes each observation
///     through <see cref="IFetchSourceStateStore"/> so it survives a restart. Lives here rather than
///     in the base <c>Mostlylucid.GeoDetection</c> project because that project deliberately has no
///     reference to <c>Mostlylucid.BotDetection</c> (where the state store lives) — this project
///     already has both, so it's the natural place for the bridge. An <see cref="IHostedService"/>
///     purely so the subscription is forced live at startup, matching
///     <see cref="Mostlylucid.GeoDetection.Services.GeoDetectionHostedSingletonsBootstrap"/>'s
///     eager-resolve pattern.
/// </summary>
internal sealed class GeoLite2StatePersistenceBridge : IHostedService
{
    private readonly GeoLite2UpdateService? _updateService;
    private readonly IFetchSourceStateStore _stateStore;
    private Action<bool, DateTimeOffset>? _handler;

    public GeoLite2StatePersistenceBridge(IFetchSourceStateStore stateStore, GeoLite2UpdateService? updateService = null)
    {
        _stateStore = stateStore;
        _updateService = updateService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_updateService is null) return Task.CompletedTask; // no AddGeoRouting - nothing to bridge

        _handler = (succeeded, atUtc) =>
        {
            var task = succeeded
                ? _stateStore.RecordSuccessAsync(GeoDetectionFetchSourceContributor.MaxMindSourceId, atUtc)
                : _stateStore.RecordFailureAsync(GeoDetectionFetchSourceContributor.MaxMindSourceId, atUtc);
            // Fire-and-forget deliberately: the fetch itself already completed (success or failure)
            // independently of whether this write lands, and the event handler can't be async. The
            // store logs and swallows its own failures - see JsonFileFetchSourceStateStore.
            _ = task;
        };
        _updateService.FetchCompleted += _handler;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_handler is not null && _updateService is not null)
            _updateService.FetchCompleted -= _handler;
        return Task.CompletedTask;
    }
}

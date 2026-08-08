using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Data.Sources;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Contributor;

/// <summary>
///     Subscribes to <see cref="GeoLite2UpdateService.FetchCompleted"/> and records FAILURES through
///     <see cref="IFetchSourceStateStore"/> so they survive a restart. Success is deliberately NOT
///     recorded here — <see cref="GeoDetectionFetchSourceContributor"/> derives "last success"
///     straight from the <c>.mmdb</c> file's own mtime instead (overview-'s correction: a stored
///     success claim and the artefact that proves it are two sources of truth, and in this
///     deployment — no persistent volume backs the gateway's data/ directory — they would disagree
///     after every restart). A failed attempt produces no artefact, so it genuinely has nothing to
///     derive from and still needs recording here.
///     <para>
///         Lives here rather than in the base <c>Mostlylucid.GeoDetection</c> project because that
///         project deliberately has no reference to <c>Mostlylucid.BotDetection</c> (where the state
///         store lives) — this project already has both, so it's the natural place for the bridge.
///         An <see cref="IHostedService"/> purely so the subscription is forced live at startup,
///         matching
///         <see cref="Mostlylucid.GeoDetection.Services.GeoDetectionHostedSingletonsBootstrap"/>'s
///         eager-resolve pattern.
///     </para>
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
            if (succeeded) return; // the .mmdb's own mtime carries this - see class docs

            // Fire-and-forget deliberately: the fetch itself already completed independently of
            // whether this write lands, and the event handler can't be async. The store logs and
            // swallows its own failures - see JsonFileFetchSourceStateStore.
            _ = _stateStore.RecordFailureAsync(GeoDetectionFetchSourceContributor.MaxMindSourceId, atUtc);
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

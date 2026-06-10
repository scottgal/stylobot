using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Storage;

/// <summary>
///     Lightweight contract for stores that need a one-shot init at host
///     startup -- typically SQLite schema creation + cold-tier load.
///     Implementors register themselves alongside
///     <see cref="StoreInitService{TStore}"/> as a hosted service, and
///     <see cref="InitializeAsync"/> runs once at <c>StartAsync</c>.
/// </summary>
public interface IStoreInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Generic <see cref="IHostedService"/> shell that calls
///     <see cref="IStoreInitializer.InitializeAsync"/> on
///     <typeparamref name="TStore"/> at boot, then exits. Replaces the
///     three-line-different-only-by-type init services
///     (PoolCollisionInitService, StickyDenyInitService,
///     WaveformHistoryInitService) that were each ~40 lines of identical
///     try/catch/log/await/Task.CompletedTask.
///     <para>
///     Wire-up:
///     <code>
///     services.AddHostedService&lt;StoreInitService&lt;SqliteStickyDenyStore&gt;&gt;();
///     </code>
///     </para>
///     <para>
///     Init failure is caught and logged at WARN rather than rethrown so a
///     transient SQLite issue (file lock, permission) doesn't take down
///     the whole host. The store falls back to hot-only operation until
///     the next process restart -- matches the prior per-service contract.
///     </para>
/// </summary>
/// <typeparam name="TStore">
///     Concrete store type that implements <see cref="IStoreInitializer"/>.
///     The type is used as the logger category and in the warning message
///     so operators can tell which store failed without grepping.
/// </typeparam>
public sealed class StoreInitService<TStore> : IHostedService
    where TStore : class, IStoreInitializer
{
    private readonly TStore _store;
    private readonly ILogger<StoreInitService<TStore>> _logger;

    public StoreInitService(TStore store, ILogger<StoreInitService<TStore>> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken);
            _logger.LogDebug("{Store} initialised", typeof(TStore).Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Store} init failed; will run hot-only until next restart",
                typeof(TStore).Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Orchestration.Manifests;

/// <summary>
///     Background service that subscribes to all registered <see cref="IConfigurationOverrideSource"/>
///     implementations and invalidates the <see cref="IDetectorConfigProvider"/> cache when
///     config changes flow in.
///
///     Enables live config updates without detector restart - commercial packages push changes
///     via Redis pub/sub, this service picks them up and invalidates the cache, and the next
///     request reads the fresh value.
///
///     FOSS ships with no override sources registered, so this service is a no-op.
/// </summary>
public sealed class ConfigurationWatcher : BackgroundService
{
    private readonly IDetectorConfigProvider _provider;
    private readonly IReadOnlyList<IConfigurationOverrideSource> _sources;
    private readonly ILogger<ConfigurationWatcher> _logger;

    public ConfigurationWatcher(
        IDetectorConfigProvider provider,
        IEnumerable<IConfigurationOverrideSource> sources,
        ILogger<ConfigurationWatcher> logger)
    {
        _provider = provider;
        _sources = sources.ToList();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_sources.Count == 0)
        {
            _logger.LogDebug("No configuration override sources registered - watcher idle");
            return;
        }

        _logger.LogInformation(
            "ConfigurationWatcher started with {Count} override source(s): {Sources}",
            _sources.Count, string.Join(", ", _sources.Select(s => s.Name)));

        // Watch all sources in parallel; each yielded change invalidates the cache.
        var tasks = _sources.Select(s => WatchSourceAsync(s, stoppingToken)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task WatchSourceAsync(IConfigurationOverrideSource source, CancellationToken ct)
    {
        try
        {
            await foreach (var change in source.WatchAsync(ct))
            {
                try
                {
                    _provider.InvalidateCache(change.DetectorName);
                    _logger.LogDebug(
                        "Config changed in {Source}: detector={Detector} parameter={Parameter} by={By}",
                        source.Name, change.DetectorName ?? "<all>", change.ParameterPath,
                        change.ChangedBy ?? "<system>");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to apply config change from {Source}: {Parameter}",
                        source.Name, change.ParameterPath);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration watcher failed for source {Source}", source.Name);
        }
    }
}
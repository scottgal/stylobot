using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Orchestration.Manifests;

/// <summary>
///     Watches a directory of YAML / JSON config files on the local filesystem and emits
///     <see cref="ConfigurationChangeNotification"/>s whenever any of them changes. Paired
///     with <see cref="ConfigurationWatcher"/>, this gives FOSS-tier installs hot-reload of
///     detector manifests and overrides without restarting the app - edit a file, save,
///     within ~500ms the new config is live.
///
///     The default watched directory is <c>{ContentRoot}/stylobot-config/</c>. Subdirectories
///     <c>detectors/</c> and <c>overrides/</c> are walked recursively. Files are reloaded via
///     <see cref="DetectorManifestLoader.LoadFromDirectory"/> on the shared singleton loader,
///     so the next cache-miss inside the provider sees the new values.
///
///     This source does NOT supply per-request overrides - <see cref="TryGetParameterAsync"/>
///     returns <c>null</c> always. Its only job is to push change notifications into the watcher
///     so the provider's cache invalidates. Per-request overrides (per-endpoint, per-user) remain
///     a commercial concern handled by <c>ControlPlaneConfigurationSource</c> (or similar) in
///     paid deployments.
/// </summary>
public sealed class FileSystemConfigurationOverrideSource : IConfigurationOverrideSource, IHostedService, IDisposable
{
    public const string DefaultConfigDirName = "stylobot-config";

    private readonly DetectorManifestLoader _manifestLoader;
    private readonly ILogger<FileSystemConfigurationOverrideSource> _logger;
    private readonly string _rootPath;
    private readonly Channel<ConfigurationChangeNotification> _changes =
        Channel.CreateUnbounded<ConfigurationChangeNotification>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<FileSystemWatcher> _watchers = new();
    private bool _disposed;

    public FileSystemConfigurationOverrideSource(
        DetectorManifestLoader manifestLoader,
        IHostEnvironment? hostEnvironment,
        ILogger<FileSystemConfigurationOverrideSource> logger,
        string? overrideRoot = null)
    {
        _manifestLoader = manifestLoader;
        _logger = logger;
        // Fall back to the process base directory when no IHostEnvironment is in DI -
        // this keeps unit-test fixtures (plain ServiceCollection without a host) able to
        // resolve the singleton without having to fake the environment. Real ASP.NET hosts
        // always supply IHostEnvironment so the customer-facing path is unchanged.
        var contentRoot = hostEnvironment?.ContentRootPath ?? AppContext.BaseDirectory;
        _rootPath = overrideRoot
            ?? Path.Combine(contentRoot, DefaultConfigDirName);
    }

    public int Priority => 50;   // Between commercial overrides (10) and YAML manifest defaults (100).
    public string Name => "FileSystem";

    public string RootPath => _rootPath;

    /// <summary>
    ///     Not a per-request override source - this implementation exists purely to emit
    ///     change notifications. Always returns null so the provider falls through to
    ///     appsettings / YAML / code defaults.
    /// </summary>
    public Task<object?> TryGetParameterAsync(
        string detectorName, string parameterName,
        ConfigResolutionContext context, CancellationToken ct = default)
        => Task.FromResult<object?>(null);

    public async IAsyncEnumerable<ConfigurationChangeNotification> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _changes.Reader.WaitToReadAsync(ct))
        {
            while (_changes.Reader.TryRead(out var change))
                yield return change;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_rootPath))
        {
            try
            {
                Directory.CreateDirectory(_rootPath);
                // Seed with README so operators know what this directory is for.
                var readmePath = Path.Combine(_rootPath, "README.md");
                if (!File.Exists(readmePath))
                    File.WriteAllText(readmePath, DefaultReadme);
                _logger.LogInformation("Created StyloBot config directory at {Path}", _rootPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create StyloBot config directory - hot-reload disabled");
                return Task.CompletedTask;
            }
        }

        StartWatching();
        // Prime the manifest loader with any files already on disk.
        ReloadFromDirectory();

        _logger.LogInformation(
            "FileSystem config watcher started on {Path} (hot-reload enabled)", _rootPath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _changes.Writer.TryComplete();
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* best effort */ }
        }
        _watchers.Clear();
        return Task.CompletedTask;
    }

    private void StartWatching()
    {
        var watcher = new FileSystemWatcher(_rootPath)
        {
            Filter = "*.*",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Renamed += (s, e) => OnFileEvent(s, e);
        watcher.Deleted += OnFileEvent;
        watcher.Error += (s, e) => _logger.LogWarning(e.GetException(), "Config watcher error");

        _watchers.Add(watcher);
    }

    private DateTime _lastReload = DateTime.MinValue;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Only react to YAML / JSON edits. Ignore README, git metadata, editor swap files.
        var ext = Path.GetExtension(e.FullPath);
        if (!string.Equals(ext, ".yaml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".yml",  StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
            return;

        // Debounce - editors often write file-atomically (temp file + rename) and fire
        // multiple events per save. Collapse into a single reload if events land within 200ms.
        var now = DateTime.UtcNow;
        if ((now - _lastReload).TotalMilliseconds < 200) return;
        _lastReload = now;

        _logger.LogDebug("Config file event: {Change} {Path}", e.ChangeType, e.FullPath);

        try
        {
            ReloadFromDirectory();
            var notification = new ConfigurationChangeNotification
            {
                // null DetectorName → invalidate ALL detectors; which is what we want for
                // a file-based reload since any detector could be affected.
                DetectorName = null,
                ParameterPath = Path.GetRelativePath(_rootPath, e.FullPath),
                ChangedAt = now,
                ChangedBy = "filesystem-watcher"
            };
            if (!_changes.Writer.TryWrite(notification))
                _logger.LogDebug("Failed to enqueue config change notification (channel closed?)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Config reload failed for {Path}", e.FullPath);
        }
    }

    private void ReloadFromDirectory()
    {
        try
        {
            _manifestLoader.LoadFromDirectory(_rootPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manifest reload from {Path} failed", _rootPath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private const string DefaultReadme = @"# stylobot-config

Local configuration overrides for StyloBot. Files in this directory are reloaded
automatically - edit, save, and the detection pipeline picks up the change within
a second. No restart required.

## Layout

- `detectors/*.detector.yaml` - per-detector weight / confidence / parameter overrides.
  Values merge on top of the defaults bundled in the StyloBot assembly.
- `overrides/*.yaml` - free-form overrides for multi-detector configuration.

## Editing

The StyloBot dashboard has a built-in Monaco editor at `/_stylobot/configuration`
that knows the manifest schema and auto-completes field names. You can also edit
these files directly in VS Code / Neovim / whatever; the file watcher picks them
up either way.

## Paid tiers

Commercial tiers add per-endpoint / per-user / per-API-key overrides via a form-
based editor backed by Postgres + Redis broadcast. The raw-file editor here stays
available as an escape hatch at every tier.
";
}
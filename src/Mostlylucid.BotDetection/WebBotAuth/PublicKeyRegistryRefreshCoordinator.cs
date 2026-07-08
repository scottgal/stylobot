using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     Periodically downloads the public-key manifest from
///     <c>BotDetection:PublicKeyRegistry:ManifestUrl</c> and atomically replaces
///     the live <see cref="PublicKeyRegistry"/> fetched layer. Subscribes to
///     <see cref="TickCadence.Tick1h"/>; gates the fetch on "last-success older
///     than the configured refresh interval". Failures leave the previous snapshot
///     intact — a transient network error never crashes the host. Taxonomy:
///     Coordinator.
/// </summary>
public sealed class PublicKeyRegistryRefreshCoordinator : IDisposable
{
    public const string HttpClientName = "stylobot.public-key-registry";
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(1);

    private readonly IOptionsMonitor<PublicKeyRegistryOptions> _options;
    private readonly PublicKeyRegistry _registry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PublicKeyRegistryRefreshCoordinator> _logger;
    private readonly Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>? _refreshedSignals;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private DateTime? _lastSuccessfulRefreshUtc;
    private int _disposed;

    public PublicKeyRegistryRefreshCoordinator(
        IOptionsMonitor<PublicKeyRegistryOptions> options,
        PublicKeyRegistry registry,
        IHttpClientFactory httpClientFactory,
        ILogger<PublicKeyRegistryRefreshCoordinator> logger,
        IScheduleCoordinator coordinator,
        Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>? refreshedSignals = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _registry = registry;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _refreshedSignals = refreshedSignals;
        _time = timeProvider ?? TimeProvider.System;

        // Seed operator-supplied manual keys immediately so a manual-only host
        // (the staging probe: manual key + hand-signed request, no live registry)
        // resolves keys from boot without waiting for a tick.
        SeedManualKeys();

        _subscription = coordinator.Subscribe(
            TickCadence.Tick1h,
            nameof(PublicKeyRegistryRefreshCoordinator),
            CostHint.Medium,
            OnTickAsync);
    }

    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        var opts = _options.CurrentValue;
        if (!opts.Enabled) return;

        // Manual keys may have been reconfigured since boot — re-seed cheaply.
        SeedManualKeys();

        if (string.IsNullOrWhiteSpace(opts.ManifestUrl)) return;

        var interval = opts.RefreshInterval < MinimumInterval ? MinimumInterval : opts.RefreshInterval;
        if (_lastSuccessfulRefreshUtc is { } last && now.UtcDateTime - last < interval) return;

        await RefreshOnceAsync(ct);
    }

    public async Task<bool> RefreshOnceAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled) return false;

        var url = opts.ManifestUrl;
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("PublicKeyRegistry: ManifestUrl '{Url}' is not an absolute HTTPS URL; skipping refresh", url);
            return false;
        }

        PublicKeyManifest? manifest;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.MaxResponseContentBufferSize = opts.MaxResponseBytes;
            // Source-generated JsonTypeInfo (reflection JSON is disabled under AOT).
            manifest = await client.GetFromJsonAsync(url, PublicKeyManifestJsonContext.Default.PublicKeyManifest, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PublicKeyRegistry: manifest download from {Url} failed", url);
            return false;
        }

        var entries = PublicKeyManifestParser.ToEntries(manifest, url);
        if (entries.Count == 0)
        {
            _logger.LogWarning("PublicKeyRegistry: empty or unparseable manifest from {Url}", url);
            return false;
        }

        var now = _time.GetUtcNow();
        _registry.Replace(entries, now);
        _lastSuccessfulRefreshUtc = now.UtcDateTime;
        _logger.LogInformation("PublicKeyRegistry: loaded {Count} keys from {Url}", entries.Count, url);

        _refreshedSignals?.Raise(
            signal: PublicKeyRegistryRefreshedSignal.Key.Name,
            payload: new PublicKeyRegistryRefreshedSignal
            {
                Timestamp = now,
                KeyCount = entries.Count,
                Source = url
            });

        return true;
    }

    private void SeedManualKeys()
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || opts.ManualKeys.Count == 0) return;

        var manual = new List<PublicKeyEntry>(opts.ManualKeys.Count);
        foreach (var m in opts.ManualKeys)
        {
            if (PublicKeyManifestParser.TryToEntry(
                    m.KeyId, m.AgentName, m.PublicKey, m.Algorithm, m.NotAfter, "manual", out var entry))
                manual.Add(entry);
            else
                _logger.LogWarning("PublicKeyRegistry: manual key '{KeyId}' is malformed and was skipped", m.KeyId);
        }

        _registry.SeedManual(manual);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}

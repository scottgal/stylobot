using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Definitions.WellKnownBots;

/// <summary>
///     Periodically downloads the arcjet well-known-bots catalog from
///     <c>BotDetection:WellKnownBots:Url</c> and atomically replaces
///     the live <see cref="WellKnownBotIndex"/> contents. Subscribes
///     to <see cref="TickCadence.Tick1h"/>; gates the fetch on
///     "last-success older than the configured refresh interval".
///     On first tick the index is empty, so the download runs immediately.
///     Failures leave the previous index intact -- the service never crashes
///     the host on a transient network error.
///     <para>
///         Safety: response size is capped at
///         <see cref="WellKnownBotsOptions.MaxResponseBytes"/> (default 2 MB)
///         so a malicious mirror cannot exhaust memory.
///     </para>
/// </summary>
public sealed class WellKnownBotRefreshService : IDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(1);

    public const string HttpClientName = "stylobot.well-known-bots";

    private readonly IOptionsMonitor<BotDetectionOptions> _options;
    private readonly WellKnownBotIndex _index;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WellKnownBotRefreshService> _logger;
    private readonly IDisposable _subscription;
    private readonly Mostlylucid.BotDetection.Identity.IdentityArchetypeRegistry? _archetypeRegistry;
    private DateTime? _lastSuccessfulRefreshUtc;
    private int _disposed;

    public WellKnownBotRefreshService(
        IOptionsMonitor<BotDetectionOptions> options,
        WellKnownBotIndex index,
        IHttpClientFactory httpClientFactory,
        ILogger<WellKnownBotRefreshService> logger,
        IScheduleCoordinator coordinator,
        Mostlylucid.BotDetection.Identity.IdentityArchetypeRegistry? archetypeRegistry = null)
    {
        _options = options;
        _index = index;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        // Optional so hosts without identity-tracking (the FOSS console for
        // simple UA matching, the benchmarks) keep working. When present
        // each refresh seeds the new arcjet entries into the registry so
        // every known bot id has a root archetype.
        _archetypeRegistry = archetypeRegistry;

        _subscription = coordinator.Subscribe(
            TickCadence.Tick1h,
            nameof(WellKnownBotRefreshService),
            CostHint.Medium,
            OnTickAsync);
    }

    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        var opts = _options.CurrentValue.WellKnownBots;
        if (string.IsNullOrWhiteSpace(opts.Url)) return;

        var interval = opts.RefreshInterval < MinimumInterval ? MinimumInterval : opts.RefreshInterval;
        if (_lastSuccessfulRefreshUtc != null && now.UtcDateTime - _lastSuccessfulRefreshUtc.Value < interval)
            return;

        await RefreshOnceAsync(ct);
    }

    public async Task<bool> RefreshOnceAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue.WellKnownBots;
        var url = opts.Url;
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("WellKnownBots: Url '{Url}' is not an absolute HTTPS URL; skipping refresh", url);
            return false;
        }

        List<WellKnownBotEntry>? entries;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.MaxResponseContentBufferSize = opts.MaxResponseBytes;
            // Source-generated JsonTypeInfo (not the reflection overload): reflection-based
            // JSON is disabled under PublishAot, so the reflection path throws on the AOT
            // console/sidecar and the catalog never loads. See WellKnownBotsJsonContext.
            entries = await client.GetFromJsonAsync(url, WellKnownBotsJsonContext.Default.ListWellKnownBotEntry, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WellKnownBots: download from {Url} failed", url);
            return false;
        }

        if (entries is null or { Count: 0 })
        {
            _logger.LogWarning("WellKnownBots: empty or null list from {Url}", url);
            return false;
        }

        _index.Replace(entries);
        _lastSuccessfulRefreshUtc = DateTime.UtcNow;
        _logger.LogInformation("WellKnownBots: loaded {Count} entries from {Url}", entries.Count, url);

        // Push the catalogue into the identity archetype registry so each
        // arcjet bot id becomes a root archetype basin (idempotent: existing
        // archetypes from explicit YAML or the BotPatterns catalogue win).
        if (_archetypeRegistry is not null)
        {
            try
            {
                var added = _archetypeRegistry.IngestWellKnownBots(_index.EnumerateForArchetypePromotion());
                if (added > 0)
                    _logger.LogInformation(
                        "WellKnownBots: promoted {Added} new entries to root archetypes", added);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "WellKnownBots: failed to promote catalogue to identity archetype registry");
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subscription.Dispose();
    }
}
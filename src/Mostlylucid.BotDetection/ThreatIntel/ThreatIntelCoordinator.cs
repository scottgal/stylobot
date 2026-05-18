using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Default <see cref="IThreatIntelCoordinator"/>. Fans out
///     <see cref="IThreatIntelCoordinator.Lookup"/> across registered providers
///     and filters past-expiry verdicts.
/// </summary>
internal sealed class ThreatIntelCoordinator : IThreatIntelCoordinator
{
    private readonly IReadOnlyList<IThreatIntelProvider> _providers;
    private readonly bool _enabled;
    private readonly ILogger<ThreatIntelCoordinator> _logger;

    public ThreatIntelCoordinator(
        IOptions<BotDetectionOptions> options,
        IEnumerable<IThreatIntelProvider> providers,
        ILogger<ThreatIntelCoordinator> logger)
    {
        _providers = providers?.ToArray() ?? [];
        var ti = options.Value.ThreatIntel;
        _enabled = ti.Enabled && _providers.Count > 0;
        _logger = logger;

        // PrivacyMode is config-surface for a guarantee no provider currently
        // implements. An operator setting redacted-ip / hash / offline-only would
        // expect raw IPs not to leave the host, but the live provider base has no
        // hook for it yet. Fail loud at startup so the compliance assumption is
        // never silently broken. Drop this check when live providers actually
        // honour the mode.
        if (_enabled
            && ti.PrivacyMode != ThreatIntelPrivacyMode.Ip
            && _providers.Any(p => p.Mode == ThreatIntelMode.Live))
        {
            throw new NotSupportedException(
                $"BotDetection:ThreatIntel:PrivacyMode = {ti.PrivacyMode} is configured but no live " +
                "provider currently honours it; raw IPs would still be sent to vendor APIs. Either " +
                "set PrivacyMode=Ip, disable all live providers, or upgrade to a build that " +
                "implements the requested mode.");
        }
    }

    public bool IsEnabled => _enabled;

    public IReadOnlyList<IThreatIntelProvider> Providers => _providers;

    public IReadOnlyList<ThreatIntelVerdict> Lookup(ThreatSubject subject)
    {
        if (!_enabled || _providers.Count == 0) return [];

        List<ThreatIntelVerdict>? hits = null;
        var now = DateTime.UtcNow;
        foreach (var provider in _providers)
        {
            if (!provider.SupportedSubjects.Contains(subject.Type)) continue;

            ThreatIntelVerdict? v;
            try
            {
                v = provider.TryLookup(subject);
            }
            catch (Exception ex)
            {
                // A misbehaving provider must not take down the hot path. Log + skip.
                _logger.LogWarning(ex, "Provider {Provider} threw during TryLookup; skipping", provider.Name);
                continue;
            }
            if (v is null) continue;
            // ExpiresUtc == default(DateTime) is treated as "no expiry" - offline feeds
            // typically don't set it because the feed-level refresh handles staleness.
            if (v.ExpiresUtc != default && v.ExpiresUtc < now) continue;

            (hits ??= []).Add(v);
        }

        return hits is null ? [] : hits;
    }

    public async Task EnrichAsync(ThreatSubject subject, CancellationToken cancellationToken)
    {
        if (!_enabled) return;

        foreach (var provider in _providers)
        {
            if (provider.Mode != ThreatIntelMode.Live) continue;
            if (!provider.SupportedSubjects.Contains(subject.Type)) continue;

            try
            {
                await provider.RefreshAsync(subject, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Provider} threw during EnrichAsync for {Subject}", provider.Name, subject);
            }
        }
    }
}

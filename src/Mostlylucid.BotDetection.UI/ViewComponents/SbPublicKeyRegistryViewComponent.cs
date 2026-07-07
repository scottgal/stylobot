using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.WebBotAuth;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders the Web-Bot-Auth public-key registry — the current known-agent
///     signing keys and the last-refresh time — as an SSR-first dashboard widget.
///     Reads the in-memory <see cref="IPublicKeyRegistry"/> directly (no store
///     query, so no render budget needed). Per <c>feedback_remote_mode_optional_di</c>
///     the registry is optional: a dashboard-only host that never registered the
///     detection stack degrades to an "unavailable" state instead of throwing.
///     <para>
///         Updates arrive via the centralised freshness beacon
///         (<c>data-sb-depends="public-keys"</c>) fired by
///         <c>PublicKeyRegistryBeaconBroadcaster</c> on each refresh — never timed
///         polling (<c>feedback_ssr_signalr_pattern</c>).
///     </para>
/// </summary>
public sealed class SbPublicKeyRegistryViewComponent : ViewComponent
{
    private readonly IPublicKeyRegistry? _registry;
    private readonly IOptions<PublicKeyRegistryOptions>? _options;

    public SbPublicKeyRegistryViewComponent(
        IPublicKeyRegistry? registry = null,
        IOptions<PublicKeyRegistryOptions>? options = null)
    {
        _registry = registry;
        _options = options;
    }

    public IViewComponentResult Invoke()
    {
        if (_registry is null)
            return View("Default", PublicKeyRegistryViewModel.Unavailable);

        var opts = _options?.Value;
        var rows = _registry.Snapshot()
            .OrderBy(k => k.AgentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.KeyId, StringComparer.Ordinal)
            .Select(k => new PublicKeyRow(k.KeyId, k.AgentName, k.Algorithm, k.NotAfter, k.Source))
            .ToList();

        return View("Default", new PublicKeyRegistryViewModel(
            IsUnavailable: false,
            IsEnabled: opts?.Enabled ?? false,
            ManifestUrl: opts?.ManifestUrl ?? "",
            LastRefreshedUtc: _registry.LastRefreshedUtc,
            Keys: rows));
    }
}

/// <summary>Backing model for <c>Views/Shared/Components/SbPublicKeyRegistry/Default.cshtml</c>.</summary>
public sealed record PublicKeyRegistryViewModel(
    bool IsUnavailable,
    bool IsEnabled,
    string ManifestUrl,
    DateTimeOffset? LastRefreshedUtc,
    IReadOnlyList<PublicKeyRow> Keys)
{
    public static readonly PublicKeyRegistryViewModel Unavailable =
        new(IsUnavailable: true, IsEnabled: false, ManifestUrl: "", LastRefreshedUtc: null, Keys: []);
}

/// <summary>One key row in the registry widget.</summary>
public sealed record PublicKeyRow(
    string KeyId,
    string AgentName,
    string Algorithm,
    DateTimeOffset? NotAfter,
    string Source);
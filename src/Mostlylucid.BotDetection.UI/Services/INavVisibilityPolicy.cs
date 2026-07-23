using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Contribution seam for hiding specific nav rows from the dashboard sidebar without touching
///     routing or detection. A commercial host uses this to hide a row (e.g. "purchase",
///     "membership") from anonymous/non-privileged visitors -- the underlying route stays fully
///     reachable and fully detection-enabled; this is visibility-only, never an access gate.
///     <para>
///     Same discipline as <see cref="ISignaturePolicyActionSlot" />: resolved ONCE via singleton
///     constructor injection and called synchronously from the view -- never per-render DI
///     resolution -- so the FOSS default (config-glob match) and any future commercial override
///     pay no cost beyond the call itself.
///     </para>
/// </summary>
public interface INavVisibilityPolicy
{
    /// <summary>
    ///     Returns <c>true</c> when the nav row for <paramref name="path" /> should render.
    ///     Must never affect routing/authorization -- only whether the sidebar link is drawn.
    /// </summary>
    /// <param name="path">
    ///     The nav row's logical id/path (e.g. <c>"traffic"</c>, <c>"visitors"</c>,
    ///     <c>"purchase"</c>, <c>"{packId}/{subId}"</c> for pack sub-rows). No leading slash,
    ///     no <c>BasePath</c> prefix.
    /// </param>
    /// <param name="isPrivilegedViewer">
    ///     True for viewers who must always see every row (operators/admins). Privileged viewers
    ///     bypass hidden-path matching entirely -- exactly one bypass tier, no further gating.
    /// </param>
    bool IsVisible(string path, bool isPrivilegedViewer);
}

/// <summary>
///     Config-bound glob-match hidden-path check -- the FOSS implementation of
///     <see cref="INavVisibilityPolicy" />. Configure <c>Dashboard:HiddenPaths</c> (a list of glob
///     patterns, e.g. <c>["purchase*", "membership*"]</c>) in appsettings; a row whose path matches
///     any pattern is hidden UNLESS the viewer is privileged. Empty/missing config hides nothing --
///     the safe FOSS default, no behaviour change out of the box.
///     <para>
///     Registered via <c>TryAddSingleton</c> so a commercial host can register a richer
///     implementation (per-viewer role, per-tenant config, etc.) ahead of
///     <c>AddStyloBotDashboard</c> if config-glob ever stops being enough -- per the operator's
///     "visibility-only" ruling, it isn't needed initially; commercial's own site-nav wiring is a
///     separate follow-up.
///     </para>
/// </summary>
public sealed class DefaultNavVisibilityPolicy : INavVisibilityPolicy
{
    private readonly IOptionsMonitor<NavVisibilityOptions> _options;

    public DefaultNavVisibilityPolicy(IOptionsMonitor<NavVisibilityOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public bool IsVisible(string path, bool isPrivilegedViewer)
    {
        // Privileged viewers always see everything -- exactly one bypass tier, no further gating.
        if (isPrivilegedViewer) return true;

        var hiddenPaths = _options.CurrentValue.HiddenPaths;
        if (hiddenPaths is not { Count: > 0 }) return true;

        foreach (var glob in hiddenPaths)
        {
            if (string.IsNullOrWhiteSpace(glob)) continue;
            if (Regex.IsMatch(path, GlobToRegexCompiler.Compile(glob), RegexOptions.IgnoreCase))
                return false;
        }

        return true;
    }
}

/// <summary>
///     Bound from <c>Dashboard:HiddenPaths</c>. See <see cref="DefaultNavVisibilityPolicy" />.
/// </summary>
public sealed class NavVisibilityOptions
{
    /// <summary>
    ///     Glob patterns (per <see cref="GlobToRegexCompiler" /> semantics) matched against a nav
    ///     row's logical path. A row hidden by a match here is still fully routable and fully
    ///     detection-enabled -- this list controls sidebar rendering only, nothing else. Default:
    ///     empty (nothing hidden).
    /// </summary>
    public List<string> HiddenPaths { get; set; } = new();
}

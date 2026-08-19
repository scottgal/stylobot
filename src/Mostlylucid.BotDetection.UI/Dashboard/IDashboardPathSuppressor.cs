namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Optional FOSS seam for display-only path suppression. A host may register an
///     implementation to hide specific endpoint paths from the dashboard display;
///     the read/derivation layer applies it ONCE at the rollup — never per-widget —
///     so suppressed paths leave the endpoint list and the aggregate counters.
///     Display-only: detection and counting are untouched; this only shapes what
///     renders. Optional DI — hosts without a registered suppressor see everything.
/// </summary>
public interface IDashboardPathSuppressor
{
    /// <summary>True when the path's data must be hidden in the current context.</summary>
    bool ShouldSuppressPath(string path);
}

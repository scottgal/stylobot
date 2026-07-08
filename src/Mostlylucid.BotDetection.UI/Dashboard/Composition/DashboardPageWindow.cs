namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     The time window, audience filter, domain filter, and pagination parameters
///     passed to <see cref="IDashboardPageComposer.ComposeAsync"/> for a single
///     dashboard page render.
/// </summary>
/// <param name="StartTime">Inclusive start of the time window; null = use store default.</param>
/// <param name="EndTime">Inclusive end of the time window; null = use store default.</param>
/// <param name="AudienceFilter">"bots" | "humans" | null (all).</param>
/// <param name="ProbMin">Minimum bot-probability floor (0–1); null = no floor.</param>
/// <param name="Domains">Domain partition filter; null / empty = all domains.</param>
/// <param name="TopN">Row cap passed to per-dataset queries (e.g. top-N bots).</param>
/// <param name="BucketMinutes">Time-bucket width for the time-series dataset.</param>
public sealed record DashboardPageWindow(
    DateTime? StartTime,
    DateTime? EndTime,
    string? AudienceFilter,
    double? ProbMin,
    IReadOnlyList<string>? Domains,
    int TopN,
    int BucketMinutes);

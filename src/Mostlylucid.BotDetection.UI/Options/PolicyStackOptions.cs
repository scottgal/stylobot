namespace Mostlylucid.BotDetection.UI.Options;

/// <summary>
///     Surface-level tunables for the SbPolicyStack view component. Bound at
///     <c>BotDetection:PolicyStack</c>. Defaults are baked in so callers that
///     never register the options binding still get sensible behaviour.
///     Each value is referenced from exactly one site so renaming a property
///     here is safe to do with a single grep.
/// </summary>
public sealed class PolicyStackOptions
{
    /// <summary>
    ///     Window the explainer + the "no decisions in last X hours" copy
    ///     reference. Matches the default <c>aggregateWindow</c> threaded
    ///     into <c>PolicyStackPresenter.BuildAsync</c>.
    /// </summary>
    public int ExplainerWindowHours { get; init; } = 24;

    /// <summary>
    ///     Cap on the recent-fingerprints datalist served from
    ///     <c>/dashboard/policystack/recent-fingerprints</c>.
    /// </summary>
    public int RecentFingerprintsLimit { get; init; } = 25;

    /// <summary>
    ///     Placeholder rendered in row signal cells whose value is null/zero
    ///     (no p99 sample, no block percentage to show). Em-dash by default;
    ///     overridable for localisation.
    /// </summary>
    public string EmptyCellPlaceholder { get; init; } = "—";
}

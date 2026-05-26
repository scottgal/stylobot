namespace Mostlylucid.BotDetection.UI.Models.Primitives;

/// <summary>Icon + colour + tooltip for a threat band cell. Replaces text columns.</summary>
public sealed record ThreatIconModel(string? Band, double BotProbability);

/// <summary>Icon + tooltip for an intent/bot-type cell. Replaces text columns.</summary>
public sealed record IntentIconModel(string? Intent);

/// <summary>5-segment severity bar for bot-probability cells. Replaces "78%" text.</summary>
public sealed record RiskBarModel(double Probability, string? Band);

/// <summary>
///     Shield-shape risk indicator. Distinct icon shape from the BOT% segment
///     bar so the operator can tell the two columns apart at a glance, while
///     the colour follows the same severity gradient (green->red) across
///     every concerning-ness column in the row.
/// </summary>
public sealed record RiskBadgeModel(string? Band, double BotProbability);

/// <summary>
///     Dot-meter confidence indicator. Confidence magnitude drives saturation
///     and segment fill; IsBot drives the polarity of the colour (confident
///     bot = red, confident human = green) so the row reads consistently with
///     the BOT% and RISK columns. Low confidence is muted grey regardless of
///     direction.
/// </summary>
public sealed record ConfidenceMeterModel(double Confidence, bool IsBot, double BotProbability);

/// <summary>
///     SSR'd inline SVG sparkline. Server emits the path string -- zero client JS,
///     zero extra fetches. Bot and human trend arrays must be the same length;
///     y-axis auto-scales to max(bot, human).
///     <para>
///     <paramref name="ThreatBand"/> drives the bot-trend stroke colour so the
///     sparkline conveys threat (the dangerous-row signal) rather than just
///     "is-bot" -- a Googlebot row at high volume should not paint red just
///     because the actor is automated. Empty / "None" / "Low" keep the line
///     neutral; "Medium" / "Elevated" go amber; "High" / "Critical" /
///     "VeryHigh" go red. This is the same rule the per-row threat shield
///     follows so the operator never sees the sparkline disagreeing with the
///     shield about how concerning a row is.
///     </para>
/// </summary>
public sealed record SparklineModel(int[] BotTrend, int[] HumanTrend, int WindowMinutes, string? ThreatBand = null);

/// <summary>Flag SVG + country-name tooltip.</summary>
public sealed record CountryFlagModel(string? Code, string? Name);

/// <summary>Relative time string with absolute timestamp in title attr.</summary>
public sealed record TimeAgoModel(System.DateTime Utc, string? RelativeText);

/// <summary>
///     Coloured chip for the behavioural grouper. Rendered next to a
///     collapsed row to show WHY rows merged (Identity / Cluster /
///     Vector / Subnet / Named). Hidden for standalone rows + humans.
/// </summary>
public sealed record GroupChipModel(
    Mostlylucid.BotDetection.Grouping.GroupKey? Key,
    int MemberCount);

/// <summary>Filter chip in the table toolbar. Count is optional -- null hides the count badge.</summary>
public sealed record FilterChip(string Key, string Label, int? Count, string Url);

/// <summary>Time-window pill option in the table toolbar.</summary>
public sealed record TimeWindowOption(string Key, string Label, string Url);

/// <summary>
///     Table toolbar: filter chips, optional search, optional time-window pills.
///     The caller pre-builds every URL -- the partial does no URL construction so the
///     route/widget naming convention stays the caller's concern.
/// </summary>
public sealed record TableToolbarModel(
    string TargetId,
    System.Collections.Generic.IReadOnlyList<FilterChip> Chips,
    string? ActiveFilter,
    bool ShowSearch,
    string? SearchUrl,
    System.Collections.Generic.IReadOnlyList<TimeWindowOption>? TimeWindowOptions,
    string? ActiveTimeWindow,
    string? SearchValue = null);

/// <summary>
///     Table pagination footer: page-size, numbered pages, total.
///     <para>
///     <b>Server-side only.</b> <see cref="PageUrl"/> and <see cref="PageSizeUrl"/> are
///     delegates -- this record must not be model-bound, JSON-serialised, or persisted.
///     Construct in the controller / view-component, hand to <c>Html.PartialAsync</c>,
///     done. Razor invokes the delegates inline during render.
///     </para>
/// </summary>
public sealed record TablePaginationModel(
    int Page,
    int PageSize,
    int TotalCount,
    System.Func<int, string> PageUrl,
    System.Func<int, string> PageSizeUrl,
    string TargetId)
{
    // PageSize == 0 is a degenerate state (a caller that hasn't set it). Treat as one
    // synthetic page so the partial renders an empty footer rather than divide-by-zero.
    public int TotalPages => PageSize > 0 ? (TotalCount + PageSize - 1) / PageSize : 1;
    public int FirstItem => TotalCount == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int LastItem => System.Math.Min(Page * PageSize, TotalCount);
}
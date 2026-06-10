using System.Text;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     VS-Code-style <c>@</c>-prefixed filter expression for the
///     <c>SbPolicyStack</c> Effective tab. Tokens compose as conjunctions:
///     every active condition must hold for a row to pass. Unknown
///     <c>@</c>-tokens are silently dropped so future tokens don't break
///     existing URLs.
/// </summary>
/// <remarks>
///     Free-text matches case-insensitively against either the row's
///     pre-rendered <see cref="PolicyStackRowViewModel.PredicateText"/> or
///     the rule notes (notes are joined into the row's predicate text by
///     <see cref="Services.PolicyStackPresenter"/> so the row carries
///     everything we need).
/// </remarks>
public sealed record PolicyStackFilter
{
    /// <summary>Empty filter -- the presenter treats <c>null</c> the same way.</summary>
    public static PolicyStackFilter Empty { get; } = new();

    /// <summary><c>@modified</c> -- only rows whose source is NOT <c>embedded:</c>.</summary>
    public bool OnlyModified { get; init; }

    /// <summary><c>@since:24h|7d|30d</c> -- only rows edited inside this window.</summary>
    public TimeSpan? EditedSince { get; init; }

    /// <summary><c>@scope:endpoint|subdomain|domain|wildcard</c>.</summary>
    public string? OnlyScope { get; init; }

    /// <summary><c>@action:block|challenge|allow|observe|tag|ratelimit</c>.</summary>
    public string? OnlyAction { get; init; }

    /// <summary><c>@observe</c> -- only rows in Observe mode.</summary>
    public bool OnlyObserve { get; init; }

    /// <summary>
    ///     <c>@hot</c> -- only the top-N rows by trigger count
    ///     (N from <see cref="PolicyStackFilterOptions.HotTopN"/>).
    /// </summary>
    public bool HotOnly { get; init; }

    /// <summary>
    ///     <c>@slow</c> -- only rows whose p99 exceeds
    ///     <see cref="PolicyStackFilterOptions.SlowP99Micros"/>.
    /// </summary>
    public bool SlowOnly { get; init; }

    /// <summary><c>@no-hits</c> -- only rows with zero triggers AND zero evaluations.</summary>
    public bool NoHitsOnly { get; init; }

    /// <summary>Case-insensitive substring against predicate text.</summary>
    public string? FreeText { get; init; }

    /// <summary>True iff any condition is active.</summary>
    public bool IsActive =>
        OnlyModified
        || EditedSince is not null
        || !string.IsNullOrEmpty(OnlyScope)
        || !string.IsNullOrEmpty(OnlyAction)
        || OnlyObserve
        || HotOnly
        || SlowOnly
        || NoHitsOnly
        || !string.IsNullOrEmpty(FreeText);

    /// <summary>
    ///     Parses tokens like <c>"@modified @since:7d @scope:endpoint substring"</c>.
    ///     Whitespace-separated. Tokens of the form <c>@key</c> or
    ///     <c>@key:value</c> are structured; everything else accumulates into
    ///     <see cref="FreeText"/>. Unknown <c>@</c>-tokens are silently
    ///     ignored.
    /// </summary>
    public static PolicyStackFilter Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return Empty;

        var onlyModified = false;
        TimeSpan? editedSince = null;
        string? onlyScope = null;
        string? onlyAction = null;
        var onlyObserve = false;
        var hotOnly = false;
        var slowOnly = false;
        var noHitsOnly = false;
        var freeTextParts = new List<string>();

        foreach (var rawToken in input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawToken.Length == 0) continue;

            if (rawToken[0] != '@')
            {
                freeTextParts.Add(rawToken);
                continue;
            }

            var body = rawToken[1..];
            var colon = body.IndexOf(':');
            var key = (colon >= 0 ? body[..colon] : body).ToLowerInvariant();
            var value = colon >= 0 ? body[(colon + 1)..] : string.Empty;

            switch (key)
            {
                case "modified":
                    onlyModified = true;
                    break;
                case "since":
                    if (TryParseSince(value, out var window)) editedSince = window;
                    break;
                case "scope":
                    if (IsKnownScope(value)) onlyScope = value.ToLowerInvariant();
                    break;
                case "action":
                    if (IsKnownAction(value)) onlyAction = value.ToLowerInvariant();
                    break;
                case "observe":
                    onlyObserve = true;
                    break;
                case "hot":
                    hotOnly = true;
                    break;
                case "slow":
                    slowOnly = true;
                    break;
                case "no-hits":
                    noHitsOnly = true;
                    break;
                default:
                    // Forward-compatible: silently drop tokens we don't recognise.
                    break;
            }
        }

        return new PolicyStackFilter
        {
            OnlyModified = onlyModified,
            EditedSince = editedSince,
            OnlyScope = onlyScope,
            OnlyAction = onlyAction,
            OnlyObserve = onlyObserve,
            HotOnly = hotOnly,
            SlowOnly = slowOnly,
            NoHitsOnly = noHitsOnly,
            FreeText = freeTextParts.Count == 0 ? null : string.Join(' ', freeTextParts)
        };
    }

    /// <summary>
    ///     Render the filter to a canonical token string. Ordering is stable
    ///     so two filters that parse to equal values round-trip to the same
    ///     text -- the URL contract depends on this.
    /// </summary>
    public string ToCanonicalString()
    {
        var sb = new StringBuilder();
        void Token(string token)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(token);
        }

        if (OnlyModified) Token("@modified");
        if (EditedSince is { } since) Token($"@since:{FormatSince(since)}");
        if (!string.IsNullOrEmpty(OnlyScope)) Token($"@scope:{OnlyScope}");
        if (!string.IsNullOrEmpty(OnlyAction)) Token($"@action:{OnlyAction}");
        if (OnlyObserve) Token("@observe");
        if (HotOnly) Token("@hot");
        if (SlowOnly) Token("@slow");
        if (NoHitsOnly) Token("@no-hits");
        if (!string.IsNullOrEmpty(FreeText)) Token(FreeText);

        return sb.ToString();
    }

    private static bool TryParseSince(string value, out TimeSpan window)
    {
        // Defers to the canonical shared parser
        // (<see cref="DurationParser"/>) for the actual grammar so the
        // dashboard "@since:&lt;value&gt;" token stays in sync with the YAML
        // rule loader's <c>sustain_for</c> / <c>recover_after</c> fields.
        //
        // The dashboard layers two extra contracts on top of the shared
        // parser:
        //   * only the hour/day units are meaningful for an edit-window
        //     filter, so reject "30s" / "5m" tokens here.
        //   * a non-positive window is meaningless ("show me rows edited in
        //     the last 0h"), so reject those too.
        window = default;
        if (!DurationParser.TryParse(value, out var parsed)) return false;
        if (parsed <= TimeSpan.Zero) return false;

        var lastChar = value.Length > 0 ? value[^1] : '\0';
        if (lastChar is not ('h' or 'H' or 'd' or 'D')) return false;

        window = parsed;
        return true;
    }

    private static string FormatSince(TimeSpan window)
    {
        if (window.TotalHours < 24 && window.TotalHours == Math.Floor(window.TotalHours))
            return $"{(int)window.TotalHours}h";
        if (window.TotalDays == Math.Floor(window.TotalDays))
            return $"{(int)window.TotalDays}d";
        // Fallback: hours.
        return $"{(int)window.TotalHours}h";
    }

    private static bool IsKnownScope(string value) => value.ToLowerInvariant() switch
    {
        "endpoint" or "subdomain" or "domain" or "wildcard" => true,
        _ => false
    };

    private static bool IsKnownAction(string value) => value.ToLowerInvariant() switch
    {
        "block" or "challenge" or "allow" or "observe" or "tag" or "ratelimit" => true,
        _ => false
    };
}

/// <summary>
///     Knobs the filter consults at apply time. Default values are wired into
///     <c>PolicyStackPresenter</c> directly; an <c>IOptions&lt;&gt;</c>
///     binding can override them via config without code changes.
/// </summary>
public sealed class PolicyStackFilterOptions
{
    /// <summary>Top-N rows by trigger count that <c>@hot</c> keeps.</summary>
    public int HotTopN { get; init; } = 10;

    /// <summary>p99 latency in microseconds above which a row is "slow".</summary>
    public long SlowP99Micros { get; init; } = 5000;
}

/// <summary>
///     Renders a <see cref="PolicyStackFilter"/> to a list of display chips.
///     Used by the filter-bar partial to surface the active filter terms as
///     dismissable badges next to the input.
/// </summary>
public static class PolicyStackFilterChips
{
    public readonly record struct Chip(string Token, string Label);

    public static IReadOnlyList<Chip> FromFilter(PolicyStackFilter filter)
    {
        if (filter is null || !filter.IsActive) return Array.Empty<Chip>();

        var chips = new List<Chip>(8);
        if (filter.OnlyModified)
            chips.Add(new Chip("@modified", "Modified from default"));
        if (filter.EditedSince is { } since)
            chips.Add(new Chip($"@since:{FormatSince(since)}", $"Edited in last {FormatSince(since)}"));
        if (!string.IsNullOrEmpty(filter.OnlyScope))
            chips.Add(new Chip($"@scope:{filter.OnlyScope}", $"Scope: {filter.OnlyScope}"));
        if (!string.IsNullOrEmpty(filter.OnlyAction))
            chips.Add(new Chip($"@action:{filter.OnlyAction}", $"Action: {filter.OnlyAction}"));
        if (filter.OnlyObserve)
            chips.Add(new Chip("@observe", "Observe-mode only"));
        if (filter.HotOnly)
            chips.Add(new Chip("@hot", "Hot (top by triggers)"));
        if (filter.SlowOnly)
            chips.Add(new Chip("@slow", "Slow (high p99)"));
        if (filter.NoHitsOnly)
            chips.Add(new Chip("@no-hits", "No hits in window"));
        if (!string.IsNullOrEmpty(filter.FreeText))
            chips.Add(new Chip(filter.FreeText, $"Contains \"{filter.FreeText}\""));
        return chips;
    }

    private static string FormatSince(TimeSpan window)
    {
        if (window.TotalHours < 24 && window.TotalHours == Math.Floor(window.TotalHours))
            return $"{(int)window.TotalHours}h";
        if (window.TotalDays == Math.Floor(window.TotalDays))
            return $"{(int)window.TotalDays}d";
        return $"{(int)window.TotalHours}h";
    }
}

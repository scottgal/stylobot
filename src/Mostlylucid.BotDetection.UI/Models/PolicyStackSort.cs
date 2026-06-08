namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>Sortable column on the <c>SbPolicyStack</c> Effective tab.</summary>
public enum PolicyStackSortKey
{
    /// <summary>Resolver order -- most-specific-first within scope.</summary>
    Priority,

    /// <summary>By matched-in-window count.</summary>
    Triggers,

    /// <summary>By predicate-eval p99 latency. Zero-eval rows sort to the end.</summary>
    P99Latency,

    /// <summary>By <c>WinDistribution["block"] / TotalEvaluations</c>. Zero-eval rows sort to the end.</summary>
    PercentBlocked,

    /// <summary>By <see cref="PolicyStackRowViewModel.LastEditedAt"/>.</summary>
    LastEdited
}

/// <summary>Direction for a <see cref="PolicyStackSort"/>.</summary>
public enum PolicyStackSortDir
{
    Asc,
    Desc
}

/// <summary>
///     Sort state for the <c>SbPolicyStack</c> Effective tab. Lives on the
///     URL via <c>?policystack-sort=...&amp;policystack-dir=...</c>.
/// </summary>
/// <param name="Key">Which column to sort on.</param>
/// <param name="Direction">Ascending or descending.</param>
public sealed record PolicyStackSort(PolicyStackSortKey Key, PolicyStackSortDir Direction)
{
    /// <summary>Default sort -- resolver order, ascending (most-specific-first).</summary>
    public static PolicyStackSort Default { get; } = new(PolicyStackSortKey.Priority, PolicyStackSortDir.Asc);

    /// <summary>
    ///     Parse query-string fragments into a sort. Unknown keys / directions
    ///     fall back to <see cref="Default"/> so URLs stay forward-compatible.
    /// </summary>
    public static PolicyStackSort Parse(string? key, string? dir)
    {
        if (string.IsNullOrWhiteSpace(key)) return Default;

        var parsedKey = key.ToLowerInvariant() switch
        {
            "priority" => PolicyStackSortKey.Priority,
            "triggers" => PolicyStackSortKey.Triggers,
            "p99" => PolicyStackSortKey.P99Latency,
            "blocked-pct" or "blocked_pct" or "blockedpct" => PolicyStackSortKey.PercentBlocked,
            "edited" or "last-edited" or "lastedited" => PolicyStackSortKey.LastEdited,
            _ => (PolicyStackSortKey?)null
        };
        if (parsedKey is null) return Default;

        var parsedDir = (dir ?? string.Empty).ToLowerInvariant() switch
        {
            "desc" or "descending" => PolicyStackSortDir.Desc,
            _ => PolicyStackSortDir.Asc
        };
        return new PolicyStackSort(parsedKey.Value, parsedDir);
    }

    /// <summary>Canonical token for the query string + data attribute.</summary>
    public string KeyToken => Key switch
    {
        PolicyStackSortKey.Priority => "priority",
        PolicyStackSortKey.Triggers => "triggers",
        PolicyStackSortKey.P99Latency => "p99",
        PolicyStackSortKey.PercentBlocked => "blocked-pct",
        PolicyStackSortKey.LastEdited => "edited",
        _ => "priority"
    };

    /// <summary>Canonical token for the direction.</summary>
    public string DirToken => Direction == PolicyStackSortDir.Asc ? "asc" : "desc";
}

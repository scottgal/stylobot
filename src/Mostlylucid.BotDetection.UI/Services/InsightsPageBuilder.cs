using System.Globalization;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds the <see cref="InsightsPageViewModel"/> for the
///     <c>/dashboard/insights</c> global meter inventory page (B1 of the Pack
///     Metrics + Cohesive Views plan).
///     <para>
///         Pure logic -- takes an <see cref="IMeterStream"/> and the parsed query
///         parameters and returns a fully composed view model. No MVC dependency
///         so the same code is exercised from the dashboard middleware and from
///         unit tests directly. Keeps the page testable without the
///         <c>WebApplicationFactory&lt;Program&gt;</c> dance.
///     </para>
///     <para>
///         Per <c>project_gateway_data_locality</c>: this code never touches the
///         DB; it goes through <c>IMeterStream</c> only. Local and Remote
///         implementations live in PrometheusPack.
///     </para>
/// </summary>
public sealed class InsightsPageBuilder
{
    /// <summary>Default sparkline window when the query string omits <c>window</c>.</summary>
    public const string DefaultWindow = "15m";

    /// <summary>Default sparkline buckets when the query string omits <c>buckets</c>.</summary>
    public const int DefaultBuckets = 24;

    /// <summary>Sort key applied when the query string omits <c>sort</c>.</summary>
    public const string DefaultSort = "name";

    /// <summary>Number of namespace chips surfaced on the filter strip.</summary>
    public const int MaxNamespaceFacets = 6;

    /// <summary>
    ///     Builds the page model. Catalog and per-meter time-series are fetched
    ///     from <paramref name="stream"/>; filters are applied in-memory because
    ///     the catalog is bounded at 1024 entries by the LFU cap.
    /// </summary>
    public async Task<InsightsPageViewModel> BuildAsync(
        IMeterStream stream,
        string? kind,
        string? @namespace,
        string? query,
        string? sort,
        string? window,
        int? buckets,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var resolvedSort    = NormalizeSort(sort);
        var resolvedWindow  = NormalizeWindowKey(window);
        var resolvedBuckets = NormalizeBuckets(buckets);
        var win             = ParseWindow(resolvedWindow);

        var catalog = await stream.ListAsync(ct).ConfigureAwait(false);
        var total = catalog.Count;

        var filtered = ApplyFilters(catalog, kind, @namespace, query);

        var rows = new List<MeterTableRowViewModel>(filtered.Count);
        foreach (var entry in filtered)
        {
            ct.ThrowIfCancellationRequested();
            var ts = await stream.GetAsync(entry.Name, win, resolvedBuckets, ct).ConfigureAwait(false);
            rows.Add(BuildRow(entry, ts));
        }

        rows = ApplySort(rows, resolvedSort);

        var emptyMessage = filtered.Count == 0 && total > 0
            ? "No meters match the current filter."
            : "No meters published yet -- once traffic arrives the gateway will register meters here.";

        var table = new MeterTableViewModel(
            Title: "All meters",
            Rows: rows,
            EmptyMessage: emptyMessage,
            Caption: $"{rows.Count} of {total} meters shown");

        return new InsightsPageViewModel(
            Table: table,
            Filters: new InsightsFilters(
                Kind: kind,
                Namespace: @namespace,
                Query: query,
                Sort: resolvedSort,
                Window: resolvedWindow,
                Buckets: resolvedBuckets),
            NamespaceFacets: BuildNamespaceFacets(catalog, @namespace),
            KindFacets: BuildKindFacets(catalog, kind),
            TotalMeterCount: total,
            FilteredMeterCount: rows.Count);
    }

    // ---------- Filtering / sorting ----------

    internal static List<MeterCatalogEntry> ApplyFilters(
        IReadOnlyList<MeterCatalogEntry> catalog,
        string? kind,
        string? @namespace,
        string? query)
    {
        var result = new List<MeterCatalogEntry>(catalog.Count);
        foreach (var entry in catalog)
        {
            if (!string.IsNullOrWhiteSpace(kind)
                && !entry.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(@namespace)
                && (entry.Namespace is null
                    || entry.Namespace.IndexOf(@namespace, StringComparison.OrdinalIgnoreCase) < 0))
                continue;

            if (!string.IsNullOrWhiteSpace(query)
                && entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            result.Add(entry);
        }
        return result;
    }

    internal static List<MeterTableRowViewModel> ApplySort(
        List<MeterTableRowViewModel> rows, string sort)
    {
        return sort switch
        {
            "kind" => rows
                .OrderBy(r => r.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "current_desc" => rows
                .OrderByDescending(r => ParseValueForSort(r.Value))
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "current_asc" => rows
                .OrderBy(r => ParseValueForSort(r.Value))
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => rows
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    // ---------- Row + facet helpers ----------

    internal static MeterTableRowViewModel BuildRow(MeterCatalogEntry entry, MeterTimeSeries? ts)
    {
        var values = ts?.Values ?? Array.Empty<double>();
        SparklineViewModel? spark = values.Count > 0
            ? new SparklineViewModel(values, ColorClass: ColorForKind(entry.Kind))
            : null;

        var current = ts is null ? "—" : FormatNumber(ts.Current);

        return new MeterTableRowViewModel(
            Name: entry.Name,
            Kind: entry.Kind.ToString(),
            Value: current,
            Unit: entry.Unit,
            Sparkline: spark,
            HealthBand: HealthBand.Neutral,
            DrillHref: $"/dashboard/insights/{Uri.EscapeDataString(entry.Name)}",
            Description: entry.Description);
    }

    internal static IReadOnlyList<InsightsFacetOption> BuildNamespaceFacets(
        IReadOnlyList<MeterCatalogEntry> catalog, string? selectedNamespace)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog)
        {
            if (string.IsNullOrEmpty(entry.Namespace)) continue;
            counts[entry.Namespace] = counts.TryGetValue(entry.Namespace, out var c) ? c + 1 : 1;
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxNamespaceFacets)
            .Select(kv => new InsightsFacetOption(
                Value: kv.Key,
                Label: kv.Key,
                Count: kv.Value,
                Selected: !string.IsNullOrEmpty(selectedNamespace)
                          && kv.Key.Equals(selectedNamespace, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    internal static IReadOnlyList<InsightsFacetOption> BuildKindFacets(
        IReadOnlyList<MeterCatalogEntry> catalog, string? selectedKind)
    {
        var counts = new Dictionary<MeterKind, int>();
        foreach (var entry in catalog)
            counts[entry.Kind] = counts.TryGetValue(entry.Kind, out var c) ? c + 1 : 1;

        // Render the four kinds in stable enum order, even when zero, so the chip
        // strip width doesn't jump as the gateway warms up. Zero-count kinds are
        // shown disabled-looking via Count = 0 -- the view drops them.
        var options = new List<InsightsFacetOption>(4);
        foreach (MeterKind kind in Enum.GetValues<MeterKind>())
        {
            if (!counts.TryGetValue(kind, out var count)) continue;
            var value = kind.ToString();
            options.Add(new InsightsFacetOption(
                Value: value,
                Label: value,
                Count: count,
                Selected: !string.IsNullOrEmpty(selectedKind)
                          && value.Equals(selectedKind, StringComparison.OrdinalIgnoreCase)));
        }
        return options;
    }

    // ---------- Normalisation ----------

    internal static string NormalizeSort(string? sort) => sort switch
    {
        "kind" or "current_desc" or "current_asc" or "name" => sort,
        _ => DefaultSort,
    };

    internal static string NormalizeWindowKey(string? window) => window switch
    {
        "5m" or "15m" or "1h" => window,
        _ => DefaultWindow,
    };

    internal static int NormalizeBuckets(int? buckets)
    {
        if (buckets is null) return DefaultBuckets;
        // Defensive: protect the meter stream from a hostile query string.
        // 4 to 240 covers every realistic sparkline density and survives a
        // user typing "1" or pasting a huge number into the URL.
        if (buckets.Value < 4)   return 4;
        if (buckets.Value > 240) return 240;
        return buckets.Value;
    }

    internal static TimeSpan ParseWindow(string normalizedWindow) => normalizedWindow switch
    {
        "5m" => TimeSpan.FromMinutes(5),
        "1h" => TimeSpan.FromHours(1),
        _    => TimeSpan.FromMinutes(15),
    };

    // ---------- Formatting ----------

    internal static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "—";
        var abs = Math.Abs(value);

        if (abs > 1_000_000)
        {
            var m = value / 1_000_000d;
            return m.ToString(Math.Abs(m) < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "M";
        }
        if (abs > 100_000)
        {
            var k = value / 1_000d;
            return k.ToString("0", CultureInfo.InvariantCulture) + "K";
        }
        if (abs > 9_999)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        // Sub-9999 -- integer-style when the value is integer-shaped, otherwise
        // one decimal place. Gauges (e.g. queue depth) and histograms (e.g. ms
        // p50) both go through this path.
        if (Math.Abs(value - Math.Round(value)) < 0.0001)
            return value.ToString("0", CultureInfo.InvariantCulture);
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Tints the per-row sparkline by meter kind so the trend column reads
    ///     consistently at a glance. Sparklines stroke <c>currentColor</c>, so
    ///     these tokens map to the existing dashboard accent classes.
    /// </summary>
    internal static string ColorForKind(MeterKind kind) => kind switch
    {
        MeterKind.Counter       => "spark-accent",
        MeterKind.Gauge         => "spark-good",
        MeterKind.Histogram     => "spark-caution",
        MeterKind.UpDownCounter => "spark-info",
        _                       => "spark-accent",
    };

    /// <summary>
    ///     Parses the pre-formatted value back into a double for sort comparison.
    ///     Returns <see cref="double.MinValue"/> for the placeholder so rows
    ///     without a time-series land at the bottom for <c>current_desc</c> and
    ///     at the top for <c>current_asc</c> (which is fine -- they're equally
    ///     uninformative for an operator scanning a sorted column either way).
    /// </summary>
    private static double ParseValueForSort(string formatted)
    {
        if (string.IsNullOrEmpty(formatted) || formatted == "—") return double.MinValue;

        var s = formatted;
        double mult = 1d;
        if (s.EndsWith('K')) { mult = 1_000d;     s = s[..^1]; }
        else if (s.EndsWith('M')) { mult = 1_000_000d; s = s[..^1]; }

        if (double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            return v * mult;
        return double.MinValue;
    }
}

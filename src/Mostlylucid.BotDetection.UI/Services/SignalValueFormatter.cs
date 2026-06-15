using System.Collections;
using System.Globalization;
using Mostlylucid.BotDetection.Dashboard;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Renders the <c>Value</c> column of <c>SbAllSignals</c> as a short inline
///     string. The default <see cref="object.ToString"/> path is fine for
///     primitives but unhelpful for structured payloads -- e.g.
///     <c>MultiFactorSignatures</c> would otherwise render as
///     <c>"Mostlylucid.BotDetection.Dashboard.MultiFactorSignatures"</c>, which
///     leaks the type name and tells the operator nothing about the value.
///
///     <para>
///     The dispatch order is deliberate: known structured types first (so we
///     produce shape-aware summaries), <c>IEnumerable</c> second (with a small
///     element cap), and only then <see cref="object.ToString"/>. Numbers go
///     through invariant culture so the rendered value stays stable across
///     locales (the dashboard is shared across operators in different regions).
///     </para>
/// </summary>
public static class SignalValueFormatter
{
    /// <summary>Cap on the number of elements shown inline for arrays / lists.</summary>
    public const int MaxEnumerableInline = 12;

    /// <summary>
    ///     Format <paramref name="value"/> as a short, deterministic, type-name-free
    ///     string suitable for the all-signals table's Value column.
    /// </summary>
    public static string Format(object? value)
    {
        if (value is null) return "null";

        switch (value)
        {
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case MultiFactorSignatures mfs:
                return FormatMultiFactorSignatures(mfs);
            case IDictionary dict:
                return FormatDictionary(dict);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case IEnumerable enumerable:
                return FormatEnumerable(enumerable);
            default:
                // ToString() can theoretically throw on user-supplied objects --
                // signals come from contributor packs we don't fully control,
                // so catch and degrade rather than blow up the whole table.
                try
                {
                    return value.ToString() ?? string.Empty;
                }
                catch
                {
                    return "<unrenderable>";
                }
        }
    }

    private static string FormatMultiFactorSignatures(MultiFactorSignatures mfs)
    {
        // Shape-aware summary -- operator sees the factor count and the first
        // few hex bytes of the primary signature so they can correlate the
        // row with the dashboard's signature pages without leaking the type
        // name. Keep the suffix short; the full signature surfaces elsewhere.
        var primary = mfs.PrimarySignature;
        var primaryFragment = string.IsNullOrEmpty(primary)
            ? "<empty>"
            : primary.Length > 12 ? primary[..12] + "..." : primary;
        return $"MultiFactorSignatures({mfs.FactorCount} factors, primary={primaryFragment})";
    }

    private static string FormatDictionary(IDictionary dict)
    {
        return $"Dictionary({dict.Count} entries)";
    }

    private static string FormatEnumerable(IEnumerable enumerable)
    {
        var items = new List<string>();
        var count = 0;
        var truncated = false;
        foreach (var item in enumerable)
        {
            if (count >= MaxEnumerableInline)
            {
                truncated = true;
                break;
            }
            items.Add(Format(item));
            count++;
        }
        var body = string.Join(", ", items);
        return truncated ? "[" + body + ", ...]" : "[" + body + "]";
    }
}
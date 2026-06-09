using System.Globalization;
using System.Text;

namespace Mostlylucid.BotDetection.Telemetry.Prometheus;

/// <summary>
///     Targeted parser for the Prometheus / OpenMetrics text-format exposition
///     that ASP.NET's OpenTelemetry Prometheus exporter emits. Used by
///     <see cref="RemoteMeterStream" /> to materialise per-family
///     <see cref="PrometheusMetric" />s from a scraped <c>/metrics</c> body.
/// </summary>
/// <remarks>
///     <para>
///         <b>Tolerance:</b> the parser never throws on malformed input. A bad
///         line is skipped; valid lines around it parse normally. This is
///         deliberate -- a flapping gateway emitting a partial scrape must not
///         crash the viewer host.
///     </para>
///     <para>
///         <b>Family rollup:</b> samples that share a base family name are
///         grouped into a single <see cref="PrometheusMetric" />. The most recent
///         <c># HELP</c> / <c># TYPE</c> / <c># UNIT</c> directives seen for a
///         name win. For histograms / summaries the <c>_bucket</c> / <c>_sum</c>
///         / <c>_count</c> sample lines are kept as samples under the family.
///     </para>
///     <para>
///         <b>Label unescaping:</b> the standard Prometheus escapes are honoured
///         inside label values: <c>\\</c> -> <c>\</c>, <c>\"</c> -> <c>"</c>,
///         <c>\n</c> -> LF. Other backslash sequences pass through unchanged.
///     </para>
/// </remarks>
public static class PrometheusTextParser
{
    /// <summary>
    ///     Parses an entire Prometheus text-exposition body into one
    ///     <see cref="PrometheusMetric" /> per family name. Empty bodies return
    ///     an empty list; malformed lines are skipped.
    /// </summary>
    public static IReadOnlyList<PrometheusMetric> Parse(string body)
    {
        if (string.IsNullOrEmpty(body))
            return Array.Empty<PrometheusMetric>();

        // Family-level metadata (HELP / TYPE / UNIT) plus accumulated samples.
        // Keyed by FAMILY name (base name, not _bucket/_sum/_count variants).
        var families = new Dictionary<string, FamilyBuilder>(StringComparer.Ordinal);

        var span = body.AsSpan();
        var pos = 0;
        while (pos < span.Length)
        {
            // Find the next line terminator (\r\n or \n) so we tolerate either.
            var lineEnd = pos;
            while (lineEnd < span.Length && span[lineEnd] != '\n')
                lineEnd++;

            // Strip trailing \r if present.
            var endExclusive = lineEnd;
            if (endExclusive > pos && span[endExclusive - 1] == '\r')
                endExclusive--;

            var line = span[pos..endExclusive];
            pos = lineEnd + 1;

            // Skip empty lines.
            if (line.IsWhiteSpace())
                continue;

            if (line[0] == '#')
            {
                TryHandleComment(line, families);
                continue;
            }

            TryHandleSample(line, families);
        }

        if (families.Count == 0)
            return Array.Empty<PrometheusMetric>();

        var result = new List<PrometheusMetric>(families.Count);
        foreach (var fb in families.Values)
            result.Add(fb.Build());
        return result;
    }

    // -------------------- comment dispatch --------------------

    private static void TryHandleComment(ReadOnlySpan<char> line, Dictionary<string, FamilyBuilder> families)
    {
        // Lone "#" or "# anything that isn't HELP/TYPE/UNIT" is treated as a
        // plain comment and ignored.
        if (line.Length < 2) return;

        // Move past the '#' and any whitespace immediately after it.
        var idx = 1;
        while (idx < line.Length && IsAsciiSpace(line[idx])) idx++;
        if (idx >= line.Length) return;

        var rest = line[idx..];

        if (StartsWithWord(rest, "HELP", out var afterHelp))
        {
            ApplyDirective(afterHelp, families, DirectiveKind.Help);
            return;
        }

        if (StartsWithWord(rest, "TYPE", out var afterType))
        {
            ApplyDirective(afterType, families, DirectiveKind.Type);
            return;
        }

        if (StartsWithWord(rest, "UNIT", out var afterUnit))
        {
            ApplyDirective(afterUnit, families, DirectiveKind.Unit);
            return;
        }

        // Any other comment: ignored.
    }

    private enum DirectiveKind { Help, Type, Unit }

    private static void ApplyDirective(ReadOnlySpan<char> after, Dictionary<string, FamilyBuilder> families, DirectiveKind kind)
    {
        // Parse "<name> <rest...>"
        var nameStart = 0;
        while (nameStart < after.Length && IsAsciiSpace(after[nameStart])) nameStart++;
        if (nameStart >= after.Length) return;

        var nameEnd = nameStart;
        while (nameEnd < after.Length && !IsAsciiSpace(after[nameEnd])) nameEnd++;
        if (nameEnd == nameStart) return;

        var name = new string(after[nameStart..nameEnd]);

        var valueStart = nameEnd;
        while (valueStart < after.Length && IsAsciiSpace(after[valueStart])) valueStart++;
        var valueSpan = valueStart < after.Length ? after[valueStart..] : ReadOnlySpan<char>.Empty;

        var fb = GetOrAdd(families, name);

        switch (kind)
        {
            case DirectiveKind.Help:
                fb.Help = valueSpan.IsEmpty ? null : new string(valueSpan);
                break;
            case DirectiveKind.Type:
                fb.Kind = MapKind(valueSpan);
                break;
            case DirectiveKind.Unit:
                fb.Unit = valueSpan.IsEmpty ? null : new string(valueSpan);
                break;
        }
    }

    // -------------------- sample dispatch --------------------

    private static void TryHandleSample(ReadOnlySpan<char> line, Dictionary<string, FamilyBuilder> families)
    {
        // Shape: name{labels} value [timestamp]
        // or:    name value [timestamp]
        // Tolerant of any malformation: if anything fails, return early.

        // 1) Read instrument name.
        var nameEnd = 0;
        while (nameEnd < line.Length && IsNameChar(line[nameEnd])) nameEnd++;
        if (nameEnd == 0) return;

        var sampleName = new string(line[..nameEnd]);
        var rest = line[nameEnd..];

        // 2) Optional label set.
        IReadOnlyDictionary<string, string> labels = EmptyLabels;
        if (rest.Length > 0 && rest[0] == '{')
        {
            // Find closing brace -- mindful of quoted strings.
            var labelsClose = FindLabelsClose(rest);
            if (labelsClose < 0) return; // malformed: unterminated label block

            var labelBody = rest[1..labelsClose];
            if (!TryParseLabels(labelBody, out labels))
                return;

            rest = rest[(labelsClose + 1)..];
        }

        // 3) Skip whitespace, then read value.
        var valStart = 0;
        while (valStart < rest.Length && IsAsciiSpace(rest[valStart])) valStart++;
        if (valStart >= rest.Length) return;

        var afterVal = valStart;
        while (afterVal < rest.Length && !IsAsciiSpace(rest[afterVal])) afterVal++;

        var valueSpan = rest[valStart..afterVal];
        if (!TryParseDouble(valueSpan, out var value))
            return;

        // 4) Optional explicit timestamp.
        long? timestampMs = null;
        var tsStart = afterVal;
        while (tsStart < rest.Length && IsAsciiSpace(rest[tsStart])) tsStart++;
        if (tsStart < rest.Length)
        {
            var tsEnd = tsStart;
            while (tsEnd < rest.Length && !IsAsciiSpace(rest[tsEnd])) tsEnd++;
            var tsSpan = rest[tsStart..tsEnd];
            if (long.TryParse(tsSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
                timestampMs = ts;
        }

        // 5) Map sample name -> family name (strip _bucket / _sum / _count for
        //    histograms / summaries). The family lookup is a "trickle up" --
        //    we look for the BASE name first; if no family yet exists with that
        //    base, we fall back to the sample name itself.
        var familyName = ResolveFamilyName(sampleName, families);
        var fb = GetOrAdd(families, familyName);

        fb.Samples.Add(new PrometheusMetricSample(sampleName, labels, value, timestampMs));
    }

    private static string ResolveFamilyName(string sampleName, Dictionary<string, FamilyBuilder> families)
    {
        // OpenMetrics histograms / summaries declare TYPE under the base name
        // and then emit samples named <base>_bucket / <base>_sum / <base>_count.
        // Roll those into the base family if one already exists; else keep the
        // sample name as the family name.
        foreach (var suffix in HistogramSummarySuffixes)
        {
            if (sampleName.EndsWith(suffix, StringComparison.Ordinal))
            {
                var baseName = sampleName[..^suffix.Length];
                if (families.ContainsKey(baseName))
                    return baseName;
            }
        }
        return sampleName;
    }

    private static readonly string[] HistogramSummarySuffixes = { "_bucket", "_sum", "_count" };

    // -------------------- label parsing --------------------

    private static int FindLabelsClose(ReadOnlySpan<char> rest)
    {
        // rest[0] is '{'. Walk forward, tracking whether we're inside a quoted
        // label value, so an embedded '}' doesn't terminate the block.
        var inQuote = false;
        var escape = false;
        for (var i = 1; i < rest.Length; i++)
        {
            var c = rest[i];
            if (inQuote)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') { inQuote = false; continue; }
                continue;
            }
            if (c == '"') { inQuote = true; continue; }
            if (c == '}') return i;
        }
        return -1;
    }

    private static bool TryParseLabels(ReadOnlySpan<char> body, out IReadOnlyDictionary<string, string> labels)
    {
        // Empty label set "{}".
        if (body.IsWhiteSpace())
        {
            labels = EmptyLabels;
            return true;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < body.Length)
        {
            // Skip whitespace and commas.
            while (i < body.Length && (IsAsciiSpace(body[i]) || body[i] == ',')) i++;
            if (i >= body.Length) break;

            // Read label key.
            var keyStart = i;
            while (i < body.Length && IsNameChar(body[i])) i++;
            if (i == keyStart) { labels = result; return false; }
            var key = new string(body[keyStart..i]);

            // Whitespace, then '='.
            while (i < body.Length && IsAsciiSpace(body[i])) i++;
            if (i >= body.Length || body[i] != '=') { labels = result; return false; }
            i++;
            while (i < body.Length && IsAsciiSpace(body[i])) i++;

            // Quoted value.
            if (i >= body.Length || body[i] != '"') { labels = result; return false; }
            i++;
            var sb = new StringBuilder();
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '\\')
                {
                    if (i + 1 >= body.Length) { labels = result; return false; }
                    var next = body[i + 1];
                    sb.Append(next switch
                    {
                        '\\' => '\\',
                        '"' => '"',
                        'n' => '\n',
                        _ => next,
                    });
                    i += 2;
                    continue;
                }
                if (c == '"') { i++; break; }
                sb.Append(c);
                i++;
            }

            result[key] = sb.ToString();
        }

        labels = result;
        return true;
    }

    // -------------------- helpers --------------------

    private static readonly IReadOnlyDictionary<string, string> EmptyLabels =
        new Dictionary<string, string>(0, StringComparer.Ordinal);

    private static FamilyBuilder GetOrAdd(Dictionary<string, FamilyBuilder> families, string name)
    {
        if (!families.TryGetValue(name, out var fb))
        {
            fb = new FamilyBuilder(name);
            families[name] = fb;
        }
        return fb;
    }

    private static bool StartsWithWord(ReadOnlySpan<char> span, string word, out ReadOnlySpan<char> after)
    {
        if (span.Length < word.Length)
        {
            after = ReadOnlySpan<char>.Empty;
            return false;
        }
        for (var i = 0; i < word.Length; i++)
        {
            if (span[i] != word[i])
            {
                after = ReadOnlySpan<char>.Empty;
                return false;
            }
        }
        // Must be followed by whitespace OR end-of-line; otherwise it's a longer
        // identifier like "HELPER" that just happens to share a prefix.
        if (span.Length == word.Length)
        {
            after = ReadOnlySpan<char>.Empty;
            return true;
        }
        if (!IsAsciiSpace(span[word.Length]))
        {
            after = ReadOnlySpan<char>.Empty;
            return false;
        }
        after = span[(word.Length + 1)..];
        return true;
    }

    private static PrometheusMetricKind MapKind(ReadOnlySpan<char> token)
    {
        // OpenMetrics is case-sensitive (lowercase); be tolerant.
        if (token.Equals("counter", StringComparison.OrdinalIgnoreCase)) return PrometheusMetricKind.Counter;
        if (token.Equals("gauge", StringComparison.OrdinalIgnoreCase)) return PrometheusMetricKind.Gauge;
        if (token.Equals("histogram", StringComparison.OrdinalIgnoreCase)) return PrometheusMetricKind.Histogram;
        if (token.Equals("summary", StringComparison.OrdinalIgnoreCase)) return PrometheusMetricKind.Summary;
        return PrometheusMetricKind.Untyped;
    }

    private static bool IsAsciiSpace(char c) => c == ' ' || c == '\t';

    private static bool IsNameChar(char c)
    {
        // Prometheus instrument / label names: [a-zA-Z_:][a-zA-Z0-9_:]*
        return (c >= 'a' && c <= 'z')
            || (c >= 'A' && c <= 'Z')
            || (c >= '0' && c <= '9')
            || c == '_'
            || c == ':';
    }

    private static bool TryParseDouble(ReadOnlySpan<char> span, out double value)
    {
        // Prometheus allows +Inf, -Inf, NaN literals; double.TryParse handles
        // those when given Float style + InvariantCulture.
        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private sealed class FamilyBuilder
    {
        public FamilyBuilder(string name)
        {
            Name = name;
            Samples = new List<PrometheusMetricSample>();
            Kind = PrometheusMetricKind.Untyped;
        }

        public string Name { get; }
        public PrometheusMetricKind Kind { get; set; }
        public string? Unit { get; set; }
        public string? Help { get; set; }
        public List<PrometheusMetricSample> Samples { get; }

        public PrometheusMetric Build() => new(Name, Kind, Unit, Help, Samples);
    }
}
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.Console;

/// <summary>
///     Offline converter: reads an NDJSON dump of <c>HarvestedBdf</c> entries (the output
///     of <c>/api/v1/bdf/harvest</c>), buckets them by verdict or by self-declared-bot
///     status, and emits one archetype YAML per bucket. The YAML schema matches what
///     <see cref="IdentityArchetypeRegistry"/> consumes: archetype_id / name / archetype_kind
///     plus a dimensions map keyed by <see cref="IdentityVectorLayout"/> slot names.
///
///     For each slot the modal value across the bucket becomes <c>value</c> and the
///     modal-frequency becomes <c>confidence</c>. Slots present in fewer than 30% of
///     the bucket's BDFs are skipped (no claim is better than a noisy one). Buckets
///     below <c>--min-group-size</c> are skipped entirely.
/// </summary>
public static class ArchetypeFromBdfCommand
{
    private const double SlotPresenceMinRatio = 0.30;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a == "-h"))
        {
            PrintUsage();
            return 0;
        }

        // args[0] = exe, args[1] = "archetype-from-bdf", args[2] = input, args[3] = output-dir
        if (args.Length < 4)
        {
            PrintUsage();
            return 1;
        }

        var inputPath = args[2];
        var outputDir = args[3];
        var groupBy = (GetArg(args, "--group-by") ?? "verdict").ToLowerInvariant();
        var minGroupSize = int.TryParse(GetArg(args, "--min-group-size"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var n) ? n : 5;

        if (groupBy is not ("verdict" or "ua-declared"))
        {
            System.Console.Error.WriteLine($"  Invalid --group-by '{groupBy}'. Use 'verdict' or 'ua-declared'.");
            return 1;
        }

        if (!File.Exists(inputPath))
        {
            System.Console.Error.WriteLine($"  Input NDJSON not found: {inputPath}");
            return 1;
        }

        Directory.CreateDirectory(outputDir);

        // Read NDJSON. Bad lines are warned to stderr, never abort the run.
        var entries = new List<ParsedHarvestedBdf>();
        var lineNumber = 0;
        await using (var fs = File.OpenRead(inputPath))
        using (var reader = new StreamReader(fs, Encoding.UTF8))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var doc = JsonDocument.Parse(line);
                    entries.Add(ParsedHarvestedBdf.From(doc.RootElement));
                }
                catch (Exception ex)
                {
                    System.Console.Error.WriteLine($"  WARN: line {lineNumber} malformed: {ex.Message}");
                }
            }
        }

        System.Console.WriteLine($"  Loaded {entries.Count} harvested BDF entries from {inputPath}");

        // Group
        var groups = entries
            .GroupBy(e => GroupKeyFor(e, groupBy))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToList();

        var layout = IdentityVectorLayout.DefaultV1();
        var slotNames = layout.Slots.Select(s => s.Name).ToList();

        var summary = new List<string>();
        var filesWritten = 0;

        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Count < minGroupSize)
            {
                summary.Add($"  [skip] group '{group.Key}' size={members.Count} (<{minGroupSize})");
                continue;
            }

            var (archetypeId, name, description, archetypeKind) = BuildArchetypeMeta(group.Key, groupBy);

            var dimensions = new List<(string Slot, object Value, double Confidence)>();
            foreach (var slot in slotNames)
            {
                // Collect values for this slot across the group. JsonElement is a
                // struct, so the "absent" cases are TryGetValue==false OR ValueKind
                // == Null / Undefined -- skip both.
                var values = new List<object>();
                foreach (var member in members)
                {
                    if (!member.Signals.TryGetValue(slot, out var raw)) continue;
                    if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                    values.Add(raw);
                }

                var presenceRatio = (double)values.Count / members.Count;
                if (presenceRatio < SlotPresenceMinRatio) continue;

                // Modal value: bucket by stable string form so JsonElement equality
                // doesn't matter; keep the FIRST occurrence as the canonical object.
                var bucketed = values
                    .Select(v => new { Key = StableKey(v), Value = v })
                    .GroupBy(x => x.Key)
                    .Select(g => new { Value = g.First().Value, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .First();

                var frequency = (double)bucketed.Count / members.Count;
                dimensions.Add((slot, bucketed.Value, Math.Round(frequency, 4)));
            }

            if (dimensions.Count == 0)
            {
                summary.Add($"  [skip] group '{group.Key}' size={members.Count} (no dimensions met presence ratio)");
                continue;
            }

            var safeId = SanitizeForFilename(archetypeId);
            var outPath = Path.Combine(outputDir, $"{safeId}.yaml");
            await File.WriteAllTextAsync(outPath, RenderYaml(archetypeId, name, description, archetypeKind, dimensions));
            filesWritten++;
            summary.Add($"  [write] {outPath} (group='{group.Key}', size={members.Count}, dims={dimensions.Count})");
        }

        System.Console.WriteLine();
        System.Console.WriteLine($"  Groups considered: {groups.Count}");
        foreach (var line in summary) System.Console.WriteLine(line);
        System.Console.WriteLine();
        System.Console.WriteLine($"  Files written: {filesWritten}");
        return 0;
    }

    private static void PrintUsage()
    {
        System.Console.WriteLine("  Usage:");
        System.Console.WriteLine("    stylobot archetype-from-bdf <input.ndjson> <output-dir>");
        System.Console.WriteLine("        [--group-by verdict|ua-declared]  (default: verdict)");
        System.Console.WriteLine("        [--min-group-size N]              (default: 5)");
        System.Console.WriteLine();
        System.Console.WriteLine("  Reads NDJSON harvested from /api/v1/bdf/harvest and emits one");
        System.Console.WriteLine("  archetype YAML per group into <output-dir>.");
    }

    private static string GroupKeyFor(ParsedHarvestedBdf e, string groupBy)
    {
        var verdict = e.Verdict ?? "unknown";
        if (groupBy == "verdict") return $"verdict-{verdict}";

        // ua-declared:
        //   selfDeclaredBot == true  -> self-declared-bot
        //   selfDeclaredBot == false -> unverified-bot when verdict=bot, otherwise human
        //   selfDeclaredBot == null  -> bucket as `verdict-{verdict}` (no UA evidence either way)
        if (e.SelfDeclaredBot == true) return "self-declared-bot";
        if (e.SelfDeclaredBot == false)
            return verdict == "bot" ? "unverified-bot" : "human";
        return $"verdict-{verdict}";
    }

    private static (string archetypeId, string name, string description, string kind) BuildArchetypeMeta(
        string groupKey, string groupBy)
    {
        // Bot-side group keys we emit.
        var isBotSide =
            groupKey == "verdict-bot" ||
            groupKey == "self-declared-bot" ||
            groupKey == "unverified-bot";

        var kind = isBotSide ? "verified-bot" : "human-browser";

        var name = groupKey switch
        {
            "verdict-bot" => "Verdict: Bot",
            "verdict-human" => "Verdict: Human",
            "self-declared-bot" => "Self-declared Bot",
            "unverified-bot" => "Unverified Bot",
            "human" => "Human",
            _ => groupKey,
        };

        var description = groupBy == "verdict"
            ? $"Auto-generated from BDF harvest, grouped by detection verdict ({groupKey})."
            : $"Auto-generated from BDF harvest, grouped by UA self-declaration ({groupKey}).";

        return (groupKey, name, description, kind);
    }

    private static string SanitizeForFilename(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
            else sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private static string StableKey(object v)
    {
        if (v is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => "s:" + el.GetString(),
                JsonValueKind.Number => "n:" + el.GetRawText(),
                JsonValueKind.True => "b:true",
                JsonValueKind.False => "b:false",
                JsonValueKind.Null => "null",
                _ => el.GetRawText(),
            };
        }
        return v.GetType().Name + ":" + (v.ToString() ?? string.Empty);
    }

    private static string RenderYaml(
        string archetypeId,
        string name,
        string description,
        string kind,
        List<(string Slot, object Value, double Confidence)> dimensions)
    {
        // Hand-rolled minimal YAML. Quoting strategy mirrors the existing
        // chrome-desktop.yaml: strings double-quoted, scalars bare.
        var sb = new StringBuilder();
        sb.Append("archetype_id: ").AppendLine(YamlScalar(archetypeId));
        sb.Append("name: ").AppendLine(YamlScalar(name));
        sb.Append("description: ").AppendLine(YamlScalar(description));
        sb.Append("archetype_kind: ").AppendLine(YamlScalar(kind));
        sb.AppendLine("dimensions:");
        foreach (var (slot, value, confidence) in dimensions)
        {
            sb.Append("  ").Append(slot).AppendLine(":");
            sb.Append("    value: ").AppendLine(YamlValue(value));
            sb.Append("    confidence: ")
                .AppendLine(confidence.ToString("0.####", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string YamlScalar(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        // Always quote strings to avoid YAML parsing surprises (colons, special chars).
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string YamlValue(object v)
    {
        if (v is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => YamlScalar(el.GetString() ?? string.Empty),
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                _ => YamlScalar(el.GetRawText()),
            };
        }
        return v switch
        {
            string s => YamlScalar(s),
            bool b => b ? "true" : "false",
            null => "null",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => YamlScalar(v.ToString() ?? string.Empty),
        };
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    /// <summary>
    ///     One NDJSON line, parsed just enough for grouping + slot extraction.
    ///     Keeps the BDF schema decoupled from the UI assembly: we read raw
    ///     <see cref="JsonElement"/> for signal values and let the YAML writer
    ///     handle the formatting.
    /// </summary>
    private sealed record ParsedHarvestedBdf(
        string? Verdict,
        bool? SelfDeclaredBot,
        Dictionary<string, JsonElement> Signals)
    {
        public static ParsedHarvestedBdf From(JsonElement root)
        {
            string? verdict = null;
            bool? selfDeclared = null;
            var signals = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            if (root.TryGetProperty("Tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
            {
                if (tagsEl.TryGetProperty("verdict", out var vEl) && vEl.ValueKind == JsonValueKind.String)
                    verdict = vEl.GetString();
                if (tagsEl.TryGetProperty("selfDeclaredBot", out var sdEl))
                {
                    if (sdEl.ValueKind == JsonValueKind.True) selfDeclared = true;
                    else if (sdEl.ValueKind == JsonValueKind.False) selfDeclared = false;
                }
            }

            if (root.TryGetProperty("Bdf", out var bdfEl) &&
                bdfEl.ValueKind == JsonValueKind.Object &&
                bdfEl.TryGetProperty("ImportantSignals", out var sigEl) &&
                sigEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in sigEl.EnumerateObject())
                    signals[prop.Name] = prop.Value.Clone();
            }

            return new ParsedHarvestedBdf(verdict, selfDeclared, signals);
        }
    }
}
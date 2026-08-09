using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     <c>stylobot dump</c> — export full detection detail from the local SQLite store.
///
///     <para>
///         Reads the <c>detections</c> table DIRECTLY, the same way <c>stylobot clear</c>
///         does, rather than booting a host. That keeps it usable inside the docker image
///         and on a box where the daemon is stopped, and means an export can never contend
///         with a running gateway for the detection pipeline.
///     </para>
///
///     <para>
///         Streams row-by-row through a <see cref="SqliteDataReader"/> and writes one JSON
///         object per line. Nothing accumulates in memory, so dumping a multi-million-row
///         table costs a constant footprint and can be piped straight into <c>jq</c>,
///         <c>gzip</c>, or a bulk loader.
///     </para>
/// </summary>
public static class DumpCommand
{
    // Every column the dashboard write path persists — this is the "full detail".
    private const string Columns =
        "timestamp, signature, method, path, is_bot, bot_probability, confidence, risk_band, " +
        "bot_name, bot_type, action, country_code, processing_time_ms, threat_score, threat_band, " +
        "status_code, user_agent_raw, risk_justification, domain, host, referrer_host, " +
        "ua_device_class, response_bytes, is_verified_bot, upstream_status_code, importance_weight";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        var dbPath = Arg(args, "--db")
            ?? Environment.GetEnvironmentVariable("STYLOBOT_DatabasePath")
            ?? Path.Combine(
                Mostlylucid.BotDetection.Models.BotDetectionOptions.ResolveDataDirectory(),
                "botdetection.db");

        if (!File.Exists(dbPath))
        {
            await System.Console.Error.WriteLineAsync($"No database found at: {dbPath}");
            await System.Console.Error.WriteLineAsync(
                "Nothing to dump. Point at one with --db <path>, or set STYLOBOT_DatabasePath.");
            return 1;
        }

        var format = (Arg(args, "--format") ?? "jsonl").ToLowerInvariant();
        if (format is not ("jsonl" or "csv"))
        {
            await System.Console.Error.WriteLineAsync($"Unknown --format '{format}'. Use jsonl or csv.");
            return 2;
        }

        // ---- Filters -------------------------------------------------------
        var where = new List<string>();
        var ps = new List<SqliteParameter>();

        var sig = Arg(args, "--fingerprint") ?? Arg(args, "--signature");
        if (!string.IsNullOrWhiteSpace(sig))
        {
            where.Add("signature = @sig");
            ps.Add(new SqliteParameter("@sig", sig));
        }

        if (TryScore(args, "--min-score", out var minScore))
        {
            where.Add("bot_probability >= @minScore");
            ps.Add(new SqliteParameter("@minScore", minScore));
        }

        if (TryScore(args, "--max-score", out var maxScore))
        {
            where.Add("bot_probability <= @maxScore");
            ps.Add(new SqliteParameter("@maxScore", maxScore));
        }

        if (ParseWhen(Arg(args, "--since")) is { } since)
        {
            where.Add("timestamp >= @since");
            ps.Add(new SqliteParameter("@since", since.ToString("O")));
        }

        if (ParseWhen(Arg(args, "--until")) is { } until)
        {
            where.Add("timestamp <= @until");
            ps.Add(new SqliteParameter("@until", until.ToString("O")));
        }

        if (args.Contains("--bots-only")) where.Add("is_bot = 1");
        if (args.Contains("--humans-only")) where.Add("is_bot = 0");

        var sql = new StringBuilder($"SELECT {Columns} FROM detections");
        if (where.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        sql.Append(" ORDER BY timestamp DESC");

        if (int.TryParse(Arg(args, "--limit"), out var limit) && limit > 0)
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {limit}");

        // ---- Output --------------------------------------------------------
        var outPath = Arg(args, "--out");
        await using var outStream = outPath is null
            ? System.Console.OpenStandardOutput()
            : File.Create(outPath);
        await using var writer = new StreamWriter(outStream, new UTF8Encoding(false));

        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        foreach (var p in ps) cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync();

        var names = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++) names[i] = reader.GetName(i);

        if (format == "csv")
            await writer.WriteLineAsync(string.Join(",", names));

        var rows = 0L;
        while (await reader.ReadAsync())
        {
            if (format == "csv")
            {
                var cells = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    cells[i] = CsvCell(reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString());
                await writer.WriteLineAsync(string.Join(",", cells));
            }
            else
            {
                // One self-contained JSON object per line: append-safe, streamable,
                // and every line parses independently if the dump is truncated.
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer))
                {
                    json.WriteStartObject();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (reader.IsDBNull(i)) { json.WriteNull(names[i]); continue; }
                        switch (reader.GetValue(i))
                        {
                            case long l:   json.WriteNumber(names[i], l); break;
                            case double d: json.WriteNumber(names[i], d); break;
                            case var v:    json.WriteString(names[i], v?.ToString()); break;
                        }
                    }
                    json.WriteEndObject();
                }
                await writer.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray()));
            }
            rows++;
        }

        await writer.FlushAsync();

        // Progress goes to stderr so `stylobot dump > file.jsonl` stays pure data.
        await System.Console.Error.WriteLineAsync($"dumped {rows} detection(s) from {dbPath}");
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
    }

    private static bool TryScore(string[] args, string name, out double value)
    {
        value = 0;
        var raw = Arg(args, name);
        return raw is not null
               && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Accepts a relative duration (90m / 24h / 7d) or any parseable date.</summary>
    private static DateTime? ParseWhen(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var span = raw.AsSpan();
        var unit = span[^1];
        if ("mhd".Contains(char.ToLowerInvariant(unit))
            && double.TryParse(span[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            return char.ToLowerInvariant(unit) switch
            {
                'm' => DateTime.UtcNow.AddMinutes(-n),
                'h' => DateTime.UtcNow.AddHours(-n),
                _   => DateTime.UtcNow.AddDays(-n),
            };
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var abs)
            ? abs
            : null;
    }

    private static string CsvCell(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\""
            : v;
    }

    private static void PrintHelp()
    {
        System.Console.WriteLine();
        System.Console.WriteLine("  stylobot dump — export full detection detail");
        System.Console.WriteLine();
        System.Console.WriteLine("  Usage:");
        System.Console.WriteLine("    stylobot dump [options] > detections.jsonl");
        System.Console.WriteLine();
        System.Console.WriteLine("  Options:");
        System.Console.WriteLine("    --fingerprint <sig>   Only this signature/fingerprint (alias: --signature)");
        System.Console.WriteLine("    --min-score <0..1>    Minimum bot_probability");
        System.Console.WriteLine("    --max-score <0..1>    Maximum bot_probability");
        System.Console.WriteLine("    --since <when>        90m / 24h / 7d, or a date");
        System.Console.WriteLine("    --until <when>        Same formats");
        System.Console.WriteLine("    --bots-only           is_bot = 1");
        System.Console.WriteLine("    --humans-only         is_bot = 0");
        System.Console.WriteLine("    --limit <n>           Cap the number of rows");
        System.Console.WriteLine("    --format <jsonl|csv>  Default jsonl (one JSON object per line)");
        System.Console.WriteLine("    --out <file>          Default stdout");
        System.Console.WriteLine("    --db <path>           Override the database location");
        System.Console.WriteLine();
        System.Console.WriteLine("  Examples:");
        System.Console.WriteLine("    stylobot dump --since 24h --bots-only > bots.jsonl");
        System.Console.WriteLine("    stylobot dump --min-score 0.9 --since 7d --format csv --out high.csv");
        System.Console.WriteLine("    stylobot dump --fingerprint a1b2c3 | jq .path");
        System.Console.WriteLine();
        System.Console.WriteLine("  Rows stream one-per-line; progress goes to stderr so stdout stays pure data.");
        System.Console.WriteLine();
    }
}

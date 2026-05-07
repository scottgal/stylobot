using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Console.Services;

public sealed record DetectionEntry(
    DateTime Timestamp,
    string Path,
    double BotProbability,
    string Verdict,
    string TopDetector,
    string? BotName,
    string? ActionPolicy,
    string? Country,
    double DetectionTimeMs,
    int DetectorCount);

public sealed class DetectionEventSink
{
    private readonly Channel<DetectionEntry> _channel = Channel.CreateBounded<DetectionEntry>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<DetectionEntry> Reader => _channel.Reader;
    public void Write(DetectionEntry entry) => _channel.Writer.TryWrite(entry);
}

public sealed class DetectionTapMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DetectionEventSink _sink;

    public DetectionTapMiddleware(RequestDelegate next, DetectionEventSink sink)
    {
        _next = next;
        _sink = sink;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try { await _next(context); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("response has already started", StringComparison.OrdinalIgnoreCase)) { }

        if (context.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evidenceObj)
            && evidenceObj is AggregatedEvidence evidence)
        {
            var isBot = evidence.BotProbability >= 0.5;
            var topDetector = evidence.Contributions?.LastOrDefault()?.DetectorName ?? "-";
            var country = evidence.Signals?.TryGetValue("geo.country_code", out var cc) == true
                ? cc?.ToString() : null;

            _sink.Write(new DetectionEntry(
                DateTime.Now,
                context.Request.Path.Value ?? "/",
                evidence.BotProbability,
                isBot ? "BOT" : "HUMAN",
                topDetector,
                evidence.PrimaryBotName,
                evidence.TriggeredActionPolicyName,
                country,
                evidence.TotalProcessingTimeMs,
                evidence.ContributingDetectors?.Count ?? 0));
        }
    }
}

/// <summary>
///     Full-screen terminal dashboard — raw ANSI, no reflection, AOT-safe.
///     Renders at 2 Hz; each frame does SetCursorPosition(0,0) and overwrites in-place.
/// </summary>
public sealed class LiveDetectionTableService : BackgroundService
{
    // ── Config ────────────────────────────────────────────────────────────────
    private readonly DetectionEventSink _sink;
    private readonly string _mode;
    private readonly string _upstream;
    private readonly string _port;
    private readonly string _policy;
    private readonly bool _useTls;
    private readonly bool _tunnelEnabled;
    private readonly Func<string?>? _tunnelUrlGetter;
    private readonly int _maxFeedRows;

    // ── Counters ──────────────────────────────────────────────────────────────
    private int _totalRequests;
    private int _totalBots;
    private int _totalHumans;
    private int _totalThreats;
    private double _totalDetectionTimeMs;
    private double _maxDetectionTimeMs;
    private readonly DateTime _startTime = DateTime.Now;

    // ── Throughput ────────────────────────────────────────────────────────────
    private readonly ConcurrentQueue<DateTime> _recentRequests = new();   // 10s window
    private readonly ConcurrentQueue<DateTime> _sparkHistory = new();     // 60s window

    // ── Aggregates ────────────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, int> _endpointHits = new();
    private readonly ConcurrentDictionary<string, int> _botSignatures = new();

    public LiveDetectionTableService(
        DetectionEventSink sink,
        string mode, string upstream, string port, string policy,
        bool useTls, bool tunnelEnabled,
        Func<string?>? tunnelUrlGetter = null, int maxFeedRows = 20)
    {
        _sink = sink;
        _mode = mode;
        _upstream = upstream;
        _port = port;
        _policy = policy;
        _useTls = useTls;
        _tunnelEnabled = tunnelEnabled;
        _tunnelUrlGetter = tunnelUrlGetter;
        _maxFeedRows = maxFeedRows;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1200, stoppingToken);

        var entries = new LinkedList<DetectionEntry>();
        var scheme = _useTls ? "https" : "http";

        System.Console.CursorVisible = false;
        System.Console.Clear();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (_sink.Reader.TryRead(out var entry))
                    Ingest(entry, entries);

                TrimWindows();
                RenderFrame(entries, scheme);

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(500);
                    await _sink.Reader.WaitToReadAsync(cts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            }
        }
        finally
        {
            System.Console.CursorVisible = true;
            System.Console.ResetColor();
        }
    }

    private void Ingest(DetectionEntry entry, LinkedList<DetectionEntry> entries)
    {
        entries.AddFirst(entry);
        _totalRequests++;
        _totalDetectionTimeMs += entry.DetectionTimeMs;
        if (entry.DetectionTimeMs > _maxDetectionTimeMs)
            _maxDetectionTimeMs = entry.DetectionTimeMs;

        if (entry.Verdict == "BOT")
        {
            _totalBots++;
            if (entry.BotProbability >= 0.8) _totalThreats++;
            var sig = entry.BotName ?? entry.TopDetector;
            _botSignatures.AddOrUpdate(sig, 1, (_, c) => c + 1);
        }
        else
        {
            _totalHumans++;
        }

        var path = entry.Path.Split('?')[0];
        _endpointHits.AddOrUpdate(path, 1, (_, c) => c + 1);
        _recentRequests.Enqueue(DateTime.Now);
        _sparkHistory.Enqueue(DateTime.Now);

        if (_endpointHits.Count > 500) Trim(_endpointHits, 100);
        if (_botSignatures.Count > 200) Trim(_botSignatures, 50);
        while (entries.Count > _maxFeedRows) entries.RemoveLast();
    }

    private void TrimWindows()
    {
        var now = DateTime.Now;
        var c10 = now.AddSeconds(-10);
        var c60 = now.AddSeconds(-60);
        while (_recentRequests.TryPeek(out var t) && t < c10) _recentRequests.TryDequeue(out _);
        while (_sparkHistory.TryPeek(out var t) && t < c60) _sparkHistory.TryDequeue(out _);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Renderer
    // ─────────────────────────────────────────────────────────────────────────

    private void RenderFrame(LinkedList<DetectionEntry> entries, string scheme)
    {
        var w = System.Console.WindowWidth;
        var h = System.Console.WindowHeight;
        if (w < 60 || h < 10) return;  // too small to render

        var sb = new StringBuilder(w * h * 4);

        // Position at top-left without clearing (avoids flicker)
        sb.Append(Ansi.Home);

        var uptime = DateTime.Now - _startTime;
        var reqPerSec = _recentRequests.Count / 10.0;
        var avgMs = _totalRequests > 0 ? _totalDetectionTimeMs / _totalRequests : 0;
        var tunnelUrl = _tunnelEnabled ? _tunnelUrlGetter?.Invoke() : null;

        // ── Row 0: Title bar ──────────────────────────────────────────────────
        var modeLabel = ModeLabel(_mode);
        var titleBar = $" stylobot  {modeLabel}  \u2192 {Truncate(_upstream, 30)}  \u23f1 {FormatUptime(uptime)}  {_totalRequests} req  {reqPerSec:F1}/s ";
        sb.Append(Ansi.BgBlue + Ansi.BrightWhite + Ansi.Bold);
        sb.Append(PadRight(titleBar, w));
        sb.Append(Ansi.Reset);
        sb.Append('\n');

        // ── Row 1: Stats bar ──────────────────────────────────────────────────
        var spark = BuildSparkline(Math.Min(30, w / 3));
        var statsBar = BuildStatsBar(avgMs, spark, w);
        sb.Append(statsBar);
        sb.Append('\n');

        // ── Body: feed (left) + sidebar (right) ──────────────────────────────
        // Sidebar is 28 chars wide on wide terminals; stacked on narrow
        var wide = w >= 100;
        var sidebarW = wide ? 29 : 0;
        var feedW = wide ? w - sidebarW - 1 : w;

        // Feed rows
        var headerRow = wide ? 3 : 3;  // rows consumed so far
        var footerRows = 2;
        var availableBodyRows = h - headerRow - footerRows;
        var feedBodyRows = availableBodyRows;

        var sideLines = wide ? BuildSidebar(sidebarW, feedBodyRows, scheme, tunnelUrl) : Array.Empty<string>();
        var feedLines = BuildFeedPanel(entries, feedW, feedBodyRows);

        for (var r = 0; r < feedBodyRows; r++)
        {
            var feedLine = r < feedLines.Length ? feedLines[r] : PadRight("", feedW);
            sb.Append(feedLine);
            if (wide)
            {
                sb.Append(r == 0 ? Ansi.Dim + "\u2502" + Ansi.Reset : Ansi.Dim + "\u2502" + Ansi.Reset);
                var sideLine = r < sideLines.Length ? sideLines[r] : PadRight("", sidebarW - 1);
                sb.Append(sideLine);
            }
            sb.Append(ClearToEndOfLine());
            sb.Append('\n');
        }

        // ── Footer ────────────────────────────────────────────────────────────
        var footerContent = BuildFooter(w);
        sb.Append(footerContent);
        sb.Append(ClearToEndOfLine());
        sb.Append('\n');

        // Blank the last line (keep cursor off content)
        sb.Append(ClearToEndOfLine());

        // Write whole frame atomically
        System.Console.SetCursorPosition(0, 0);
        System.Console.Write(sb.ToString());
    }

    // ── Feed panel ────────────────────────────────────────────────────────────

    private string[] BuildFeedPanel(LinkedList<DetectionEntry> entries, int width, int totalRows)
    {
        var lines = new string[totalRows];
        var border = Ansi.Dim + "\u250c" + new string('\u2500', width - 2) + "\u2510" + Ansi.Reset;
        var bottomBorder = Ansi.Dim + "\u2514" + new string('\u2500', width - 2) + "\u2518" + Ansi.Reset;
        var title = " Recent Requests ";
        var titleLine = Ansi.Dim + "\u250c\u2500" + Ansi.Reset + Ansi.Bold + title + Ansi.Reset
                      + Ansi.Dim + new string('\u2500', Math.Max(0, width - 2 - title.Length)) + "\u2510" + Ansi.Reset;

        lines[0] = VisualPad(titleLine, width);

        // Column widths
        var timeW = 8;
        var msW = 5;
        var probW = 5;
        var actionW = 8;
        var pathW = width - timeW - msW - probW - actionW - 7; // 7 = separators + padding

        // Header
        if (totalRows > 2)
        {
            lines[1] = Ansi.Dim
                + "\u2502 " + Ansi.Reset + PadRight("Time", timeW) + Ansi.Dim + "  "
                + PadRight("Path", pathW) + "  "
                + PadLeft("Bot%", probW) + "  "
                + PadRight("Action", actionW) + "  "
                + PadLeft("ms", msW) + " "
                + "\u2502" + Ansi.Reset;
        }

        var idx = 2;
        var entryList = entries.Take(totalRows - 3).ToArray();
        foreach (var e in entryList)
        {
            if (idx >= totalRows - 1) break;
            lines[idx++] = FormatFeedRow(e, width, timeW, pathW, probW, actionW, msW);
        }

        // Fill empty rows
        while (idx < totalRows - 1)
        {
            if (idx == 2 && entries.Count == 0)
            {
                lines[idx++] = Ansi.Dim + "\u2502" + Ansi.Reset
                    + PadRight("  Waiting for requests...", width - 2)
                    + Ansi.Dim + "\u2502" + Ansi.Reset;
            }
            else
            {
                lines[idx++] = Ansi.Dim + "\u2502" + Ansi.Reset
                    + PadRight("", width - 2)
                    + Ansi.Dim + "\u2502" + Ansi.Reset;
            }
        }

        lines[totalRows - 1] = VisualPad(bottomBorder, width);
        return lines;
    }

    private static string FormatFeedRow(DetectionEntry e, int width, int timeW, int pathW, int probW, int actionW, int msW)
    {
        var path = Truncate(e.Path.Split('?')[0], pathW);
        var prob = e.BotProbability;
        var probStr = $"{prob * 100:F0}%";
        var timeColor = e.DetectionTimeMs > 200 ? Ansi.Red : e.DetectionTimeMs > 50 ? Ansi.Yellow : Ansi.Dim;

        string probColor;
        if (prob >= 0.9)       probColor = Ansi.Bold + Ansi.Red;
        else if (prob >= 0.7)  probColor = Ansi.Red;
        else if (prob >= 0.4)  probColor = Ansi.Yellow;
        else                   probColor = Ansi.Green;

        var action = FormatAction(e);

        return Ansi.Dim + "\u2502 " + Ansi.Reset
            + Ansi.Dim + PadRight(e.Timestamp.ToString("HH:mm:ss"), timeW) + Ansi.Reset
            + "  " + PadRight(path, pathW)
            + "  " + probColor + PadLeft(probStr, probW) + Ansi.Reset
            + "  " + action + PadRight("", Math.Max(0, actionW - VisualLength(action))) + Ansi.Reset
            + "  " + timeColor + PadLeft($"{e.DetectionTimeMs:F0}", msW) + Ansi.Reset
            + " " + Ansi.Dim + "\u2502" + Ansi.Reset;
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private string[] BuildSidebar(int width, int totalRows, string scheme, string? tunnelUrl)
    {
        var lines = new List<string>(totalRows);
        var inner = width - 2;

        void AddSection(string title, IEnumerable<string> rows, int maxRows)
        {
            lines.Add(SectionHeader(title, inner));
            var count = 0;
            foreach (var r in rows)
            {
                if (count++ >= maxRows) break;
                lines.Add(" " + Truncate(r, inner - 1));
            }
        }

        // Top bots
        AddSection(" Top Bots ", TopBotRows(), 5);

        // Separator
        lines.Add(Ansi.Dim + "\u251c" + new string('\u2500', inner) + "\u2524" + Ansi.Reset);

        // Top endpoints
        AddSection(" Endpoints ", TopEndpointRows(), 5);

        // Separator
        lines.Add(Ansi.Dim + "\u251c" + new string('\u2500', inner) + "\u2524" + Ansi.Reset);

        // Config
        AddSection(" Config ", ConfigRows(scheme, tunnelUrl), 4);

        // Fill remaining with empty lines
        while (lines.Count < totalRows)
            lines.Add(PadRight("", inner));

        return lines.Take(totalRows).Select(l => VisualPad(l, width)).ToArray();
    }

    private IEnumerable<string> TopBotRows()
    {
        var bots = _botSignatures.OrderByDescending(kv => kv.Value).Take(6).ToList();
        if (bots.Count == 0)
        {
            yield return Ansi.Dim + "  none yet" + Ansi.Reset;
            yield break;
        }
        foreach (var (sig, count) in bots)
        {
            var name = Truncate(sig, 18);
            var countStr = count.ToString();
            yield return Ansi.Red + " \u25a0 " + Ansi.Reset + PadRight(name, 18) + " " + Ansi.Bold + PadLeft(countStr, 4) + Ansi.Reset;
        }
    }

    private IEnumerable<string> TopEndpointRows()
    {
        var eps = _endpointHits.OrderByDescending(kv => kv.Value).Take(6).ToList();
        if (eps.Count == 0)
        {
            yield return Ansi.Dim + "  none yet" + Ansi.Reset;
            yield break;
        }
        foreach (var (ep, count) in eps)
        {
            var path = Truncate(ep, 18);
            var countStr = count.ToString();
            yield return Ansi.Dim + " \u2192 " + Ansi.Reset + PadRight(path, 18) + " " + Ansi.Bold + PadLeft(countStr, 4) + Ansi.Reset;
        }
    }

    private IEnumerable<string> ConfigRows(string scheme, string? tunnelUrl)
    {
        yield return Ansi.Dim + "  Listen  " + Ansi.Reset + $"{scheme}://:{_port}";
        yield return Ansi.Dim + "  Policy  " + Ansi.Reset + PolicyLabel(_policy);
        if (_tunnelEnabled)
        {
            yield return Ansi.Dim + "  Tunnel  " + Ansi.Reset
                + (tunnelUrl != null
                    ? Ansi.Green + Truncate(tunnelUrl.Replace("https://", ""), 20) + Ansi.Reset
                    : Ansi.Yellow + "connecting\u2026" + Ansi.Reset);
        }
    }

    // ── Stats bar ─────────────────────────────────────────────────────────────

    private string BuildStatsBar(double avgMs, string spark, int width)
    {
        var threatColor = _totalThreats > 0 ? Ansi.Red : Ansi.Dim;

        var parts = new[]
        {
            Ansi.Green  + $" \u2713 {_totalHumans} ok" + Ansi.Reset,
            Ansi.Red    + $" \u2717 {_totalBots} bots" + Ansi.Reset,
            threatColor + $" \u26a0 {_totalThreats} threats" + Ansi.Reset,
            Ansi.Dim    + $"  avg {avgMs:F1}ms  max {_maxDetectionTimeMs:F1}ms  " + Ansi.Reset,
            Ansi.Blue   + spark + Ansi.Reset
        };

        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p);

        // Pad to width (hard: our string contains escape codes)
        return PadAnsiRight(sb.ToString(), width);
    }

    // ── Sparkline ─────────────────────────────────────────────────────────────

    private string BuildSparkline(int buckets)
    {
        const double windowSeconds = 60.0;
        var bucketSecs = windowSeconds / buckets;
        var now = DateTime.Now;
        var counts = new int[buckets];

        foreach (var t in _sparkHistory)
        {
            var age = (now - t).TotalSeconds;
            if (age < 0 || age >= windowSeconds) continue;
            var idx = buckets - 1 - (int)(age / bucketSecs);
            if (idx >= 0 && idx < buckets) counts[idx]++;
        }

        var max = 1;
        foreach (var c in counts) if (c > max) max = c;

        var bars = "\u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588";
        var sb = new StringBuilder(buckets + 2);
        sb.Append('\u258f');  // left thin bar as axis
        foreach (var c in counts)
        {
            var idx = (int)((double)c / max * (bars.Length - 1));
            sb.Append(bars[idx]);
        }
        return sb.ToString();
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private string BuildFooter(int width)
    {
        var hint = _totalThreats >= 3 && _policy.Equals("logonly", StringComparison.OrdinalIgnoreCase)
            ? Ansi.Yellow + $"  \u26a0  {_totalThreats} threats — run with --policy block to enable blocking" + Ansi.Reset
            : Ansi.Dim + "  Ctrl+C to stop  \u2502  --verbose for full logs" + Ansi.Reset;
        return hint;
    }

    // ── ANSI helpers ──────────────────────────────────────────────────────────

    private static class Ansi
    {
        public const string Reset   = "\x1b[0m";
        public const string Bold    = "\x1b[1m";
        public const string Dim     = "\x1b[2m";
        public const string Red     = "\x1b[31m";
        public const string Green   = "\x1b[32m";
        public const string Yellow  = "\x1b[33m";
        public const string Blue    = "\x1b[34m";
        public const string BgBlue  = "\x1b[44m";
        public const string BrightWhite = "\x1b[97m";
        public const string Home    = "\x1b[H";
    }

    private static string ClearToEndOfLine() => "\x1b[K";

    private static string SectionHeader(string title, int innerW)
    {
        var padding = Math.Max(0, innerW - title.Length);
        return Ansi.Dim + "\u2502" + Ansi.Reset + Ansi.Bold + title + Ansi.Reset
             + Ansi.Dim + new string('\u2500', padding) + "\u2502" + Ansi.Reset;
    }

    private static string FormatAction(DetectionEntry e)
    {
        if (e.Verdict == "HUMAN") return Ansi.Green + "Allow" + Ansi.Reset;
        return (e.ActionPolicy ?? "").ToLowerInvariant() switch
        {
            "block" or "block-hard" or "block-soft" => Ansi.Bold + Ansi.Red + "BLOCK" + Ansi.Reset,
            "challenge" or "challenge-pow" or "challenge-js" => Ansi.Yellow + "Chall" + Ansi.Reset,
            "throttle" or "throttle-stealth" or "throttle-aggressive" => Ansi.Yellow + "Throt" + Ansi.Reset,
            "logonly" or "shadow" or "debug" => Ansi.Dim + "Watch" + Ansi.Reset,
            _ => e.BotProbability >= 0.5 ? Ansi.Dim + "Watch" + Ansi.Reset : Ansi.Green + "Allow" + Ansi.Reset
        };
    }

    private static string ModeLabel(string mode) => mode.ToLowerInvariant() switch
    {
        "demo"       => "OBSERVE",
        "production" => Ansi.Green + "ACTIVE" + Ansi.BrightWhite,
        "learning"   => Ansi.Yellow + "LEARNING" + Ansi.BrightWhite,
        _            => mode.ToUpperInvariant()
    };

    private static string PolicyLabel(string policy) => policy.ToLowerInvariant() switch
    {
        "block"    => Ansi.Red + "Block" + Ansi.Reset,
        "throttle" or "throttle-stealth" => Ansi.Yellow + "Throttle" + Ansi.Reset,
        "challenge" => Ansi.Yellow + "Challenge" + Ansi.Reset,
        "logonly"  => Ansi.Dim + "Observe only" + Ansi.Reset,
        _          => policy
    };

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{ts.Hours}h{ts.Minutes:D2}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m{ts.Seconds:D2}s";
        return $"{ts.Seconds}s";
    }

    // ── String padding helpers (ANSI-aware visual length) ─────────────────────

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 1)] + "\u2026";

    private static string PadRight(string s, int width) =>
        s.Length >= width ? s : s + new string(' ', width - s.Length);

    private static string PadLeft(string s, int width) =>
        s.Length >= width ? s : new string(' ', width - s.Length) + s;

    // Visual pad: appends spaces to reach `width` visible chars (ignores escape codes)
    private static string VisualPad(string s, int width)
    {
        var vis = VisualLength(s);
        return vis >= width ? s : s + new string(' ', width - vis);
    }

    // Pad an ANSI-colored string to visual width, then add reset
    private static string PadAnsiRight(string s, int width)
    {
        var vis = VisualLength(s);
        var pad = Math.Max(0, width - vis);
        return s + new string(' ', pad) + Ansi.Reset;
    }

    private static int VisualLength(string s)
    {
        var len = 0;
        var inEsc = false;
        foreach (var c in s)
        {
            if (c == '\x1b') { inEsc = true; continue; }
            if (inEsc) { if (c == 'm') inEsc = false; continue; }
            len++;
        }
        return len;
    }

    private static void Trim(ConcurrentDictionary<string, int> dict, int keepTop)
    {
        var toKeep = dict.OrderByDescending(kv => kv.Value).Take(keepTop).Select(kv => kv.Key).ToHashSet();
        foreach (var key in dict.Keys)
            if (!toKeep.Contains(key))
                dict.TryRemove(key, out _);
    }
}

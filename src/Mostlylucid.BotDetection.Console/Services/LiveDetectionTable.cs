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
    int DetectorCount,
    string? PrimarySignature,
    RiskBand RiskBand,
    ThreatBand ThreatBand,
    double ThreatScore);

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
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("response has already started", StringComparison.OrdinalIgnoreCase)) { }

        if (context.Items.TryGetValue("BotDetection.AggregatedEvidence", out var obj)
            && obj is AggregatedEvidence ev)
        {
            var isBot = ev.BotProbability >= 0.5;
            var detector = ev.Contributions?.LastOrDefault()?.DetectorName ?? "-";
            var country = ev.Signals?.TryGetValue("geo.country_code", out var cc) == true
                ? cc?.ToString() : null;
            var primarySig = ev.Signals?.TryGetValue("signature.primary", out var sig) == true
                ? sig?.ToString() : null;

            _sink.Write(new DetectionEntry(
                DateTime.Now,
                context.Request.Path.Value ?? "/",
                ev.BotProbability,
                isBot ? "BOT" : "HUMAN",
                detector,
                ev.PrimaryBotName,
                ev.TriggeredActionPolicyName,
                country,
                ev.TotalProcessingTimeMs,
                ev.ContributingDetectors?.Count ?? 0,
                primarySig,
                ev.RiskBand,
                ev.ThreatBand,
                ev.ThreatScore));
        }
    }
}

/// <summary>
///     Full-screen TUI dashboard.
///     Uses the VT100 alternate screen buffer so the frame is written in-place
///     without scrolling, and the original terminal content is restored on exit.
/// </summary>
public sealed class LiveDetectionTableService : BackgroundService
{
    private readonly DetectionEventSink _sink;
    private readonly string _mode;
    private readonly string _upstream;
    private readonly string _port;
    private readonly string _policy;
    private readonly bool _useTls;
    private readonly bool _tunnelEnabled;
    private readonly Func<string?>? _tunnelUrlGetter;

    private int _totalRequests;
    private double _totalDetectionTimeMs;
    private double _maxDetectionTimeMs;
    private readonly DateTime _startTime = DateTime.Now;

    private readonly ConcurrentQueue<DateTime> _recentRequests = new();
    private readonly ConcurrentQueue<DateTime> _sparkHistory = new();
    private readonly ConcurrentDictionary<string, int> _endpointHits = new();

    /// <summary>
    ///     Per-fingerprint state. Counts in the stats bar are derived from this dictionary,
    ///     so "32 hum" means thirty-two distinct fingerprints, not thirty-two requests.
    /// </summary>
    private readonly ConcurrentDictionary<string, FingerprintStat> _fingerprints = new();

    private sealed class FingerprintStat
    {
        public double LastBotProbability;
        public RiskBand LastRisk;
        public ThreatBand LastIntent;
        public double LastThreatScore;
        public bool IsBot;
        public int Requests;
        public DateTime LastSeen;
    }

    public LiveDetectionTableService(
        DetectionEventSink sink,
        string mode, string upstream, string port, string policy,
        bool useTls, bool tunnelEnabled,
        Func<string?>? tunnelUrlGetter = null, int maxFeedRows = 0)
    {
        _sink = sink;
        _mode = mode;
        _upstream = upstream;
        _port = port;
        _policy = policy;
        _useTls = useTls;
        _tunnelEnabled = tunnelEnabled;
        _tunnelUrlGetter = tunnelUrlGetter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1200, stoppingToken);

        var entries = new LinkedList<DetectionEntry>();
        var scheme = _useTls ? "https" : "http";

        // Enter the VT100 alternate screen buffer.
        // This gives us a clean slate that is discarded on exit,
        // restoring whatever was in the terminal beforehand.
        // \x1b[?7l  disables line-wrap so oversized content clips instead of wrapping.
        System.Console.Write("\x1b[?1049h\x1b[?7l\x1b[?25l");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (_sink.Reader.TryRead(out var entry))
                    Ingest(entry, entries);

                TrimWindows();

                var w = System.Console.WindowWidth;
                var h = System.Console.WindowHeight;
                if (w >= 40 && h >= 8)
                {
                    var frame = BuildFrame(entries, scheme, w, h);
                    System.Console.Write(frame);
                }

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
            // Exit alt screen, re-enable wrap and cursor — restores original terminal.
            System.Console.Write("\x1b[?1049l\x1b[?7h\x1b[?25h");
        }
    }

    // ── Stats ingestion ───────────────────────────────────────────────────────

    private void Ingest(DetectionEntry entry, LinkedList<DetectionEntry> entries)
    {
        entries.AddFirst(entry);
        _totalRequests++;
        _totalDetectionTimeMs += entry.DetectionTimeMs;
        if (entry.DetectionTimeMs > _maxDetectionTimeMs)
            _maxDetectionTimeMs = entry.DetectionTimeMs;

        // Track per-fingerprint stats. Counts shown in the stats bar are derived from
        // _fingerprints so they represent unique fingerprints, not request volume.
        // Requests without a primary signature (rare) still count toward _totalRequests
        // but cannot be attributed to a fingerprint.
        var fp = entry.PrimarySignature;
        if (!string.IsNullOrEmpty(fp))
        {
            _fingerprints.AddOrUpdate(
                fp,
                _ => new FingerprintStat
                {
                    LastBotProbability = entry.BotProbability,
                    LastRisk = entry.RiskBand,
                    LastIntent = entry.ThreatBand,
                    LastThreatScore = entry.ThreatScore,
                    IsBot = entry.Verdict == "BOT",
                    Requests = 1,
                    LastSeen = entry.Timestamp,
                },
                (_, s) =>
                {
                    s.LastBotProbability = entry.BotProbability;
                    s.LastRisk = entry.RiskBand;
                    s.LastIntent = entry.ThreatBand;
                    s.LastThreatScore = entry.ThreatScore;
                    s.IsBot = entry.Verdict == "BOT";
                    s.Requests++;
                    s.LastSeen = entry.Timestamp;
                    return s;
                });
        }

        var path = entry.Path.Split('?')[0];
        _endpointHits.AddOrUpdate(path, 1, (_, c) => c + 1);
        _recentRequests.Enqueue(DateTime.Now);
        _sparkHistory.Enqueue(DateTime.Now);

        if (_endpointHits.Count > 500) Trim(_endpointHits, 100);
        if (_fingerprints.Count > 1000) TrimFingerprints(200);
        while (entries.Count > 200) entries.RemoveLast();
    }

    private void TrimFingerprints(int keep)
    {
        var stale = _fingerprints.OrderBy(kv => kv.Value.LastSeen)
            .Take(_fingerprints.Count - keep)
            .Select(kv => kv.Key).ToArray();
        foreach (var k in stale) _fingerprints.TryRemove(k, out _);
    }

    private (int Humans, int Bots, int Threats) FingerprintCounts()
    {
        var humans = 0; var bots = 0; var threats = 0;
        foreach (var stat in _fingerprints.Values)
        {
            if (stat.IsBot) bots++; else humans++;
            if (stat.LastIntent >= ThreatBand.High || stat.LastBotProbability >= 0.8) threats++;
        }
        return (humans, bots, threats);
    }

    private void TrimWindows()
    {
        var now = DateTime.Now;
        while (_recentRequests.TryPeek(out var t) && (now - t).TotalSeconds > 10)
            _recentRequests.TryDequeue(out _);
        while (_sparkHistory.TryPeek(out var t) && (now - t).TotalSeconds > 60)
            _sparkHistory.TryDequeue(out _);
    }

    // ── Frame builder ─────────────────────────────────────────────────────────

    private string BuildFrame(LinkedList<DetectionEntry> entries, string scheme, int w, int h)
    {
        var sb = new StringBuilder(w * h * 6);
        // Home cursor — in the alt screen this is always top-left of the visible window.
        sb.Append("\x1b[H");

        var uptime = DateTime.Now - _startTime;
        var reqPerSec = _recentRequests.Count / 10.0;
        var avgMs = _totalRequests > 0 ? _totalDetectionTimeMs / _totalRequests : 0;
        var tunnelUrl = _tunnelEnabled ? _tunnelUrlGetter?.Invoke() : null;

        var lines = 0;

        // ── Line 0: Title bar ─────────────────────────────────────────────────
        var title = $"  stylobot  {ModeLabel(_mode)}  \u2192 {_upstream}  \u23f1 {FormatUptime(uptime)}  {_totalRequests} req  {reqPerSec:F1}/s  ";
        var titleVis = VLen(title);
        sb.Append(C.BgBlue + C.White + C.Bold);
        sb.Append(title);
        if (titleVis < w) sb.Append(new string(' ', w - titleVis));
        sb.Append(C.R + "\n");
        lines++;

        // ── Line 1: Stats bar (counts are unique fingerprints, not requests) ──
        var spark = BuildSparkline(Math.Min(40, w / 3));
        var (humansFp, botsFp, threatsFp) = FingerprintCounts();
        var threatCol = threatsFp > 0 ? C.Red : C.Dim;

        var statsContent = C.Dim + "  fp " + C.R
            + C.Green + $"\u2713 {humansFp} hum" + C.R
            + C.Red + $"  \u2717 {botsFp} bot" + C.R
            + threatCol + $"  \u26a0 {threatsFp} threats" + C.R
            + C.Dim + $"  avg {avgMs:F1}ms  max {_maxDetectionTimeMs:F1}ms  " + C.R
            + C.Blue + spark + C.R;
        var statsVis = VLen(statsContent);
        sb.Append(statsContent);
        if (statsVis < w) sb.Append(new string(' ', w - statsVis));
        sb.Append("\x1b[K\n");
        lines++;

        // ── Line 2: Column headers ────────────────────────────────────────────
        var wide = w >= 100;
        var sideW = wide ? 30 : 0;
        var feedW = wide ? w - sideW - 1 : w;

        // Time(8) + Path + Bot%(4) + Rsk(3) + Int(3) + Act(6) + ms(4) = path consumes the rest
        var fixedCols = 8 + 4 + 3 + 3 + 6 + 4 + 7 * 2; // 7 two-space separators
        var feedHdr = " " + C.Dim
            + VPad("Time", 8)
            + "  " + VPad("Path", Math.Max(10, feedW - fixedCols))
            + "  " + VPadL("Bot%", 4)
            + "  " + VPad("Rsk", 3)
            + "  " + VPad("Int", 3)
            + "  " + VPad("Act", 6)
            + "  " + VPadL("ms", 4) + " " + C.R;
        sb.Append(feedHdr);
        if (wide)
        {
            sb.Append(C.Dim + "\u2502" + C.R);
            sb.Append(C.Dim + VPad(" Top Fingerprints", sideW - 1) + C.R);
        }
        sb.Append("\x1b[K\n");
        lines++;

        // ── Line 3: Divider ───────────────────────────────────────────────────
        sb.Append(C.Dim + new string('\u2500', feedW) + C.R);
        if (wide) sb.Append(C.Dim + "\u253c" + new string('\u2500', sideW - 1) + C.R);
        sb.Append("\x1b[K\n");
        lines++;

        // ── Body rows ─────────────────────────────────────────────────────────
        var footerRows = _tunnelEnabled ? 3 : 2; // extra row for tunnel URL line
        var bodyRows = h - lines - footerRows;
        var feedEntries = entries.Take(bodyRows).ToArray();
        var sideLines = wide ? BuildSideLines(sideW - 1, bodyRows, scheme, tunnelUrl) : null;

        for (var r = 0; r < bodyRows; r++)
        {
            // Feed column
            var feedLine = r < feedEntries.Length
                ? FormatFeedRow(feedEntries[r], feedW)
                : VPad("", feedW);
            if (r == 0 && feedEntries.Length == 0)
                feedLine = " " + C.Dim + "Waiting for requests..." + C.R + new string(' ', feedW - 25);

            sb.Append(feedLine);

            // Sidebar column
            if (wide)
            {
                sb.Append(C.Dim + "\u2502" + C.R);
                var side = sideLines != null && r < sideLines.Length ? sideLines[r] : new string(' ', sideW - 1);
                sb.Append(side);
            }

            sb.Append("\x1b[K\n");
            lines++;
        }

        // ── Footer divider ────────────────────────────────────────────────────
        sb.Append(C.Dim + new string('\u2500', w) + C.R + "\x1b[K\n");
        lines++;

        // ── Footer text ───────────────────────────────────────────────────────
        string footer;
        if (threatsFp >= 3 && _policy.Equals("logonly", StringComparison.OrdinalIgnoreCase))
            footer = C.Yellow + $"  \u26a0  {threatsFp} threat fingerprints in observe-only mode  add --policy block to enable blocking" + C.R;
        else
            footer = C.Dim + "  Ctrl+C to stop  |  --verbose for full logs" + C.R;

        sb.Append(footer);
        sb.Append("\x1b[K");

        if (_tunnelEnabled)
        {
            sb.Append("\n");
            if (tunnelUrl != null)
            {
                // Re-enable wrap so the full URL is never clipped; OSC 8 makes it clickable.
                sb.Append("\x1b[?7h");
                sb.Append(C.Bold + C.Green + "  tunnel: " + C.R);
                sb.Append($"\x1b]8;;{tunnelUrl}\x1b\\");   // OSC 8 open
                sb.Append(C.Green + C.Bold + tunnelUrl + C.R);
                sb.Append("\x1b]8;;\x1b\\");               // OSC 8 close
                sb.Append("\x1b[?7l");                      // restore no-wrap for rest of frame
            }
            else
                sb.Append(C.Yellow + "  tunnel: connecting\u2026" + C.R);
            sb.Append("\x1b[K");
        }
        // No trailing \n on the last line — prevents the terminal from scrolling.

        return sb.ToString();
    }

    // ── Feed row ──────────────────────────────────────────────────────────────

    private static string FormatFeedRow(DetectionEntry e, int width)
    {
        // Keep in sync with the column-width math in BuildFrame's feed header.
        var fixedCols = 8 + 4 + 3 + 3 + 6 + 4 + 7 * 2;
        var pathW = Math.Max(10, width - fixedCols);

        var path = VTrunc(e.Path.Split('?')[0], pathW);
        var prob = e.BotProbability;
        var probStr = $"{prob * 100:F0}%";

        string probCol;
        if (prob >= 0.9)       probCol = C.Bold + C.Red;
        else if (prob >= 0.7)  probCol = C.Red;
        else if (prob >= 0.4)  probCol = C.Yellow;
        else                   probCol = C.Green;

        var msCol = e.DetectionTimeMs > 200 ? C.Red : e.DetectionTimeMs > 50 ? C.Yellow : C.Dim;
        var action = FormatAction(e);
        var (riskTxt, riskCol) = FormatRiskCell(e.RiskBand);
        var (intTxt, intCol)   = FormatIntentCell(e.ThreatBand);

        return " " + C.Dim + e.Timestamp.ToString("HH:mm:ss") + C.R
            + "  " + VPad(path, pathW)
            + "  " + probCol + VPadL(probStr, 4) + C.R
            + "  " + riskCol + VPad(riskTxt, 3) + C.R
            + "  " + intCol  + VPad(intTxt, 3)  + C.R
            + "  " + action + VPad("", Math.Max(0, 6 - VLen(action)))
            + "  " + msCol + VPadL($"{e.DetectionTimeMs:F0}", 4) + C.R
            + " ";
    }

    /// <summary>Compact 2-3 char abbreviation for the risk band with a colour.</summary>
    private static (string Text, string Colour) FormatRiskCell(RiskBand r) => r switch
    {
        RiskBand.Verified => ("Ver", C.Bold + C.Red),
        RiskBand.VeryHigh => ("VH",  C.Bold + C.Red),
        RiskBand.High     => ("Hi",  C.Red),
        RiskBand.Medium   => ("Md",  C.Yellow),
        RiskBand.Elevated => ("El",  C.Yellow),
        RiskBand.Low      => ("Lo",  C.Green),
        RiskBand.VeryLow  => ("VL",  C.Green),
        _                 => ("-",   C.Dim),
    };

    /// <summary>Compact 1-2 char abbreviation for the intent (threat) band with a colour.</summary>
    private static (string Text, string Colour) FormatIntentCell(ThreatBand t) => t switch
    {
        ThreatBand.Critical => ("Cr", C.Bold + C.Red),
        ThreatBand.High     => ("Hi", C.Red),
        ThreatBand.Elevated => ("El", C.Yellow),
        ThreatBand.Low      => ("Lo", C.Dim),
        ThreatBand.None     => ("-",  C.Dim),
        _                   => ("-",  C.Dim),
    };

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private string[] BuildSideLines(int width, int totalRows, string scheme, string? tunnelUrl)
    {
        var lines = new List<string>(totalRows);

        void Section(string title)
        {
            var pad = Math.Max(0, width - title.Length - 1);
            lines.Add(" " + C.Bold + title + C.R + C.Dim + new string('\u2500', pad) + C.R);
        }

        void Row(string content)
        {
            var vis = VLen(content);
            var pad = Math.Max(0, width - vis);
            lines.Add(content + new string(' ', pad));
        }

        void Divider()
        {
            lines.Add(C.Dim + new string('\u2500', width) + C.R);
        }

        // Top fingerprints. Ordered by most-recently-seen so the list visibly updates
        // as scores change. Each row shows the last 10 chars of the signature, the
        // latest bot percentage, and the latest risk band, colour-coded by verdict.
        Section("Top Fingerprints");
        var fps = _fingerprints
            .OrderByDescending(kv => kv.Value.LastSeen)
            .Take(7)
            .ToList();
        if (fps.Count == 0)
            Row(C.Dim + "  none yet" + C.R);
        else
            foreach (var (sig, stat) in fps)
            {
                var bullet = stat.IsBot ? C.Red + "\u25a0" : C.Green + "\u25a0";
                var sigTail = sig.Length > 10 ? sig[^10..] : sig;
                var prob = $"{stat.LastBotProbability * 100:F0}%";
                var (rTxt, rCol) = FormatRiskCell(stat.LastRisk);
                // Layout: " ● <sigtail-10>  ##%  RR "  ≈ 4 + 10 + 2 + 4 + 2 + 3 = 25 chars min
                var line = " " + bullet + C.R + " "
                    + VPad(sigTail, 10)
                    + "  " + (stat.IsBot ? C.Red : C.Green) + VPadL(prob, 4) + C.R
                    + "  " + rCol + VPad(rTxt, 3) + C.R;
                Row(line);
            }

        Divider();

        // Top endpoints
        Section("Endpoints");
        var eps = _endpointHits.OrderByDescending(kv => kv.Value).Take(5).ToList();
        if (eps.Count == 0)
            Row(C.Dim + "  none yet" + C.R);
        else
            foreach (var (ep, count) in eps)
                Row(C.Dim + " \u2192 " + C.R + VTrunc(ep, width - 8) + C.Dim + " " + count.ToString().PadLeft(4) + C.R);

        Divider();

        // Config
        Section("Config");
        Row(C.Dim + " listen  " + C.R + $"{scheme}://:{_port}");
        Row(C.Dim + " policy  " + C.R + PolicyLabel(_policy));
        if (_tunnelEnabled)
            Row(C.Dim + " tunnel  " + C.R
                + (tunnelUrl != null
                    ? C.Green + VTrunc(tunnelUrl.Replace("https://", ""), width - 10) + C.R
                    : C.Yellow + "connecting\u2026" + C.R));

        // Fill to totalRows
        while (lines.Count < totalRows)
            lines.Add(new string(' ', width));

        return lines.Take(totalRows).ToArray();
    }

    // ── Sparkline ─────────────────────────────────────────────────────────────

    private string BuildSparkline(int buckets)
    {
        var now = DateTime.Now;
        var bucketSecs = 60.0 / buckets;
        var counts = new int[buckets];
        foreach (var t in _sparkHistory)
        {
            var age = (now - t).TotalSeconds;
            if (age < 0 || age >= 60) continue;
            var idx = buckets - 1 - (int)(age / bucketSecs);
            if ((uint)idx < (uint)buckets) counts[idx]++;
        }
        var max = 1;
        foreach (var c in counts) if (c > max) max = c;
        const string bars = "\u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588";
        var sb = new StringBuilder(buckets + 1);
        sb.Append('\u258f');
        foreach (var c in counts)
            sb.Append(bars[(int)((double)c / max * (bars.Length - 1))]);
        return sb.ToString();
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static string FormatAction(DetectionEntry e)
    {
        if (e.Verdict == "HUMAN") return C.Green + "Allow" + C.R;
        return (e.ActionPolicy ?? "").ToLowerInvariant() switch
        {
            "block" or "block-hard" or "block-soft" => C.Bold + C.Red + "BLOCK" + C.R,
            "challenge" or "challenge-pow" or "challenge-js" => C.Yellow + "Chall" + C.R,
            "throttle" or "throttle-stealth" => C.Yellow + "Throt" + C.R,
            "logonly" or "shadow" or "debug" => C.Dim + "Watch" + C.R,
            _ => e.BotProbability >= 0.5 ? C.Dim + "Watch" + C.R : C.Green + "Allow" + C.R
        };
    }

    private static string ModeLabel(string mode) => mode.ToLowerInvariant() switch
    {
        "demo"       => "OBSERVE",
        "production" => C.Green + "ACTIVE" + C.White,
        "learning"   => C.Yellow + "LEARNING" + C.White,
        _            => mode.ToUpperInvariant()
    };

    private static string PolicyLabel(string policy) => policy.ToLowerInvariant() switch
    {
        "block"     => C.Red + "Block" + C.R,
        "throttle" or "throttle-stealth" => C.Yellow + "Throttle" + C.R,
        "challenge" => C.Yellow + "Challenge" + C.R,
        "logonly"   => C.Dim + "Observe only" + C.R,
        _           => policy
    };

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m{ts.Seconds:D2}s";
        return $"{ts.Seconds}s";
    }

    // ── ANSI color constants ──────────────────────────────────────────────────

    private static class C
    {
        public const string R      = "\x1b[0m";
        public const string Bold   = "\x1b[1m";
        public const string Dim    = "\x1b[2m";
        public const string Red    = "\x1b[31m";
        public const string Green  = "\x1b[32m";
        public const string Yellow = "\x1b[33m";
        public const string Blue   = "\x1b[34m";
        public const string White  = "\x1b[97m";
        public const string BgBlue = "\x1b[44m";
    }

    // ── Visual-width string helpers ───────────────────────────────────────────

    // Count printable characters, skipping CSI escape sequences (\x1b[...m and \x1b[...H etc.)
    private static int VLen(string s)
    {
        var len = 0;
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                i += 2;
                while (i < s.Length && s[i] != 'm' && s[i] != 'H' && s[i] != 'J' && s[i] != 'K' && s[i] != 'l' && s[i] != 'h')
                    i++;
                i++;
            }
            else
            {
                len++;
                i++;
            }
        }
        return len;
    }

    private static string VTrunc(string s, int maxVis)
    {
        if (s.Length <= maxVis) return s; // fast path: no escape codes, length == vis length
        var vis = 0;
        var i = 0;
        while (i < s.Length && vis < maxVis - 1)
        {
            if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                var j = i + 2;
                while (j < s.Length && s[j] != 'm' && s[j] != 'H' && s[j] != 'J') j++;
                i = j + 1;
            }
            else { vis++; i++; }
        }
        return s[..i] + "\u2026";
    }

    private static string VPad(string s, int width)
    {
        var vis = VLen(s);
        return vis >= width ? s : s + new string(' ', width - vis);
    }

    private static string VPadL(string s, int width)
    {
        var vis = VLen(s);
        return vis >= width ? s : new string(' ', width - vis) + s;
    }

    private static void Trim(ConcurrentDictionary<string, int> dict, int keepTop)
    {
        var keep = dict.OrderByDescending(kv => kv.Value).Take(keepTop).Select(kv => kv.Key).ToHashSet();
        foreach (var key in dict.Keys)
            if (!keep.Contains(key))
                dict.TryRemove(key, out _);
    }
}

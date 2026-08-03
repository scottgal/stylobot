using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;

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
    double ThreatScore,
    double PriorProbability,
    double RequestContributionDelta,
    string VerdictSource);

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

            // ev.Signals is a snapshot of ledger.MergedSignals; SignatureAtom raises its primary
            // signature as a sink-only "signature.primary:<hash>" hint that never lands in a
            // detector Contribution, so it never reaches MergedSignals. Reading it via
            // ev.Signals left every entry's PrimarySignature null, so the fingerprint
            // dictionary the stats bar / Top Fingerprints sidebar are keyed on never
            // populated - counts rendered "0" once and never moved. Read the rich signature
            // object SignatureAtom stores directly on HttpContext.Items instead.
            var primarySig = SignatureAtom.TryGetMultifactor(context)?.PrimarySignature;

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
                ev.ThreatScore,
                ev.PriorProbability,
                ev.RequestContributionDelta,
                context.Response.Headers.TryGetValue("X-StyloBot-VerdictSource", out var vs) ? vs.ToString() : "pipeline"));
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
    private readonly string? _licenseBanner; // pre-formatted license line, null when unlicensed

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
        public DateTime FirstSeen;
        public DateTime LastSeen;

        /// <summary>
        ///     Display name for this fingerprint when the orchestrator identified it as a
        ///     known bot (e.g. "Mastodon", "googlebot", "curl"). Null for unidentified
        ///     fingerprints — render falls back to the trailing hash slice in that case.
        /// </summary>
        public string? BotName;

        /// <summary>
        ///     EWMA of bot probability across this fingerprint's requests. Damps
        ///     per-request volatility so the sidebar reflects the fingerprint's stable
        ///     trend rather than the latest spike.
        /// </summary>
        public double Ewma;

        /// <summary>
        ///     Last 8 per-request probabilities (oldest..newest, packed left). Powers
        ///     the per-fingerprint sparkline so volatility is visible without
        ///     dominating the score column.
        /// </summary>
        public readonly double[] RecentScores = new double[8];
        public int RecentScoresCount;

        public void Push(double p)
        {
            if (RecentScoresCount < RecentScores.Length)
            {
                RecentScores[RecentScoresCount++] = p;
            }
            else
            {
                for (var i = 1; i < RecentScores.Length; i++)
                    RecentScores[i - 1] = RecentScores[i];
                RecentScores[^1] = p;
            }
        }
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
        _licenseBanner = ResolveLicenseBanner();
    }

    /// <summary>
    ///     Reads license info from STYLOBOT_LICENSE (env var or file path).
    ///     Returns a pre-formatted banner line, or null when unlicensed.
    /// </summary>
    private static string? ResolveLicenseBanner()
    {
        try
        {
            var envVal = Environment.GetEnvironmentVariable("STYLOBOT_LICENSE");
            var content = envVal;
            if (string.IsNullOrWhiteSpace(content)) return null;

            // If the value looks like a file path, read it
            if (content.Length < 500 && File.Exists(content))
                content = File.ReadAllText(content).Trim();

            if (string.IsNullOrWhiteSpace(content)) return null;

            // Try to parse as JSON for structured display
            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                var tier = GetString(root, "tier") ?? GetString(root, "plan") ?? "pro";
                var licensee = GetString(root, "licensee") ?? GetString(root, "company") ?? "";
                var seats = root.TryGetProperty("seats", out var s) ? s.GetInt32().ToString() : null;
                var expires = GetString(root, "expires") ?? GetString(root, "expiry") ?? "";

                var parts = new List<string> { $"stylo.bot/{tier}" };
                if (!string.IsNullOrEmpty(licensee)) parts.Add(licensee);
                if (seats is not null) parts.Add($"{seats} seats");
                if (!string.IsNullOrEmpty(expires)) parts.Add($"expires {expires}");
                return string.Join(" · ", parts);
            }
            catch
            {
                // Not valid JSON — use first line as the banner
                var firstLine = content.Split('\n')[0].Trim();
                return firstLine.Length > 80 ? firstLine[..77] + "..." : firstLine;
            }
        }
        catch { return null; }
    }

    private static string? GetString(JsonElement el, string key)
    {
        return el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
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
                _ =>
                {
                    var stat = new FingerprintStat
                    {
                        LastBotProbability = entry.BotProbability,
                        LastRisk = entry.RiskBand,
                        LastIntent = entry.ThreatBand,
                        LastThreatScore = entry.ThreatScore,
                        IsBot = entry.Verdict == "BOT",
                        Requests = 1,
                        FirstSeen = entry.Timestamp,
                        LastSeen = entry.Timestamp,
                        Ewma = entry.BotProbability,
                        BotName = entry.BotName,
                    };
                    stat.Push(entry.BotProbability);
                    return stat;
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
                    s.Ewma = 0.3 * entry.BotProbability + 0.7 * s.Ewma;
                    s.Push(entry.BotProbability);
                    // Latch the first non-empty BotName: deterministic name synthesis can
                    // overwrite later but a known UA name should never be lost to a null update.
                    if (!string.IsNullOrEmpty(entry.BotName))
                        s.BotName = entry.BotName;
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

    // ── Frame builder (revamped — dashboard-style) ────────────────────────────

    private string BuildFrame(LinkedList<DetectionEntry> entries, string scheme, int w, int h)
    {
        var sb = new StringBuilder(w * h * 6);
        sb.Append("\x1b[H"); // home cursor

        var uptime = DateTime.Now - _startTime;
        var reqPerSec = _recentRequests.Count / 10.0;
        var avgMs = _totalRequests > 0 ? _totalDetectionTimeMs / _totalRequests : 0;

        var (humansFp, botsFp, threatsFp) = FingerprintCounts();
        var totalFp = humansFp + botsFp;
        var humanPct = totalFp > 0 ? (int)(humansFp * 100.0 / totalFp) : 0;
        var botPct = totalFp > 0 ? (int)(botsFp * 100.0 / totalFp) : 0;

        // ═══ Row 0: Top border ═══════════════════════════════════════════════
        sb.Append(C.Blue).Append('╔').Append(new string('═', w - 2)).Append("╗\n");

        // ═══ Row 1: Title bar ═══════════════════════════════════════════════
        var titleLeft = $" stylo{C.Dim}·{C.R}{C.Bold}bot{C.R}";
        var modeTag = _mode == "production" ? $"{C.Red}●{C.R} production" : $"{C.Yellow}○{C.R} demo";
        var upstreamDisplay = TruncateUrl(_upstream, Math.Max(20, w - VLen(titleLeft) - VLen(modeTag) - 25));
        var titleRight = $"{C.Dim}⏱{C.R} {FormatUptime(uptime)}  {C.Dim}{reqPerSec:F1}/s{C.R}";

        sb.Append(C.Blue).Append("║ ").Append(C.R);
        sb.Append(C.Bold).Append(titleLeft).Append(C.R);
        sb.Append("  ").Append(modeTag).Append("  ").Append(C.Dim).Append("→").Append(C.R).Append(" ").Append(upstreamDisplay);
        var titleUsed = VLen(titleLeft) + 2 + VLen(modeTag) + 2 + 1 + 1 + upstreamDisplay.Length;
        var rightPad = Math.Max(0, w - 4 - titleUsed - VLen(titleRight));
        sb.Append(new string(' ', rightPad)).Append(titleRight);
        sb.Append(C.Blue).Append(" ║\n");

        // ═══ Row 2: Stats bar with mini progress bars ═══════════════════════
        var barW = Math.Min(16, (w - 40) / 3);
        var humanBar = BuildMiniBar(humanPct, barW, C.Green);
        var botBar = BuildMiniBar(botPct, barW, C.Red);
        var stats = $"{C.Green}✓ {humansFp} humans{C.R} {humanBar} {humanPct}%  "
                  + $"{C.Red}✗ {botsFp} bots{C.R} {botBar} {botPct}%";
        if (threatsFp > 0) stats += $"  {C.Yellow}⚠ {threatsFp}{C.R}";
        stats += $"  {C.Dim}avg {FormatLatency(avgMs)}{C.R}";
        var statsVis = VLen(stats);
        sb.Append(C.Blue).Append("║ ").Append(C.R).Append(stats);
        if (statsVis < w - 4) sb.Append(new string(' ', w - 4 - statsVis));
        sb.Append(C.Blue).Append(" ║\n");

        // ═══ Row 3: License banner (if present) ═══════════════════════════
        if (_licenseBanner is not null)
        {
            var licLine = $" {C.Green}🔑{C.R} {C.Dim}{_licenseBanner}{C.R}";
            sb.Append(C.Blue).Append("║").Append(C.R).Append(licLine);
            var licVis = VLen(licLine);
            if (licVis < w - 2) sb.Append(new string(' ', w - 2 - licVis));
            sb.Append(C.Blue).Append("║\n");
        }

        // ═══ Column headers ═══════════════════════════════════════════════
        var sideW = w >= 120 ? Math.Clamp(w / 2, 40, 70) : 0;
        var wide = sideW > 0;
        var feedW = wide ? w - sideW - 3 : w - 2;
        var showDelta = feedW >= 70;
        var showRisk = feedW >= 60;
        var showIntent = feedW >= 55;

        var hdr = BuildFeedHeader(feedW, showDelta, showRisk, showIntent);
        sb.Append(C.Blue).Append("║").Append(C.R).Append(hdr);
        if (wide)
        {
            sb.Append(C.Blue).Append("│").Append(C.R);
            sb.Append(C.Dim).Append(" Fingerprint").Append(new string(' ', Math.Max(0, sideW - 14))).Append(C.R);
        }
        else sb.Append(new string(' ', w - 2 - VLen(hdr)));
        sb.Append(C.Blue).Append("║\n");

        // Divider
        sb.Append(C.Blue).Append("╟").Append(new string('─', feedW));
        if (wide) sb.Append(C.Blue).Append("┼").Append(new string('─', sideW)).Append(C.R);
        else sb.Append(new string('─', w - 2 - feedW));
        sb.Append(C.Blue).Append("╢\n");

        // ═══ Body rows ════════════════════════════════════════════════════
        var footerRows = 2 + (_tunnelEnabled ? 1 : 0);
        var bodyRows = h - 8 - footerRows; // header(4)+divider(1)+footer(2)+border(1) approx
        bodyRows = Math.Max(3, bodyRows);
        var feedEntries = entries.Take(bodyRows).ToArray();
        var sideLines = wide ? BuildFingerprintSidebar(sideW, bodyRows) : null;

        for (var r = 0; r < bodyRows; r++)
        {
            sb.Append(C.Blue).Append("║").Append(C.R);
            if (r < feedEntries.Length)
                sb.Append(FormatFeedRowV2(feedEntries[r], feedW, showDelta, showRisk, showIntent));
            else if (r == 0 && feedEntries.Length == 0)
            {
                sb.Append(C.Dim).Append("  Waiting for requests...").Append(C.R);
                sb.Append(new string(' ', Math.Max(0, feedW - 24)));
            }
            else sb.Append(new string(' ', feedW));

            if (wide)
            {
                sb.Append(C.Blue).Append("│").Append(C.R);
                sb.Append(sideLines is not null && r < sideLines.Length ? sideLines[r] : new string(' ', sideW));
            }
            sb.Append(C.Blue).Append("║\n");
        }

        // ═══ Footer ═══════════════════════════════════════════════════════
        sb.Append(C.Blue).Append("╚").Append(new string('═', w - 2)).Append("╝\n");

        var threatWarn = threatsFp >= 3 && _policy.Equals("logonly", StringComparison.OrdinalIgnoreCase)
            ? $"{C.Yellow}⚠ {threatsFp} threats — add --policy block{C.R}  "
            : "";
        var footStats = $"{C.Dim}{_totalRequests:N0} total  avg {FormatLatency(avgMs)}  max {FormatLatency(_maxDetectionTimeMs)}  Ctrl+C stop{C.R}";
        var footer = $" {threatWarn}{footStats}";
        sb.Append(footer);

        // SSL warning: proxying HTTPS upstreams without proper TLS client certs
        // can cause SSL errors (untrusted CA, self-signed upstream cert). Surface
        // the fix inline so operators don't have to dig through logs.
        if (_upstream.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("\n ").Append(C.Yellow).Append("⚠").Append(C.R);
            sb.Append(C.Yellow).Append(" HTTPS upstream: SSL errors? Use --cert <pfx> --cert-password <pw>").Append(C.R);
            sb.Append(C.Dim).Append("  (or set STYLOBOT_SKIP_UPSTREAM_SSL_VALIDATION=true for dev only)").Append(C.R);
        }

        if (_tunnelEnabled)
        {
            var tunnelUrl = _tunnelEnabled ? _tunnelUrlGetter?.Invoke() : null;
            sb.Append("\n");
            sb.Append(tunnelUrl is not null
                ? $"{C.Green}  tunnel: {tunnelUrl}{C.R}"
                : $"{C.Yellow}  tunnel: connecting…{C.R}");
        }

        return sb.ToString();
    }

    // ── Mini bar helper ────────────────────────────────────────────────────────

    private static string BuildMiniBar(int pct, int width, string color)
    {
        if (width <= 0) return "";
        var filled = (int)Math.Round(pct * width / 100.0);
        filled = Math.Clamp(filled, 0, width);
        var empty = width - filled;
        return color + new string('█', filled) + C.Dim + new string('░', empty) + C.R;
    }

    private static string TruncateUrl(string url, int maxLen)
    {
        if (url.Length <= maxLen) return url;
        var display = url.Replace("https://", "").Replace("http://", "");
        if (display.Length <= maxLen) return display;
        return display[..(maxLen - 1)] + "…";
    }

    // ── Feed header V2 ────────────────────────────────────────────────────────

    private static string BuildFeedHeader(int feedW, bool showDelta, bool showRisk, bool showIntent)
    {
        var hdr = " " + C.Dim + VPad("Time", 8);
        var pathW = feedW - 10;
        if (showDelta) pathW -= 7;
        if (showRisk) pathW -= 5;
        if (showIntent) pathW -= 5;
        pathW -= 11;
        pathW = Math.Max(10, pathW);
        hdr += "  " + VPad("Path", pathW);
        if (showDelta) hdr += "  " + VPadL("Δ%", 5);
        if (showRisk) hdr += "  " + VPad("Rsk", 3);
        if (showIntent) hdr += "  " + VPad("Int", 3);
        hdr += "  " + VPad("Action", 6);
        hdr += "  " + VPadL("Lat", 5) + " " + C.R;
        return hdr;
    }

    // ── Feed row V2 ───────────────────────────────────────────────────────────

    private static string FormatFeedRowV2(DetectionEntry e, int width, bool showDelta, bool showRisk, bool showIntent)
    {
        var pathW = width - 10;
        if (showDelta) pathW -= 7;
        if (showRisk) pathW -= 5;
        if (showIntent) pathW -= 5;
        pathW -= 11;
        pathW = Math.Max(8, pathW);

        var path = VTrunc(e.Path.Split('?')[0], pathW);
        var srcMark = e.VerdictSource == "cache" ? C.Dim + "*" + C.R : " ";
        var row = srcMark + C.Dim + VPad(e.Timestamp.ToString("HH:mm:ss"), 8) + C.R
            + "  " + path + new string(' ', Math.Max(0, pathW - path.Length));

        if (showDelta)
        {
            var d = e.RequestContributionDelta;
            var dStr = $"{(d >= 0 ? "+" : "")}{d * 100:F1}";
            var dCol = Math.Abs(d) < 0.02 ? C.Dim : d > 0 ? C.Yellow : C.Green;
            row += "  " + dCol + VPadL(dStr, 5) + C.R;
        }
        if (showRisk) { var (t, c) = FormatRiskCell(e.RiskBand); row += "  " + c + VPad(t, 3) + C.R; }
        if (showIntent) { var (t, c) = FormatIntentCell(e.ThreatBand); row += "  " + c + VPad(t, 3) + C.R; }

        var action = FormatAction(e);
        row += "  " + action + new string(' ', Math.Max(0, 6 - VLen(action)));

        var msCol = e.DetectionTimeMs > 200 ? C.Red : e.DetectionTimeMs > 50 ? C.Yellow : C.Dim;
        row += "  " + msCol + VPadL(FormatLatency(e.DetectionTimeMs), 5) + C.R + " ";

        var vis = VLen(row);
        if (vis < width) row += new string(' ', width - vis);
        return row;
    }

    // ── Fingerprint sidebar V2 ────────────────────────────────────────────────

    private string[] BuildFingerprintSidebar(int width, int totalRows)
    {
        var lines = new List<string>(totalRows);
        lines.Add(" " + C.Dim + VPad("fingerprint", width - 14) + "  %    req  last" + C.R);
        lines.Add(C.Dim + new string('─', width) + C.R);

        var ordered = _fingerprints.Values
            .OrderByDescending(s => s.LastSeen)
            .Take(totalRows - 2)
            .ToArray();

        foreach (var s in ordered)
        {
            var fpKey = "";
            foreach (var kv in _fingerprints) { if (kv.Value == s) { fpKey = kv.Key; break; } }

            var name = !string.IsNullOrEmpty(s.BotName) ? s.BotName
                : fpKey.Length > 8 ? fpKey[^8..] : fpKey;
            var nameW = Math.Max(6, width - 24);
            name = VTrunc(name, nameW);
            var pct = s.Ewma * 100;
            var pctCol = pct >= 70 ? C.Red : pct >= 40 ? C.Yellow : C.Green;

            var spark = BuildTinySpark(s.RecentScores, s.RecentScoresCount, 6);
            var row = " " + VPad(name, nameW)
                + "  " + pctCol + VPadL($"{pct:F0}%", 4) + C.R
                + "  " + C.Dim + VPadL(s.Requests.ToString(), 4) + C.R
                + "  " + VPadL(FormatAgo(DateTime.Now - s.LastSeen), 4)
                + " " + spark;
            var vis = VLen(row);
            if (vis < width) row += new string(' ', width - vis);
            lines.Add(row);
        }
        while (lines.Count < totalRows) lines.Add(new string(' ', width));
        return lines.ToArray();
    }

    private static string BuildTinySpark(double[] scores, int count, int width)
    {
        if (count < 2 || width <= 0) return "";
        var chars = " ▁▂▃▄▅▆▇█";
        var result = new char[Math.Min(width, count)];
        var step = Math.Max(1, count / width);
        for (var i = 0; i < result.Length; i++)
        {
            var idx = Math.Min(i * step, count - 1);
            var level = (int)Math.Round(Math.Clamp(scores[idx], 0, 1) * (chars.Length - 1));
            result[i] = chars[level];
        }
        return new string(result);
    }

    // ── Feed row ──────────────────────────────────────────────────────────────

    private static string FormatFeedRow(DetectionEntry e, int width)
    {
        // Keep in sync with the column-width math in BuildFrame's feed header.
        var fixedCols = 1 + 8 + 5 + 3 + 3 + 6 + 5 + 7 * 2;
        var pathW = Math.Max(10, width - fixedCols);

        var path = VTrunc(e.Path.Split('?')[0], pathW);

        // Per-request contribution delta in percentage points. Bounded to roughly
        // [-100, +100] but typically tiny; F1 keeps the column scannable.
        var delta = e.RequestContributionDelta;
        var deltaStr = $"{(delta >= 0 ? "+" : "")}{delta * 100:F1}";
        var deltaCol = Math.Abs(delta) < 0.02 ? C.Dim
            : delta > 0 ? C.Yellow
            : C.Green;

        var msCol = e.DetectionTimeMs > 200 ? C.Red : e.DetectionTimeMs > 50 ? C.Yellow : C.Dim;
        var action = FormatAction(e);
        var (riskTxt, riskCol) = FormatRiskCell(e.RiskBand);
        var (intTxt, intCol)   = FormatIntentCell(e.ThreatBand);

        // Dim asterisk in front of the timestamp marks rows where the verdict
        // came from the cached fingerprint posterior (bypassed the pipeline).
        var srcMark = e.VerdictSource == "cache" ? C.Dim + "*" + C.R : " ";

        return srcMark + C.Dim + e.Timestamp.ToString("HH:mm:ss") + C.R
            + "  " + VPad(path, pathW)
            + "  " + deltaCol + VPadL(deltaStr, 5) + C.R
            + "  " + riskCol + VPad(riskTxt, 3) + C.R
            + "  " + intCol  + VPad(intTxt, 3)  + C.R
            + "  " + action + VPad("", Math.Max(0, 6 - VLen(action)))
            + "  " + msCol + VPadL(FormatLatency(e.DetectionTimeMs), 5) + C.R
            + " ";
    }

    /// <summary>
    ///     Adaptive latency formatter for the per-request column. Sub-ms values render
    ///     as integer microseconds (the verdict-cache Skip path lives here), single-digit
    ///     ms get one decimal, larger ms drop the fraction, and seconds collapse with a
    ///     decimal. All forms fit a five-character field.
    /// </summary>
    public static string FormatLatency(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) return "-";
        if (ms < 1.0) return $"{(int)Math.Round(ms * 1000.0)}\u00b5s"; // e.g. 342µs
        if (ms < 10.0) return $"{ms:F1}ms";                            // e.g. 1.4ms
        if (ms < 1000.0) return $"{ms:F0}ms";                          // e.g. 55ms, 200ms
        return $"{ms / 1000.0:F1}s";                                   // e.g. 1.2s
    }

    /// <summary>
    ///     Compact relative-time format for the fingerprint sidebar's first-seen /
    ///     last-seen columns. Tops out at 5 chars: "now", "12s", "5m", "2h", "3d".
    ///     Negatives clamp to "now". Right-aligned by the caller via VPadL.
    /// </summary>
    public static string FormatAgo(TimeSpan elapsed)
    {
        var s = (int)elapsed.TotalSeconds;
        if (s <= 1) return "now";
        if (s < 60) return $"{s}s";
        var m = s / 60;
        if (m < 60) return $"{m}m";
        var h = m / 60;
        if (h < 48) return $"{h}h";
        return $"{h / 24}d";
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
        // EWMA-smoothed posterior (the fingerprint's stable verdict, not the latest
        // spike), an 8-sample sparkline of recent observations so volatility shows
        // up as trend, and the latest risk band. Bullet colour reflects the EWMA.
        Section("Top Fingerprints");
        var sidebarTail = _tunnelEnabled ? 3 : 2; // config one-liner (+ optional tunnel) + divider
        var fpRows = Math.Max(totalRows - sidebarTail - 2 /* Section header + divider */, 4);
        var fps = _fingerprints
            .OrderByDescending(kv => kv.Value.LastSeen)
            .Take(fpRows)
            .ToList();
        if (fps.Count == 0)
            Row(C.Dim + "  none yet" + C.R);
        else
        {
            // Extended layout fires when the sidebar is wide enough (\u226550) to fit
            // first-seen + last-seen + request-count without crushing the name. The
            // last-seen column ticks visibly as new requests arrive, so the operator
            // can SEE a fingerprint converging in real time. First-seen + variant
            // suffix together disambiguate distinct fingerprints that compose to the
            // same base name (e.g. two Mastodon instances when the UA has no +URL).
            var extended = width >= 50;
            // Column budgets in the extended layout (header row pre-computes the same):
            //   bullet(1) sp(1) name(width-30) sp(2) pct(4) sp(2) first(5) sp(2) last(5) sp(2) reqs(4)
            var nameW = extended ? Math.Max(20, width - 30) : 23;
            var now = DateTime.UtcNow;

            // Same-name collision disambiguation. The composer leaves it to the display
            // layer because the right label depends on local context: when two distinct
            // fingerprints both produce "Mastodon" (no +URL discriminator in the UA),
            // the *first* one keeps the bare name and subsequent ones get " variant N".
            // Ordered by FirstSeen so "variant 1" is the older fingerprint - stable
            // across renders even as new ones arrive.
            var byName = fps
                .GroupBy(kv => !string.IsNullOrEmpty(kv.Value.BotName)
                    ? kv.Value.BotName!
                    : (kv.Key.Length > 10 ? kv.Key[^10..] : kv.Key))
                .ToDictionary(g => g.Key,
                    g => g.OrderBy(kv => kv.Value.FirstSeen).Select(kv => kv.Key).ToList());

            foreach (var (sig, stat) in fps)
            {
                var bulletCol = stat.Ewma > 0.5 ? C.Red : C.Green;
                var bullet = bulletCol + "\u25a0";
                // Prefer the orchestrator's identified BotName (Mastodon, googlebot, curl, ...)
                // over the trailing hash slice. Falls back to the hash for unidentified
                // fingerprints (anonymous humans, unrecognised tools).
                var baseLabel = !string.IsNullOrEmpty(stat.BotName)
                    ? stat.BotName!
                    : (sig.Length > 10 ? sig[^10..] : sig);
                var siblings = byName[baseLabel];
                var label = siblings.Count > 1
                    ? $"{baseLabel} variant {siblings.IndexOf(sig) + 1}"
                    : baseLabel;
                var posterior = $"{stat.Ewma * 100:F0}%";

                string line;
                if (extended)
                {
                    var firstSeen = FormatAgo(now - stat.FirstSeen);
                    var lastSeen = FormatAgo(now - stat.LastSeen);
                    var reqs = stat.Requests.ToString();
                    line = bullet + C.R
                        + " " + VPad(label, nameW)
                        + "  " + bulletCol + VPadL(posterior, 4) + C.R
                        + "  " + C.Dim + VPadL(firstSeen, 5) + C.R
                        + "  " + C.Dim + VPadL(lastSeen, 5) + C.R
                        + "  " + C.Dim + VPadL(reqs, 4) + C.R;
                }
                else
                {
                    // Compact (30-wide) layout: name + posterior only, as before.
                    // bullet(1) + space(1) + label(23) + space(1) + posterior(5) = 31.
                    line = bullet + C.R
                        + " " + VPad(label, 23)
                        + " " + bulletCol + VPadL(posterior, 5) + C.R;
                }
                Row(line);
            }
        }

        Divider();

        // Config one-liner. Tunnel URL gets its own row only when enabled (it's the long
        // element). The dedicated Section header and the separate listen/policy rows were
        // collapsed: the operator already sees this info in the startup banner and dashboard
        // URL print-out, and the row budget here is better spent on more fingerprints.
        Row(" " + C.Dim + scheme + "://:" + _port + "  " + PolicyLabel(_policy) + C.R);
        if (_tunnelEnabled)
            Row(" " + C.Dim + "tunnel "
                + (tunnelUrl != null
                    ? C.Green + VTrunc(tunnelUrl.Replace("https://", ""), width - 9) + C.R
                    : C.Yellow + "connecting\u2026" + C.R));

        // Fill to totalRows
        while (lines.Count < totalRows)
            lines.Add(new string(' ', width));

        return lines.Take(totalRows).ToArray();
    }

    // ── Sparkline ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Render a fixed 8-cell sparkline from probability samples in [0,1].
    ///     Pads with leading spaces when fewer than 8 samples have arrived so
    ///     the column width stays stable as fingerprints warm up.
    /// </summary>
    private static string MicroSpark(double[] vals, int count)
    {
        if (count == 0) return new string(' ', 8);
        const string chars = "\u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588"; // 8 levels
        var sb = new StringBuilder(8);
        var pad = 8 - count;
        for (var i = 0; i < pad; i++) sb.Append(' ');
        for (var i = 0; i < count; i++)
        {
            var v = Math.Clamp(vals[i], 0.0, 1.0);
            var idx = (int)Math.Floor(v * (chars.Length - 1));
            sb.Append(chars[idx]);
        }
        return sb.ToString();
    }

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
        public const string R         = "\x1b[0m";
        public const string Bold      = "\x1b[1m";
        public const string Dim       = "\x1b[2m";
        public const string Italic    = "\x1b[3m";
        public const string Red       = "\x1b[31m";
        public const string Green     = "\x1b[32m";
        public const string Yellow    = "\x1b[33m";
        public const string Blue      = "\x1b[34m";
        public const string White     = "\x1b[97m";
        public const string BgBlue    = "\x1b[44m";
        public const string StyloGray = "\x1b[38;2;221;221;221m"; // #dddddd, 24-bit truecolor
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

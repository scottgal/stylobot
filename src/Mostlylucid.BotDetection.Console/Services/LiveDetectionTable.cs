using System.Collections.Concurrent;
using System.Threading.Channels;
using Mostlylucid.BotDetection.Orchestration;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     A single detection event for the live table display.
/// </summary>
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

/// <summary>
///     Singleton channel that receives detection results from middleware
///     and feeds them to the live Spectre.Console table.
/// </summary>
public sealed class DetectionEventSink
{
    private readonly Channel<DetectionEntry> _channel = Channel.CreateBounded<DetectionEntry>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<DetectionEntry> Reader => _channel.Reader;

    public void Write(DetectionEntry entry) => _channel.Writer.TryWrite(entry);
}

/// <summary>
///     Middleware that taps detection results from HttpContext.Items
///     and writes them to the DetectionEventSink for the live table.
/// </summary>
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
        try
        {
            await _next(context);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("response has already started", StringComparison.OrdinalIgnoreCase))
        {
            // Expected: YARP throws when bot detection already wrote a block response.
        }

        // Read detection results from HttpContext.Items (set by BotDetectionMiddleware)
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
///     Background service that renders a live Spectre.Console display
///     showing detection results, throughput, and stats as they arrive.
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
    private readonly int _maxRows;

    // Stats
    private int _totalRequests;
    private int _totalBots;
    private int _totalHumans;
    private int _totalThreats; // prob >= 0.8
    private double _totalDetectionTimeMs;
    private double _maxDetectionTimeMs;
    private readonly DateTime _startTime = DateTime.Now;

    // Throughput tracking (sliding 10s window)
    private readonly ConcurrentQueue<DateTime> _recentRequests = new();

    // Top endpoints
    private readonly ConcurrentDictionary<string, int> _endpointHits = new();

    // Top bot signatures
    private readonly ConcurrentDictionary<string, int> _botSignatures = new();

    public LiveDetectionTableService(
        DetectionEventSink sink,
        string mode, string upstream, string port, string policy,
        bool useTls, bool tunnelEnabled,
        Func<string?>? tunnelUrlGetter = null, int maxRows = 15)
    {
        _sink = sink;
        _mode = mode;
        _upstream = upstream;
        _port = port;
        _policy = policy;
        _useTls = useTls;
        _tunnelEnabled = tunnelEnabled;
        _tunnelUrlGetter = tunnelUrlGetter;
        _maxRows = maxRows;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let startup complete
        await Task.Delay(2000, stoppingToken);

        var entries = new LinkedList<DetectionEntry>();
        var scheme = _useTls ? "https" : "http";

        await AnsiConsole.Live(BuildDisplay(entries, scheme))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    while (_sink.Reader.TryRead(out var entry))
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
                        else _totalHumans++;

                        // Normalize path (strip query string to prevent unbounded growth)
                        var normalizedPath = entry.Path.Split('?')[0];
                        _endpointHits.AddOrUpdate(normalizedPath, 1, (_, c) => c + 1);
                        _recentRequests.Enqueue(DateTime.Now);

                        // Trim dictionaries to prevent unbounded growth
                        if (_endpointHits.Count > 500) TrimDictionary(_endpointHits, 100);
                        if (_botSignatures.Count > 200) TrimDictionary(_botSignatures, 50);

                        while (entries.Count > _maxRows)
                            entries.RemoveLast();
                    }

                    // Trim throughput window to last 10 seconds
                    var cutoff = DateTime.Now.AddSeconds(-10);
                    while (_recentRequests.TryPeek(out var oldest) && oldest < cutoff)
                        _recentRequests.TryDequeue(out _);

                    ctx.UpdateTarget(BuildDisplay(entries, scheme));

                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        cts.CancelAfter(1000);
                        await _sink.Reader.WaitToReadAsync(cts.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Periodic refresh
                    }
                }
            });
    }

    private IRenderable BuildDisplay(LinkedList<DetectionEntry> entries, string scheme)
    {
        var uptime = DateTime.Now - _startTime;
        var reqPerSec = _recentRequests.Count / 10.0;
        var avgDetectionMs = _totalRequests > 0 ? _totalDetectionTimeMs / _totalRequests : 0;

        // === HEADER: Status line ===
        var threatPart = _totalThreats > 0
            ? $"[bold red]{_totalThreats} threats[/]"
            : "[green]0 threats[/]";
        var statusLine = $"[bold blue]stylobot[/] [dim]community edition - free forever |[/] Protected for [bold]{FormatUptime(uptime)}[/] [dim]|[/] [bold]{_totalRequests}[/] requests [dim]|[/] {threatPart}";

        // === LEFT COLUMN: Config + Stats ===
        var configPanel = new Table { Border = TableBorder.None, ShowHeaders = false };
        configPanel.AddColumn(new TableColumn("").Width(10).NoWrap());
        configPanel.AddColumn(new TableColumn("").NoWrap());
        configPanel.AddRow("[dim]Mode[/]", ModeMarkup(_mode));
        configPanel.AddRow("[dim]Policy[/]", PolicyMarkup(_policy));
        configPanel.AddRow("[dim]Upstream[/]", Markup.Escape(_upstream));
        configPanel.AddRow("[dim]Listen[/]", $"{scheme}://localhost:{_port}");
        if (_tunnelEnabled)
        {
            var tUrl = _tunnelUrlGetter?.Invoke();
            configPanel.AddRow("[dim]Tunnel[/]",
                tUrl != null ? $"[bold green]{Markup.Escape(tUrl)}[/]" : "[yellow]connecting...[/]");
        }

        // Stats panel
        var statsPanel = new Table { Border = TableBorder.None, ShowHeaders = false };
        statsPanel.AddColumn(new TableColumn("").Width(10).NoWrap());
        statsPanel.AddColumn(new TableColumn("").NoWrap());
        statsPanel.AddRow("[dim]Rate[/]", $"[bold]{reqPerSec:F1}[/] req/s");
        statsPanel.AddRow("[dim]Humans[/]", $"[bold green]{_totalHumans}[/]");
        statsPanel.AddRow("[dim]Bots[/]", $"[bold red]{_totalBots}[/]");
        statsPanel.AddRow("[dim]Threats[/]", _totalThreats > 0 ? $"[bold red on black] {_totalThreats} [/]" : "[dim]0[/]");
        statsPanel.AddRow("[dim]Avg time[/]", $"{avgDetectionMs:F1}ms");
        statsPanel.AddRow("[dim]Max time[/]", $"{_maxDetectionTimeMs:F1}ms");

        // Top endpoints (top 5)
        var endpointsTable = new Table { Border = TableBorder.Simple, Expand = true };
        endpointsTable.AddColumn(new TableColumn("[dim]Endpoint[/]"));
        endpointsTable.AddColumn(new TableColumn("[dim]Hits[/]").Width(6).RightAligned());
        var topEndpoints = _endpointHits
            .OrderByDescending(kv => kv.Value)
            .Take(5);
        foreach (var ep in topEndpoints)
        {
            var path = ep.Key.Length > 30 ? ep.Key[..27] + "..." : ep.Key;
            endpointsTable.AddRow(Markup.Escape(path), $"[bold]{ep.Value}[/]");
        }
        if (!_endpointHits.Any())
            endpointsTable.AddRow("[dim]waiting...[/]", "");

        // Top bots (top 5)
        var botsTable = new Table { Border = TableBorder.Simple, Expand = true };
        botsTable.AddColumn(new TableColumn("[dim]Bot Signature[/]"));
        botsTable.AddColumn(new TableColumn("[dim]Hits[/]").Width(6).RightAligned());
        var topBots = _botSignatures
            .OrderByDescending(kv => kv.Value)
            .Take(5);
        foreach (var bot in topBots)
        {
            var name = bot.Key.Length > 25 ? bot.Key[..22] + "..." : bot.Key;
            botsTable.AddRow($"[red]{Markup.Escape(name)}[/]", $"[bold]{bot.Value}[/]");
        }
        if (!_botSignatures.Any())
            botsTable.AddRow("[dim]none detected[/]", "");

        // Left sidebar
        var leftSide = new Rows(
            new Panel(configPanel) { Header = new PanelHeader("[bold]Config[/]"), Border = BoxBorder.Rounded, Expand = true },
            new Panel(statsPanel) { Header = new PanelHeader("[bold]Throughput[/]"), Border = BoxBorder.Rounded, Expand = true },
            new Panel(endpointsTable) { Header = new PanelHeader("[bold]Top Endpoints[/]"), Border = BoxBorder.Rounded, Expand = true },
            new Panel(botsTable) { Header = new PanelHeader("[bold]Top Bots[/]"), Border = BoxBorder.Rounded, Expand = true }
        );

        // === RIGHT COLUMN: Detection feed ===
        var detectionTable = new Table { Border = TableBorder.Rounded, Expand = true };
        detectionTable.AddColumn(new TableColumn("[dim]Time[/]").Width(8));
        detectionTable.AddColumn(new TableColumn("[dim]Path[/]"));
        detectionTable.AddColumn(new TableColumn("[dim]Risk[/]").Width(12));
        detectionTable.AddColumn(new TableColumn("[dim]Action[/]").Width(11));
        detectionTable.AddColumn(new TableColumn("[dim]ms[/]").Width(5));
        detectionTable.AddColumn(new TableColumn("[dim]Detector[/]"));
        detectionTable.AddColumn(new TableColumn("[dim]Identity[/]"));

        if (entries.Count == 0)
        {
            detectionTable.AddRow("[dim]Waiting for requests...[/]", "", "", "", "", "", "");
        }
        else
        {
            foreach (var e in entries)
            {
                var riskMarkup = FormatRisk(e.BotProbability);
                var actionMarkup = FormatAction(e);
                var timeColor = e.DetectionTimeMs > 200 ? "red"
                    : e.DetectionTimeMs > 50 ? "yellow"
                    : "dim";

                var path = e.Path.Length > 25 ? e.Path[..22] + "..." : e.Path;
                var detector = e.TopDetector.Length > 16 ? e.TopDetector[..13] + "..." : e.TopDetector;
                var identity = (e.BotName ?? "-");
                if (identity.Length > 18) identity = identity[..15] + "...";

                detectionTable.AddRow(
                    $"[dim]{e.Timestamp:HH:mm:ss}[/]",
                    Markup.Escape(path),
                    riskMarkup,
                    actionMarkup,
                    $"[{timeColor}]{e.DetectionTimeMs:F0}[/]",
                    Markup.Escape(detector),
                    Markup.Escape(identity));
            }
        }

        // === LAYOUT: responsive - side-by-side on wide terminals, stacked on narrow ===
        var termWidth = AnsiConsole.Profile.Width;
        IRenderable layout;
        if (termWidth >= 120)
        {
            layout = new Columns(
                new Padder(leftSide, new Padding(0, 0, 1, 0)),
                detectionTable
            );
        }
        else
        {
            // Narrow terminal: compact stats bar + full-width detection table
            var compactStats = new Markup(
                $"[bold blue]stylobot[/] [dim]|[/] {ModeMarkup(_mode)} [dim]|[/] {PolicyMarkup(_policy)} " +
                $"[dim]|[/] [bold]{reqPerSec:F1}[/]req/s [dim]|[/] [green]{_totalHumans}[/]ok [red]{_totalBots}[/]bot " +
                (_totalThreats > 0 ? $"[dim]|[/] [bold red]{_totalThreats}[/]threats" : ""));
            layout = new Rows(compactStats, detectionTable);
        }

        // === SUGGESTION BAR ===
        IRenderable suggestion;
        if (_totalThreats >= 5 && _policy.Equals("logonly", StringComparison.OrdinalIgnoreCase))
        {
            suggestion = new Panel(
                new Markup($"[bold yellow]! {_totalThreats} threats detected in observe-only mode.[/] To block: [bold]--policy block[/]"))
            { Border = BoxBorder.Heavy, BorderStyle = new Style(Color.Yellow) };
        }
        else
        {
            suggestion = new Markup("[dim]Press Ctrl+C to stop  |  --verbose for full logs[/]");
        }

        // === FINAL ASSEMBLY ===
        var root = new Rows(new Markup(statusLine), layout, suggestion);
        return root;
    }

    private static string FormatRisk(double prob)
    {
        if (prob >= 0.9) return $"[bold red on black] {prob:F2} HIGH [/]";
        if (prob >= 0.7) return $"[bold red]{prob:F2}[/] [red]RISK[/]";
        if (prob >= 0.4) return $"[yellow]{prob:F2}[/] [dim]MED[/]";
        return $"[green]{prob:F2}[/] [dim]LOW[/]";
    }

    private static string FormatAction(DetectionEntry e)
    {
        var policy = (e.ActionPolicy ?? "").ToLowerInvariant();
        if (e.Verdict == "HUMAN") return "[bold green]Allowed[/]";

        return policy switch
        {
            "block" or "block-hard" or "block-soft" => "[bold red]Blocked[/]",
            "challenge" or "challenge-pow" or "challenge-js" => "[bold yellow]Challenge[/]",
            "throttle" or "throttle-stealth" or "throttle-aggressive" => "[yellow]Throttled[/]",
            "logonly" or "shadow" or "debug" => "[dim]Monitored[/]",
            _ => e.BotProbability >= 0.5 ? "[dim]Monitored[/]" : "[bold green]Allowed[/]"
        };
    }

    private static string ModeMarkup(string mode) => mode.ToLowerInvariant() switch
    {
        "demo" => "[bold]OBSERVE ONLY[/] [dim](demo)[/]",
        "production" => "[bold green]ACTIVE PROTECTION[/]",
        "learning" => "[bold yellow]LEARNING[/]",
        _ => $"[bold]{Markup.Escape(mode.ToUpper())}[/]"
    };

    private static string PolicyMarkup(string policy) => policy.ToLowerInvariant() switch
    {
        "block" => "[bold red]Block[/]",
        "throttle" or "throttle-stealth" => "[bold yellow]Throttle[/]",
        "challenge" => "[bold yellow]Challenge[/]",
        "logonly" => "[dim]Observe only[/]",
        _ => $"[bold]{Markup.Escape(policy)}[/]"
    };

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{ts.Hours}h{ts.Minutes:D2}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m{ts.Seconds:D2}s";
        return $"{ts.Seconds}s";
    }

    private static void TrimDictionary(ConcurrentDictionary<string, int> dict, int keepTop)
    {
        var toKeep = dict.OrderByDescending(kv => kv.Value).Take(keepTop).Select(kv => kv.Key).ToHashSet();
        foreach (var key in dict.Keys)
            if (!toKeep.Contains(key))
                dict.TryRemove(key, out _);
    }
}

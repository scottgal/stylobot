using System.Text;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace Mostlylucid.BotDetection.Console.Services;

public static class RemoteDashboardTui
{
    public static async Task<int> RunAsync(string url, string? apiKey)
    {
        await using var service = new RemoteDashboardService(url, apiKey);
        service.Start();

        // Mutable state shared between background drain and UI render
        State<RemoteStats> stats = new(RemoteStats.Empty);
        State<IReadOnlyList<RemoteBotEntry>> topBots = new([]);
        State<string> status = new($"Connecting to {url}...");
        var exitFlag = new int[1]; // [0]=0 running, 1=exit; Interlocked.Exchange for thread safety
        var lastDetectionTime = DateTime.MinValue;

        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Interlocked.Exchange(ref exitFlag[0], 1);
        };

        // Detection feed - LogControl provides scrollback + search (Ctrl+F)
        var feed = new LogControl
        {
            FollowTail  = true,
            MaxCapacity = 500,
            WrapText    = false
        };

        // Title/header
        var header = new Group($" stylobot dashboard  \u00b7  {url} ")
            .Content(new HStack(
                new TextBlock(() => $" \u2713 {stats.Value.HumanRequests:N0} ok  "),
                new TextBlock(() => $"\u2717 {stats.Value.BotRequests:N0} bots  "),
                new TextBlock(() => $"\u26a0 {HighCount(stats.Value)} high  "),
                new TextBlock(() => $"\u00b7 {stats.Value.UniqueSignatures} sigs  "),
                new TextBlock(() => BuildRps(stats.Value))));

        // Right sidebar
        var sidebar = new VStack(
            new Group(" Top Bots ")
                .Content(new TextBlock(() => FormatTopBots(topBots.Value))),
            new Group(" Risk Bands ")
                .Content(new TextBlock(() => FormatRiskBands(stats.Value))));

        // Main layout
        var root = new DockLayout
        {
            Top = header,
            Content = new HStack(
                new Group(" Detections ").Content(feed),
                sidebar),
            Bottom = new TextBlock(() =>
                $"  {status.Value}  |  Ctrl+F search  |  Ctrl+C quit")
        };

        Terminal.Run(root, onUpdate: () =>
        {
            // Drain snapshots on the UI thread (safe to update State<T> and call feed.AppendLine here)
            while (service.Updates.TryRead(out var snap))
            {
                stats.Value   = snap.Stats;
                topBots.Value = snap.TopBots;
                status.Value  = snap.ConnectionStatus;

                foreach (var d in snap.RecentDetections
                    .OrderBy(x => x.Timestamp)
                    .Where(x => x.Timestamp > lastDetectionTime))
                {
                    feed.AppendLine(FormatDetectionLine(d));
                    lastDetectionTime = d.Timestamp;
                }
            }

            return Interlocked.CompareExchange(ref exitFlag[0], 0, 0) == 1
                ? TerminalLoopResult.StopAndKeepVisual
                : TerminalLoopResult.Continue;
        });

        return 0;
    }

    // ── Formatters ────────────────────────────────────────────────────────────

    private static int HighCount(RemoteStats s) =>
        s.RiskBandCounts.TryGetValue("High", out var h) ? h : 0;

    private static string BuildRps(RemoteStats s)
    {
        // Show bot ratio as a simple bar
        var total = s.TotalRequests;
        if (total == 0) return "";
        var pct = s.BotRequests * 100 / total;
        return $" {pct}% bots";
    }

    private static string FormatTopBots(IReadOnlyList<RemoteBotEntry> bots)
    {
        if (bots.Count == 0) return "  (none yet)";
        var sb = new StringBuilder();
        foreach (var b in bots.Take(8))
        {
            var name = b.BotName ?? b.Signature[..Math.Min(18, b.Signature.Length)];
            sb.AppendLine($"  \u25a0 {name,-18} {b.HitCount,5}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatRiskBands(RemoteStats s)
    {
        if (s.TotalRequests == 0) return "  (no data)";
        var sb = new StringBuilder();
        foreach (var (band, count) in s.RiskBandCounts.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {band,-8} {count,6:N0}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatDetectionLine(RemoteDetectionEntry d)
    {
        var pct    = $"{d.BotProbability * 100:F0}%";
        var path   = d.Path.Length > 35 ? d.Path[..34] + "\u2026" : d.Path;
        var action = d.Action ?? (d.BotProbability >= 0.5 ? "bot " : "ok  ");
        var cc     = d.Country != null ? $" [{d.Country}]" : "";
        return $"  {d.Timestamp:HH:mm:ss}  {path,-35}  {pct,4}  {action,-8}{cc}";
    }
}

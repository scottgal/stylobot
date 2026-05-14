using System.Text;
using SysConsole = System.Console;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Inline live renderer for <see cref="StartupStatusBoard"/>. Uses raw ANSI
///     (cursor up + line clear) so it stays AOT-friendly and does not pull in
///     Spectre.Console. Designed to run BEFORE the alt-screen
///     <c>LiveDetectionTableService</c> takes over: rendering happens inline,
///     and <see cref="StopAsync"/> leaves a clean final-state block on the
///     scrollback so operators can audit what happened.
/// </summary>
public sealed class StartupBannerRenderer
{
    private static readonly string[] Spinner =
        { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    private readonly StartupStatusBoard _board;
    private readonly string _title;
    private readonly TimeSpan _refreshInterval;
    private int _renderedLines;
    private long _lastSeenVersion = -1;
    private int _spinnerStep;
    private bool _started;

    public StartupBannerRenderer(StartupStatusBoard board, string? title = null, TimeSpan? refreshInterval = null)
    {
        _board = board;
        _title = title ?? "StyloBot starting up";
        _refreshInterval = refreshInterval ?? TimeSpan.FromMilliseconds(120);
    }

    public async Task RunUntilSettledAsync(CancellationToken ct)
    {
        // Hide the cursor while we redraw to keep flicker low. Restored in StopAsync.
        SysConsole.Write("\x1b[?25l");
        _started = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                RenderFrame();
                if (_board.AllSettled() && _board.Snapshot().Count > 0) return;
                try { await Task.Delay(_refreshInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            // Final paint so the post-run scrollback shows accurate state.
            RenderFrame(final: true);
            SysConsole.Write("\x1b[?25h");
            SysConsole.WriteLine();
            _started = false;
        }
    }

    /// <summary>Force a final paint and tear down (used on early failure paths).</summary>
    public void StopAndFlush()
    {
        if (!_started) return;
        RenderFrame(final: true);
        SysConsole.Write("\x1b[?25h");
        SysConsole.WriteLine();
        _started = false;
    }

    private void RenderFrame(bool final = false)
    {
        var version = _board.Version;
        if (!final && version == _lastSeenVersion) { _spinnerStep++; }
        _lastSeenVersion = version;
        _spinnerStep++;

        var snap = _board.Snapshot();

        // Move cursor up over the previously rendered block.
        var sb = new StringBuilder();
        if (_renderedLines > 0)
            sb.Append('\x1b').Append('[').Append(_renderedLines).Append('A');

        // Title row.
        sb.Append("\x1b[K");
        sb.Append("\x1b[1m").Append(_title).Append("\x1b[0m\n");
        var lines = 1;

        foreach (var task in snap)
        {
            sb.Append("\x1b[K");
            sb.Append(' ').Append(GlyphFor(task)).Append(' ');
            sb.Append(LabelColor(task)).Append(task.Label).Append("\x1b[0m");
            if (!string.IsNullOrEmpty(task.Detail))
            {
                sb.Append("  \x1b[2m— ").Append(task.Detail).Append("\x1b[0m");
            }
            if (task.State is StartupTaskState.Done or StartupTaskState.Failed)
            {
                sb.Append("  \x1b[2m(").Append(FormatElapsed(task.Elapsed)).Append(")\x1b[0m");
            }
            sb.Append('\n');
            lines++;
        }

        // If the previous frame had more lines than this one, blank the extras.
        for (var i = lines; i < _renderedLines; i++)
        {
            sb.Append("\x1b[K\n");
            lines++;
        }
        _renderedLines = lines;

        SysConsole.Write(sb.ToString());
    }

    private string GlyphFor(StartupTask t) => t.State switch
    {
        StartupTaskState.Done    => "\x1b[32m✓\x1b[0m",  // green check
        StartupTaskState.Failed  => "\x1b[31m✗\x1b[0m",  // red cross
        StartupTaskState.Skipped => "\x1b[2m—\x1b[0m",   // dim em-dash
        StartupTaskState.Running => "\x1b[36m" + Spinner[_spinnerStep % Spinner.Length] + "\x1b[0m",
        _                        => "\x1b[2m·\x1b[0m",   // pending: dim dot
    };

    private static string LabelColor(StartupTask t) => t.State switch
    {
        StartupTaskState.Failed => "\x1b[31m",                 // red
        StartupTaskState.Skipped => "\x1b[2m",                 // dim
        StartupTaskState.Pending => "\x1b[2m",
        _ => string.Empty
    };

    private static string FormatElapsed(TimeSpan ts)
        => ts.TotalSeconds switch
        {
            < 1 => $"{ts.TotalMilliseconds:F0}ms",
            < 60 => $"{ts.TotalSeconds:F1}s",
            _ => $"{(int)ts.TotalMinutes}m{ts.Seconds:00}s"
        };
}

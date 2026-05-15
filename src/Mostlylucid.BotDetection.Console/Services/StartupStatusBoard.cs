using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Console.Services;

public enum StartupTaskState
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped
}

public sealed record StartupTask(
    string Id,
    string Label,
    StartupTaskState State,
    string? Detail,
    DateTime StartedUtc,
    DateTime? FinishedUtc)
{
    public TimeSpan Elapsed => (FinishedUtc ?? DateTime.UtcNow) - StartedUtc;
}

/// <summary>
///     Thread-safe board of startup task state. Insertion order is preserved so
///     the renderer can display tasks in declaration order. Update events bump a
///     monotonic <see cref="Version"/> token so the renderer can detect changes
///     without polling individual rows.
/// </summary>
public sealed class StartupStatusBoard
{
    private readonly ConcurrentDictionary<string, StartupTask> _tasks = new();
    private readonly List<string> _order = new();
    private readonly object _orderLock = new();
    private readonly ILogger? _echoLogger;
    private long _version;

    public StartupStatusBoard(ILogger? echoLogger = null)
    {
        _echoLogger = echoLogger;
    }

    public long Version => Interlocked.Read(ref _version);

    public IReadOnlyList<StartupTask> Snapshot()
    {
        lock (_orderLock)
        {
            var result = new List<StartupTask>(_order.Count);
            foreach (var id in _order)
                if (_tasks.TryGetValue(id, out var t))
                    result.Add(t);
            return result;
        }
    }

    public void Start(string id, string label, string? detail = null)
        => Upsert(id, label, StartupTaskState.Running, detail, finished: false);

    public void Done(string id, string? detail = null)
        => Mark(id, StartupTaskState.Done, detail);

    public void Fail(string id, string? detail = null)
        => Mark(id, StartupTaskState.Failed, detail);

    public void Skip(string id, string label, string? detail = null)
        => Upsert(id, label, StartupTaskState.Skipped, detail, finished: true);

    private void Upsert(string id, string label, StartupTaskState state, string? detail, bool finished)
    {
        var now = DateTime.UtcNow;
        _tasks.AddOrUpdate(id,
            _ =>
            {
                lock (_orderLock)
                    if (!_order.Contains(id)) _order.Add(id);
                return new StartupTask(id, label, state, detail, now, finished ? now : null);
            },
            (_, existing) => existing with
            {
                Label = label,
                State = state,
                Detail = detail ?? existing.Detail,
                FinishedUtc = finished ? now : existing.FinishedUtc
            });
        Interlocked.Increment(ref _version);
        Echo(id, label, state, detail);
    }

    private void Mark(string id, StartupTaskState state, string? detail)
    {
        var now = DateTime.UtcNow;
        if (_tasks.TryGetValue(id, out var existing))
        {
            _tasks[id] = existing with
            {
                State = state,
                Detail = detail ?? existing.Detail,
                FinishedUtc = now
            };
            Interlocked.Increment(ref _version);
            Echo(id, existing.Label, state, detail);
        }
    }

    private void Echo(string id, string label, StartupTaskState state, string? detail)
    {
        if (_echoLogger is null) return;
        var detailSuffix = string.IsNullOrEmpty(detail) ? "" : $" — {detail}";
        var message = $"[startup:{id}] {label}: {state}{detailSuffix}";
        var level = state switch
        {
            StartupTaskState.Failed => LogLevel.Warning,
            StartupTaskState.Skipped => LogLevel.Debug,
            _ => LogLevel.Information
        };
        _echoLogger.Log(level, "{StartupTask}", message);
    }

    /// <summary>True when every registered task is in a terminal state (Done/Failed/Skipped).</summary>
    public bool AllSettled()
    {
        foreach (var t in Snapshot())
            if (t.State is StartupTaskState.Pending or StartupTaskState.Running)
                return false;
        return true;
    }
}

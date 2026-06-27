using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.Atoms.Ephemeral;

/// <summary>
///     Tick-driven LLM work executor. Subscribes to a configurable
///     <see cref="IScheduleCoordinator"/> cadence; on each tick fire, asks the
///     <see cref="IEphemeralPicker{TItem}"/> for hot items needing work, runs
///     up to <see cref="EphemeralLlmCoordinatorOptions.MaxConcurrent"/>
///     invocations in parallel through <see cref="IEphemeralLlmInvoker{TResult}"/>,
///     and persists successes via <see cref="IEphemeralWriteback{TItem,TResult}"/>.
///     Failed invocations log + fault-count and skip writeback; the picker
///     surfaces them again next tick.
/// </summary>
public sealed class EphemeralLlmCoordinator<TItem, TResult> : IDisposable
{
    private readonly IEphemeralPicker<TItem> _picker;
    private readonly IEphemeralPrompter<TItem> _prompter;
    private readonly IEphemeralLlmInvoker<TResult> _invoker;
    private readonly IEphemeralWriteback<TItem, TResult> _writeback;
    private readonly EphemeralLlmCoordinatorOptions _opts;
    private readonly ILogger? _log;
    private readonly IDisposable _subscription;

    public EphemeralLlmCoordinator(
        IEphemeralPicker<TItem> picker,
        IEphemeralPrompter<TItem> prompter,
        IEphemeralLlmInvoker<TResult> invoker,
        IEphemeralWriteback<TItem, TResult> writeback,
        IScheduleCoordinator schedule,
        IOptions<EphemeralLlmCoordinatorOptions> options,
        ILogger<EphemeralLlmCoordinator<TItem, TResult>>? log = null)
    {
        _picker = picker;
        _prompter = prompter;
        _invoker = invoker;
        _writeback = writeback;
        _opts = options.Value;
        _log = log;

        _subscription = schedule.Subscribe(
            _opts.Cadence,
            _opts.SubscriberName,
            CostHint.High,
            OnTickAsync);
    }

    private async Task OnTickAsync(DateTimeOffset _, CancellationToken ct)
    {
        var picked = _picker.Pick(_opts.MaxItemsPerTick);
        if (picked.Count == 0) return;

        using var sem = new SemaphoreSlim(_opts.MaxConcurrent, _opts.MaxConcurrent);
        var tasks = new List<Task>(picked.Count);
        foreach (var item in picked)
            tasks.Add(ProcessOneAsync(item, sem, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessOneAsync(TItem item, SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            var prompt = _prompter.Build(item);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_opts.InvocationTimeout);
            TResult result;
            try
            {
                result = await _invoker.InvokeAsync(prompt, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _log?.LogWarning("Ephemeral LLM invocation timed out for {Subscriber}", _opts.SubscriberName);
                return;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Ephemeral LLM invocation failed for {Subscriber}", _opts.SubscriberName);
                return;
            }

            try
            {
                await _writeback.ApplyAsync(item, result, ct);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Ephemeral LLM writeback failed for {Subscriber}", _opts.SubscriberName);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public void Dispose() => _subscription.Dispose();
}

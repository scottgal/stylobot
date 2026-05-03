using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Learning;

/// <summary>
///     Keyed learning coordinator - allows parallel learning workflows by signal type.
///     Architecture:
///     - Multiple learning workflows can run in parallel, keyed by signal type
///     - Each key (e.g., "ua.pattern", "ip.reputation", "tls.fingerprint") has its own queue
///     - Prevents a slow learner from blocking fast learners
///     - Thread-safe and non-blocking from request path
///     Example keys:
///     - "ua.pattern" - User-Agent pattern learning
///     - "ip.reputation" - IP reputation updates
///     - "tls.ja3" - JA3 fingerprint learning
///     - "behavior.waveform" - Behavioral waveform pattern extraction
///     - "heuristic.weights" - Heuristic weight updates
/// </summary>
public interface ILearningCoordinator
{
    /// <summary>
    ///     Submit a learning task for a specific signal key.
    ///     Non-blocking - returns immediately.
    /// </summary>
    /// <param name="signalKey">Learning workflow key (e.g., "ua.pattern", "ip.reputation")</param>
    /// <param name="learningTask">The learning task to execute</param>
    /// <returns>True if queued successfully, false if queue is full</returns>
    bool TrySubmitLearning(string signalKey, LearningTask learningTask);

    /// <summary>
    ///     Get statistics for a specific learning key.
    /// </summary>
    KeyedLearningStats? GetStats(string signalKey);

    /// <summary>
    ///     Get all active learning keys and their stats.
    /// </summary>
    IReadOnlyDictionary<string, KeyedLearningStats> GetAllStats();

    /// <summary>
    ///     Shutdown the coordinator gracefully.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     A learning task to be executed asynchronously.
/// </summary>
public class LearningTask
{
    /// <summary>Source detector or component that generated this task</summary>
    public required string Source { get; init; }

    /// <summary>Type of learning operation</summary>
    public required LearningOperationType OperationType { get; init; }

    /// <summary>Feature vector for supervised learning</summary>
    public Dictionary<string, double>? Features { get; init; }

    /// <summary>Label for supervised learning (true = bot)</summary>
    public bool? Label { get; init; }

    /// <summary>Confidence score</summary>
    public double? Confidence { get; init; }

    /// <summary>Pattern string (for pattern extraction)</summary>
    public string? Pattern { get; init; }

    /// <summary>Request ID for correlation</summary>
    public string? RequestId { get; init; }

    /// <summary>Additional metadata</summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>Timestamp when task was created</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
///     Types of learning operations.
/// </summary>
public enum LearningOperationType
{
    /// <summary>Update pattern database (add new pattern or update confidence)</summary>
    PatternUpdate,

    /// <summary>Train ML model with new data</summary>
    ModelTraining,

    /// <summary>Update heuristic weights based on feedback</summary>
    WeightUpdate,

    /// <summary>Extract patterns from high-confidence detections</summary>
    PatternExtraction,

    /// <summary>Update reputation database</summary>
    ReputationUpdate,

    /// <summary>Analyze drift between fast-path and full detection</summary>
    DriftAnalysis,

    /// <summary>Consolidate learned patterns into fast-path rules</summary>
    RuleConsolidation
}

/// <summary>
///     Statistics for a keyed learning workflow.
/// </summary>
public class KeyedLearningStats
{
    public string SignalKey { get; init; } = string.Empty;
    public long TotalTasksSubmitted { get; set; }
    public long TotalTasksCompleted { get; set; }
    public long TotalTasksFailed { get; set; }
    public long TotalTasksDropped { get; set; }
    public int QueuedTasks { get; set; }
    public double AverageProcessingTimeMs { get; set; }
    public DateTimeOffset? LastTaskCompletedAt { get; set; }
    public DateTimeOffset? LastTaskFailedAt { get; set; }
}

/// <summary>
///     Keyed learning coordinator implementation using per-key background processors.
/// </summary>
public class LearningCoordinator : ILearningCoordinator, IDisposable
{
    private readonly IEnumerable<IKeyedLearningHandler> _handlers;
    private readonly ILogger<LearningCoordinator> _logger;
    private readonly int _maxQueueSize;
    private readonly ConcurrentDictionary<string, KeyedLearningQueue> _queues = new();
    private readonly SemaphoreSlim _shutdownLock = new(1, 1);
    private readonly ConcurrentDictionary<string, KeyedLearningStats> _stats = new();
    private bool _disposed;
    private bool _isShuttingDown;

    public LearningCoordinator(
        ILogger<LearningCoordinator> logger,
        IEnumerable<IKeyedLearningHandler> handlers,
        int maxQueueSize = 1000)
    {
        _logger = logger;
        _handlers = handlers;
        _maxQueueSize = maxQueueSize;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _isShuttingDown = true;
        _shutdownLock.Dispose();

        foreach (var queue in _queues.Values) queue.Dispose();

        _queues.Clear();
    }

    public bool TrySubmitLearning(string signalKey, LearningTask learningTask)
    {
        if (_isShuttingDown || _disposed)
        {
            _logger.LogWarning("Learning coordinator is shutting down, cannot submit new tasks");
            return false;
        }

        // Get or create queue for this signal key
        var queue = _queues.GetOrAdd(signalKey, key =>
        {
            _logger.LogInformation("Creating new learning queue for signal key: {SignalKey}", key);
            var stats = new KeyedLearningStats { SignalKey = key };
            _stats[key] = stats;

            var newQueue = new KeyedLearningQueue(key, _maxQueueSize, _logger);

            // Start background processor for this queue
            _ = Task.Run(async () => await ProcessQueueAsync(key, newQueue));

            return newQueue;
        });

        // Try to enqueue the task
        if (queue.TryEnqueue(learningTask))
        {
            var stats = _stats[signalKey];
            stats.TotalTasksSubmitted++;
            stats.QueuedTasks = queue.Count;
            return true;
        }

        // Queue is full - drop the task
        var statsForDrop = _stats[signalKey];
        statsForDrop.TotalTasksDropped++;

        _logger.LogWarning(
            "Learning queue for {SignalKey} is full ({Count}/{Max}), dropping task",
            signalKey, queue.Count, _maxQueueSize);

        return false;
    }

    public KeyedLearningStats? GetStats(string signalKey)
    {
        return _stats.TryGetValue(signalKey, out var stats) ? stats : null;
    }

    public IReadOnlyDictionary<string, KeyedLearningStats> GetAllStats()
    {
        return _stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _shutdownLock.WaitAsync(cancellationToken);
        try
        {
            if (_isShuttingDown)
                return;

            _logger.LogInformation("Learning coordinator shutting down...");
            _isShuttingDown = true;

            // Wait for queues to drain (with timeout)
            var drainTimeout = TimeSpan.FromSeconds(30);
            var stopwatch = Stopwatch.StartNew();

            foreach (var kvp in _queues)
            {
                var remaining = drainTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    _logger.LogWarning(
                        "Shutdown timeout exceeded, {Count} queues may have pending tasks",
                        _queues.Count);
                    break;
                }

                while (kvp.Value.Count > 0 && stopwatch.Elapsed < drainTimeout)
                    await Task.Delay(100, cancellationToken);

                _logger.LogInformation(
                    "Queue {SignalKey} drained ({Completed} tasks completed)",
                    kvp.Key, _stats[kvp.Key].TotalTasksCompleted);
            }

            _logger.LogInformation("Learning coordinator shutdown complete");
        }
        finally
        {
            _shutdownLock.Release();
        }
    }

    private async Task ProcessQueueAsync(string signalKey, KeyedLearningQueue queue)
    {
        _logger.LogInformation("Learning processor started for signal key: {SignalKey}", signalKey);

        try
        {
            while (!_isShuttingDown)
            {
                // Wait for next task (with timeout to check shutdown)
                var task = await queue.DequeueAsync(TimeSpan.FromSeconds(1));

                if (task == null)
                    continue; // Timeout, check shutdown flag

                await ProcessLearningTaskAsync(signalKey, task);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in learning processor for {SignalKey}", signalKey);
        }

        _logger.LogInformation("Learning processor stopped for signal key: {SignalKey}", signalKey);
    }

    private async Task ProcessLearningTaskAsync(string signalKey, LearningTask task)
    {
        var stats = _stats[signalKey];
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            _logger.LogDebug(
                "Processing learning task: key={SignalKey}, operation={Operation}, source={Source}",
                signalKey, task.OperationType, task.Source);

            // Find handlers that can process this signal key
            var relevantHandlers = _handlers
                .Where(h => h.CanHandle(signalKey, task.OperationType))
                .ToList();

            if (relevantHandlers.Count == 0)
            {
                _logger.LogWarning(
                    "No handlers found for signal key: {SignalKey}, operation: {Operation}",
                    signalKey, task.OperationType);
                return;
            }

            // Execute all relevant handlers (could be parallel if independent)
            foreach (var handler in relevantHandlers)
                try
                {
                    await handler.HandleAsync(signalKey, task, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Handler {Handler} failed for signal key {SignalKey}",
                        handler.GetType().Name, signalKey);
                }

            // Update stats
            var elapsed = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
            stats.TotalTasksCompleted++;
            stats.LastTaskCompletedAt = DateTimeOffset.UtcNow;

            // Update rolling average
            var previousAvg = stats.AverageProcessingTimeMs;
            var totalCompleted = stats.TotalTasksCompleted;
            stats.AverageProcessingTimeMs =
                (previousAvg * (totalCompleted - 1) + elapsed) / totalCompleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing learning task for signal key: {SignalKey}",
                signalKey);

            stats.TotalTasksFailed++;
            stats.LastTaskFailedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            // Update queued count
            stats.QueuedTasks = _queues[signalKey].Count;
        }
    }
}

/// <summary>
///     Thread-safe queue for a specific signal key.
/// </summary>
internal class KeyedLearningQueue : IDisposable
{
    private readonly ILogger _logger;
    private readonly int _maxSize;
    private readonly ConcurrentQueue<LearningTask> _queue = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly string _signalKey;
    private int _currentCount;
    private bool _disposed;

    public KeyedLearningQueue(string signalKey, int maxSize, ILogger logger)
    {
        _signalKey = signalKey;
        _maxSize = maxSize;
        _logger = logger;
        _semaphore = new SemaphoreSlim(0);
    }

    public int Count => _currentCount;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _semaphore.Dispose();
    }

    public bool TryEnqueue(LearningTask task)
    {
        if (_disposed)
            return false;

        // Check if queue is full
        if (_currentCount >= _maxSize)
            return false;

        _queue.Enqueue(task);
        Interlocked.Increment(ref _currentCount);
        _semaphore.Release(); // Signal that item is available
        return true;
    }

    public async Task<LearningTask?> DequeueAsync(TimeSpan timeout)
    {
        if (_disposed)
            return null;

        // Wait for item to be available (or timeout)
        if (!await _semaphore.WaitAsync(timeout))
            return null; // Timeout

        if (_queue.TryDequeue(out var task))
        {
            Interlocked.Decrement(ref _currentCount);
            return task;
        }

        return null;
    }
}

/// <summary>
///     Handler for keyed learning operations.
/// </summary>
public interface IKeyedLearningHandler
{
    /// <summary>
    ///     Check if this handler can process the given signal key and operation type.
    /// </summary>
    bool CanHandle(string signalKey, LearningOperationType operationType);

    /// <summary>
    ///     Handle the learning task for the given signal key.
    /// </summary>
    Task HandleAsync(string signalKey, LearningTask task, CancellationToken cancellationToken = default);
}
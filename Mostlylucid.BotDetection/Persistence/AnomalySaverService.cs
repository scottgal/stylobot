using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Persistence;

/// <summary>
///     Background service that saves bot detection events to rolling JSON files.
///     Uses write-through semantics with SHORT tracking windows - entries are cleared quickly after writing.
///     Adapts to write pressure automatically using bounded channels.
/// </summary>
public class AnomalySaverService : BackgroundService
{
    // Batch buffer - cleared quickly after write (SHORT window)
    private readonly List<DetectionEvent> _batchBuffer = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private readonly ILearningEventBus? _eventBus;

    // Per-file lock to ensure sequential writes (keyed by file path)
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private readonly RollingFileManager _fileManager;
    private readonly ILogger<AnomalySaverService> _logger;
    private readonly AnomalySaverOptions _options;

    // Bounded channel for write-through buffering with backpressure
    private readonly Channel<DetectionEvent> _writeChannel;
    private DateTime _lastFlush = DateTime.UtcNow;

    public AnomalySaverService(
        ILogger<AnomalySaverService> logger,
        IOptions<BotDetectionOptions> options,
        ILearningEventBus? eventBus = null)
    {
        _logger = logger;
        _options = options.Value.AnomalySaver;
        _eventBus = eventBus;
        _fileManager = new RollingFileManager(_options);

        // Create bounded channel - backpressure kicks in when full
        // Short buffer = quick clear after write
        var channelOptions = new BoundedChannelOptions(_options.BatchSize * 2)
        {
            FullMode = BoundedChannelFullMode.Wait // Backpressure
        };
        _writeChannel = Channel.CreateBounded<DetectionEvent>(channelOptions);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Anomaly saver is disabled");
            return;
        }

        _logger.LogInformation(
            "Anomaly saver started: path={Path}, threshold={Threshold:P0}, batch={BatchSize}, flush={FlushInterval}",
            _options.OutputPath,
            _options.MinBotProbabilityThreshold,
            _options.BatchSize,
            _options.FlushInterval);

        // Background processing loops
        var tasks = new List<Task>
        {
            WriterLoopAsync(stoppingToken),
            FlushTimerAsync(stoppingToken)
        };

        // Add learning event bus reader if available
        if (_eventBus != null) tasks.Add(LearningEventReaderAsync(stoppingToken));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    ///     Main writer loop - reads from channel and batches writes.
    ///     SHORT tracking window: batch is cleared immediately after successful write.
    /// </summary>
    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _writeChannel.Reader;

        while (!cancellationToken.IsCancellationRequested)
            try
            {
                // Wait for next event
                if (await reader.WaitToReadAsync(cancellationToken))
                    while (reader.TryRead(out var evt))
                    {
                        await _batchLock.WaitAsync(cancellationToken);
                        try
                        {
                            _batchBuffer.Add(evt);

                            // Adaptive batching: flush when batch is full OR timeout elapsed
                            var shouldFlush = _batchBuffer.Count >= _options.BatchSize ||
                                              DateTime.UtcNow - _lastFlush >= _options.FlushInterval;

                            if (shouldFlush) await FlushBatchAsync(cancellationToken);
                        }
                        finally
                        {
                            _batchLock.Release();
                        }
                    }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in writer loop");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
    }

    /// <summary>
    ///     Timer-based flush - ensures events don't sit in buffer too long.
    /// </summary>
    private async Task FlushTimerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.FlushInterval);

        while (!cancellationToken.IsCancellationRequested)
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);

                await _batchLock.WaitAsync(cancellationToken);
                try
                {
                    if (_batchBuffer.Count > 0) await FlushBatchAsync(cancellationToken);
                }
                finally
                {
                    _batchLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in flush timer");
            }
    }

    /// <summary>
    ///     Flushes the batch buffer to file.
    ///     SHORT window: buffer is cleared immediately after write (entries don't linger).
    /// </summary>
    private async Task FlushBatchAsync(CancellationToken cancellationToken)
    {
        if (_batchBuffer.Count == 0) return;

        var currentFile = _fileManager.GetCurrentFile();
        var eventsToWrite = _batchBuffer.ToList();

        // CLEAR QUICKLY: Empty buffer immediately (SHORT tracking window)
        _batchBuffer.Clear();
        _lastFlush = DateTime.UtcNow;

        // Get or create file lock for sequential writes per file path
        var fileLock = _fileLocks.GetOrAdd(currentFile, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync(cancellationToken);
        try
        {
            await WriteToFileAsync(currentFile, eventsToWrite, cancellationToken);

            // Check if we need to roll the file
            _fileManager.CheckAndRoll();
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    ///     Writes events to file (newline-delimited JSON).
    /// </summary>
    private async Task WriteToFileAsync(
        string filePath,
        List<DetectionEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var writer = new StreamWriter(filePath, true);

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            foreach (var evt in events)
            {
                var json = JsonSerializer.Serialize(evt, jsonOptions);
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            }

            await writer.FlushAsync(cancellationToken);

            _logger.LogDebug(
                "Wrote {Count} detection events to {File}",
                events.Count,
                Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write detection batch to file {File}", filePath);
        }
    }

    /// <summary>
    ///     Reads learning events from the event bus and queues them for writing.
    /// </summary>
    private async Task LearningEventReaderAsync(CancellationToken cancellationToken)
    {
        var reader = _eventBus!.Reader;

        while (!cancellationToken.IsCancellationRequested)
            try
            {
                if (await reader.WaitToReadAsync(cancellationToken))
                    while (reader.TryRead(out var evt))
                        await HandleLearningEventAsync(evt, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading learning events");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
    }

    /// <summary>
    ///     Handles learning events and queues them for writing.
    /// </summary>
    private async Task HandleLearningEventAsync(LearningEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            // Extract bot probability from metadata
            if (evt.Metadata == null || !evt.Metadata.TryGetValue("botProbability", out var probObj) || probObj == null)
                return;

            var botProbability = Convert.ToDouble(probObj);

            // Filter by threshold
            if (botProbability < _options.MinBotProbabilityThreshold)
                return;

            // Create detection event
            var detectionEvent = new DetectionEvent
            {
                Timestamp = DateTime.UtcNow,
                RequestId = evt.RequestId,
                BotProbability = botProbability,
                Confidence = evt.Confidence ?? 0.0,
                IsBot = evt.Label ?? false,
                RiskBand = evt.Metadata.TryGetValue("riskBand", out var rb) ? rb?.ToString() : null,
                UserAgent = evt.Metadata.TryGetValue("userAgent", out var ua) ? ua?.ToString() : null,
                IpAddress = evt.Metadata.TryGetValue("ip", out var ip) ? ip?.ToString() : null,
                Path = evt.Metadata.TryGetValue("path", out var path) ? path?.ToString() : null,
                ProcessingTimeMs = evt.Metadata.TryGetValue("processingTimeMs", out var time)
                    ? Convert.ToDouble(time)
                    : 0,
                ContributingDetectors = evt.Metadata.TryGetValue("contributingDetectors", out var detectors)
                    ? detectors as List<string>
                    : null,
                FailedDetectors = evt.Metadata.TryGetValue("failedDetectors", out var failed)
                    ? failed as List<string>
                    : null,
                CategoryBreakdown = evt.Metadata.TryGetValue("categoryBreakdown", out var breakdown) ? breakdown : null
            };

            // Write to channel - backpressure handled automatically
            // If channel is full, this will wait (adapts to write pressure)
            await _writeChannel.Writer.WriteAsync(detectionEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle learning event for anomaly saving");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Anomaly saver stopping, flushing remaining events...");

        // Complete the channel (no more writes)
        _writeChannel.Writer.Complete();

        // Flush any remaining batched events
        await _batchLock.WaitAsync(cancellationToken);
        try
        {
            if (_batchBuffer.Count > 0) await FlushBatchAsync(cancellationToken);
        }
        finally
        {
            _batchLock.Release();
        }

        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
///     Manages rolling file creation and rotation.
/// </summary>
internal class RollingFileManager
{
    private readonly AnomalySaverOptions _options;
    private string? _currentFile;
    private DateTime _currentFileStartTime;

    public RollingFileManager(AnomalySaverOptions options)
    {
        _options = options;

        // Ensure output directory exists
        if (!string.IsNullOrEmpty(_options.OutputPath))
        {
            var directory = Path.GetDirectoryName(_options.OutputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
        }
    }

    public string GetCurrentFile()
    {
        if (_currentFile == null || ShouldRoll()) Roll();

        return _currentFile!;
    }

    public void CheckAndRoll()
    {
        if (ShouldRoll()) Roll();
    }

    private bool ShouldRoll()
    {
        if (_currentFile == null) return true;

        // Check time-based rolling
        if (DateTime.UtcNow - _currentFileStartTime >= _options.RollingInterval) return true;

        // Check size-based rolling
        if (_options.MaxFileSizeBytes > 0 && File.Exists(_currentFile))
        {
            var fileInfo = new FileInfo(_currentFile);
            if (fileInfo.Length >= _options.MaxFileSizeBytes) return true;
        }

        return false;
    }

    private void Roll()
    {
        _currentFileStartTime = DateTime.UtcNow;

        // Generate timestamped filename
        var timestamp = _currentFileStartTime.ToString("yyyyMMdd-HHmmss");
        var baseFileName = Path.GetFileNameWithoutExtension(_options.OutputPath);
        var extension = Path.GetExtension(_options.OutputPath);
        var directory = Path.GetDirectoryName(_options.OutputPath) ?? ".";

        _currentFile = Path.Combine(directory, $"{baseFileName}-{timestamp}{extension}");

        // Clean up old files if retention policy is set
        CleanupOldFiles(directory, baseFileName, extension);
    }

    private void CleanupOldFiles(string directory, string baseFileName, string extension)
    {
        if (_options.RetentionDays <= 0) return;

        try
        {
            var pattern = $"{baseFileName}-*{extension}";
            var files = Directory.GetFiles(directory, pattern)
                .Select(f => new FileInfo(f))
                .Where(f => f.CreationTimeUtc < DateTime.UtcNow.AddDays(-_options.RetentionDays))
                .ToList();

            foreach (var file in files) file.Delete();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}

/// <summary>
///     Represents a bot detection event for persistence.
/// </summary>
public class DetectionEvent
{
    public DateTime Timestamp { get; set; }
    public string? RequestId { get; set; }
    public double BotProbability { get; set; }
    public double Confidence { get; set; }
    public bool IsBot { get; set; }
    public string? RiskBand { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
    public string? Path { get; set; }
    public double ProcessingTimeMs { get; set; }
    public List<string>? ContributingDetectors { get; set; }
    public List<string>? FailedDetectors { get; set; }
    public object? CategoryBreakdown { get; set; }
}
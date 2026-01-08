using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Yarp;

/// <summary>
///     Writes YARP bot signatures to files with rotation and buffering.
/// </summary>
public interface IYarpSignatureWriter
{
    /// <summary>Write a signature (buffered)</summary>
    Task WriteAsync(YarpBotSignature signature);

    /// <summary>Flush all pending signatures to disk</summary>
    Task FlushAsync();
}

/// <summary>
///     Implementation of YARP signature writer with file rotation.
/// </summary>
public class YarpSignatureWriter : IYarpSignatureWriter, IDisposable
{
    private readonly Timer _autoFlushTimer;
    private readonly ConcurrentQueue<YarpBotSignature> _buffer = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly ILogger<YarpSignatureWriter> _logger;
    private readonly YarpLearningModeOptions _options;
    private int _bufferedCount;
    private DateTime _currentFileCreated;

    private string? _currentFilePath;
    private long _currentFileSize;

    public YarpSignatureWriter(
        ILogger<YarpSignatureWriter> logger,
        IOptions<YarpLearningModeOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        // Auto-flush timer (every 10 seconds)
        _autoFlushTimer = new Timer(
            _ => FlushAsync().GetAwaiter().GetResult(),
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10));

        // Ensure output directory exists
        if (_options.Enabled) Directory.CreateDirectory(_options.OutputPath);
    }

    public void Dispose()
    {
        _autoFlushTimer.Dispose();
        FlushAsync().GetAwaiter().GetResult();
        _flushLock.Dispose();
    }

    public Task WriteAsync(YarpBotSignature signature)
    {
        if (!_options.Enabled)
            return Task.CompletedTask;

        _buffer.Enqueue(signature);
        Interlocked.Increment(ref _bufferedCount);

        // Auto-flush if buffer is full
        if (_bufferedCount >= _options.BufferSize) return FlushAsync();

        return Task.CompletedTask;
    }

    public async Task FlushAsync()
    {
        if (_bufferedCount == 0)
            return;

        await _flushLock.WaitAsync();
        try
        {
            // Get current file path (may rotate)
            var filePath = GetCurrentFilePath();

            // Drain buffer
            var signatures = new List<YarpBotSignature>();
            while (_buffer.TryDequeue(out var sig)) signatures.Add(sig);

            if (signatures.Count == 0)
                return;

            // Write to file
            if (_options.FileFormat.Equals("jsonl", StringComparison.OrdinalIgnoreCase))
                await WriteJsonLinesAsync(filePath, signatures);
            else
                await WriteJsonArrayAsync(filePath, signatures);

            // Update counters
            Interlocked.Exchange(ref _bufferedCount, 0);
            _currentFileSize += signatures.Count * 500; // Rough estimate

            _logger.LogDebug(
                "Flushed {Count} signatures to {FilePath}",
                signatures.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush signatures");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private string GetCurrentFilePath()
    {
        // Check if rotation needed
        if (ShouldRotate()) RotateFile();

        // Generate file path if needed
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            _currentFilePath = GenerateFilePath();
            _currentFileCreated = DateTime.UtcNow;
            _currentFileSize = 0;
        }

        return _currentFilePath;
    }

    private bool ShouldRotate()
    {
        if (string.IsNullOrEmpty(_currentFilePath))
            return false;

        // Check file size
        if (File.Exists(_currentFilePath))
        {
            var fileInfo = new FileInfo(_currentFilePath);
            if (fileInfo.Length >= _options.Rotation.MaxSizeBytes)
            {
                _logger.LogInformation(
                    "Rotating file {Path} - size limit reached ({Size} bytes)",
                    _currentFilePath, fileInfo.Length);
                return true;
            }
        }

        // Check file age
        var age = DateTime.UtcNow - _currentFileCreated;
        if (age.TotalHours >= _options.Rotation.MaxAgeHours)
        {
            _logger.LogInformation(
                "Rotating file {Path} - age limit reached ({Hours} hours)",
                _currentFilePath, age.TotalHours);
            return true;
        }

        return false;
    }

    private void RotateFile()
    {
        _currentFilePath = null;
        _currentFileCreated = DateTime.MinValue;
        _currentFileSize = 0;

        // Cleanup old files if needed
        CleanupOldFiles();
    }

    private void CleanupOldFiles()
    {
        if (_options.Rotation.MaxFiles <= 0)
            return;

        try
        {
            var directory = _options.OutputPath;
            var pattern = $"signatures*.{_options.FileFormat}";
            var files = Directory.GetFiles(directory, pattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            // Delete old files
            var filesToDelete = files.Skip(_options.Rotation.MaxFiles);
            foreach (var file in filesToDelete)
                try
                {
                    file.Delete();
                    _logger.LogInformation("Deleted old signature file: {Path}", file.FullName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old file: {Path}", file.FullName);
                }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup old signature files");
        }
    }

    private string GenerateFilePath()
    {
        var fileName = _options.UseTimestampedFiles
            ? $"signatures_{DateTime.UtcNow:yyyy-MM-dd_HHmmss}.{_options.FileFormat}"
            : $"signatures.{_options.FileFormat}";

        return Path.Combine(_options.OutputPath, fileName);
    }

    private async Task WriteJsonLinesAsync(string filePath, List<YarpBotSignature> signatures)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        await using var stream = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            true);

        await using var writer = new StreamWriter(stream);

        foreach (var signature in signatures)
        {
            var json = JsonSerializer.Serialize(signature, options);
            await writer.WriteLineAsync(json);
        }
    }

    private async Task WriteJsonArrayAsync(string filePath, List<YarpBotSignature> signatures)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        // For JSON array, we need to read existing, append, and rewrite
        // This is less efficient than JSONL but maintains valid JSON
        var allSignatures = new List<YarpBotSignature>();

        if (File.Exists(filePath))
            try
            {
                var existingJson = await File.ReadAllTextAsync(filePath);
                var existing = JsonSerializer.Deserialize<List<YarpBotSignature>>(existingJson, options);
                if (existing != null) allSignatures.AddRange(existing);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read existing JSON file, starting fresh");
            }

        allSignatures.AddRange(signatures);

        var json = JsonSerializer.Serialize(allSignatures, options);
        await File.WriteAllTextAsync(filePath, json);
    }
}
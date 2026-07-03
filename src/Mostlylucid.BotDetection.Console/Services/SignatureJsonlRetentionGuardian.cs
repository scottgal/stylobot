using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Guardians;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Data guardian that bounds the gateway's signature JSONL footprint.
///     <see cref="Logging.SignatureLogger"/> appends to a per-day
///     <c>signatures-{date}.jsonl</c> and never rotates; left alone these grow
///     without limit (a soak accumulated ~14 GB, which then stalled boot when the
///     loader scanned them). This guardian deletes day-files past the retention
///     window and, if the surviving set still exceeds the byte cap, deletes
///     oldest-first until under it. The current day's file is always spared: it is
///     the one the logger is actively appending to.
///
///     The guardian framework is FOSS-core; this concrete guardian lives in the
///     Console gateway because that is where the signature JSONL is written. The
///     retention policy (days / cap / interval) is config, per
///     <c>SignatureLogging:Retention:*</c>.
/// </summary>
public sealed class SignatureJsonlRetentionGuardian : IGuardian
{
    private const string FilePattern = "signatures-*.jsonl";

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly long _maxTotalBytes;
    private readonly ILogger<SignatureJsonlRetentionGuardian> _logger;

    public SignatureJsonlRetentionGuardian(
        ILogger<SignatureJsonlRetentionGuardian> logger,
        string directory,
        int retentionDays,
        long maxTotalBytes,
        TimeSpan interval)
    {
        _logger = logger;
        _directory = string.IsNullOrWhiteSpace(directory) ? "." : directory;
        _retentionDays = Math.Max(1, retentionDays);
        _maxTotalBytes = maxTotalBytes; // <= 0 disables the byte cap
        Interval = interval;
    }

    public string Name => "SignatureJsonlRetention";
    public GuardianCategory Category => GuardianCategory.Data;
    public TimeSpan Interval { get; }

    public Task<GuardianReport> GuardAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var filesBefore = 0;
        var filesDeleted = 0;
        long bytesReclaimed = 0;

        try
        {
            if (!Directory.Exists(_directory))
                return Task.FromResult(GuardianReport.Ok(this, sw.Elapsed.TotalMilliseconds));

            // Never touch today's active file (the logger is appending to it).
            var todayFile = $"signatures-{DateTime.UtcNow:yyyy-MM-dd}.jsonl";

            var files = Directory.GetFiles(_directory, FilePattern)
                .Select(f => new FileInfo(f))
                .Where(f => !string.Equals(f.Name, todayFile, StringComparison.OrdinalIgnoreCase))
                .OrderBy(FileDate) // oldest first
                .ToList();

            filesBefore = files.Count;

            // Pass 1: age-based prune (files older than the retention window).
            var cutoff = DateTime.UtcNow.Date.AddDays(-_retentionDays);
            foreach (var f in files.ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (FileDate(f) >= cutoff) continue;
                bytesReclaimed += TryDelete(f);
                files.Remove(f);
                filesDeleted++;
            }

            // Pass 2: byte-cap prune (oldest-first) if the survivors still overflow.
            if (_maxTotalBytes > 0)
            {
                var remaining = files.Sum(f => f.Length);
                foreach (var f in files) // already oldest-first
                {
                    if (remaining <= _maxTotalBytes) break;
                    ct.ThrowIfCancellationRequested();
                    var len = f.Length;
                    var freed = TryDelete(f);
                    if (freed <= 0) continue;
                    remaining -= len;
                    bytesReclaimed += freed;
                    filesDeleted++;
                }
            }

            var status = filesDeleted > 0 ? "pruned" : "ok";
            var details = filesDeleted > 0
                ? $"{filesDeleted} jsonl file(s), {bytesReclaimed / 1_048_576} MB reclaimed"
                : null;

            return Task.FromResult(new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = status,
                RowsBefore = filesBefore,
                RowsAfter = filesBefore - filesDeleted,
                BytesReclaimed = bytesReclaimed,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Details = details
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signature JSONL retention pass failed");
            return Task.FromResult(new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = "error",
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Details = ex.Message
            });
        }
    }

    private long TryDelete(FileInfo f)
    {
        try
        {
            var bytes = f.Length;
            f.Delete();
            _logger.LogInformation("Pruned signature file {File} ({Bytes} bytes)", f.Name, bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune signature file {File}", f.Name);
            return 0;
        }
    }

    /// <summary>Date encoded in <c>signatures-yyyy-MM-dd.jsonl</c>; falls back to
    ///     last-write time when the name does not parse.</summary>
    private static DateTime FileDate(FileInfo f)
    {
        var name = Path.GetFileNameWithoutExtension(f.Name); // signatures-2026-06-01
        var dash = name.IndexOf('-');
        if (dash >= 0 && DateTime.TryParse(name[(dash + 1)..], out var d)) return d;
        return f.LastWriteTimeUtc;
    }
}

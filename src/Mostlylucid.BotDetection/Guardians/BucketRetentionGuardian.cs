using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Guardians;

/// <summary>
///     Data guardian that prunes stale time-series bucket rows from the
///     detection archive (Phase 1 of the old VectorCompactionService).
///
///     Buckets are the only data type that is truly deleted (no compaction needed).
///     All other stored types are compacted; buckets simply expire after
///     <see cref="RetentionOptions.BucketRetention"/>.
///
///     This is a behaviour-preserving extract: the body is the exact
///     <c>RunPhase1BucketPruneAsync</c> logic from VectorCompactionService,
///     wrapped in the <see cref="IGuardian"/> contract. Both the interval and
///     the enabled flag are config-driven via
///     <c>BotDetection:Guardians:BucketRetention:*</c>.
/// </summary>
public sealed class BucketRetentionGuardian : IGuardian
{
    private readonly IDetectionArchive _store;
    private readonly TimeSpan _bucketRetention;
    private readonly ILogger<BucketRetentionGuardian> _logger;

    public BucketRetentionGuardian(
        IDetectionArchive store,
        IOptions<BotDetectionOptions> options,
        IConfiguration config,
        ILogger<BucketRetentionGuardian> logger)
    {
        _store = store;
        _bucketRetention = options.Value.Retention.BucketRetention;
        _logger = logger;

        var (enabled, interval) = GuardianConfig.Read(
            config, "BucketRetention", options.Value.Retention.CompactionInterval);

        Enabled = enabled;
        Interval = interval;
    }

    public string Name => "BucketRetention";
    public GuardianCategory Category => GuardianCategory.Data;
    public TimeSpan Interval { get; }
    public bool Enabled { get; }

    public async Task<GuardianReport> GuardAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Propagate cancellation; do not catch OperationCanceledException.
            ct.ThrowIfCancellationRequested();

            await _store.PruneBucketsAsync(_bucketRetention, ct);

            _logger.LogDebug(
                "BucketRetention: pruned bucket rows older than {Retention}",
                _bucketRetention);

            return new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = "pruned",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BucketRetention: bucket pruning failed");
            return new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = "error",
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Details = ex.Message
            };
        }
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Guardians;

/// <summary>
///     Data guardian that compacts overflowing SQLite session rows into
///     behavioural centroids (Phase 2 of the old VectorCompactionService).
///
///     For each signature that exceeds <see cref="RetentionOptions.MaxSessionsPerSignature"/>
///     full-resolution session rows, computes a maturity-weighted behavioural
///     centroid AND a velocity centroid (average drift direction across consecutive
///     sessions), stores them as <c>root_vector</c> on the signature row, and
///     deletes the overflow rows. The most recent
///     <see cref="RetentionOptions.MaxSessionsPerSignature"/> sessions are kept
///     at full resolution.
///
///     After compaction the HNSW index entry for the signature is replaced with
///     a single centroid entry so vector search stays consistent with the store.
///
///     This is a behaviour-preserving extract: the body is the exact
///     <c>RunPhase2SessionCompactionAsync</c> + <c>UpdateHnswEntryForSignatureAsync</c>
///     logic from VectorCompactionService, wrapped in the <see cref="IGuardian"/>
///     contract. Both the interval and the enabled flag are config-driven via
///     <c>BotDetection:Guardians:SessionCompaction:*</c>.
/// </summary>
public sealed class SessionCompactionGuardian : IGuardian
{
    private readonly IDetectionArchive _store;
    private readonly ISessionVectorSearch? _vectorSearch;
    private readonly int _maxSessionsPerSignature;
    private readonly ILogger<SessionCompactionGuardian> _logger;

    public SessionCompactionGuardian(
        IDetectionArchive store,
        IOptions<BotDetectionOptions> options,
        IConfiguration config,
        ILogger<SessionCompactionGuardian> logger,
        ISessionVectorSearch? vectorSearch = null)
    {
        _store = store;
        _vectorSearch = vectorSearch;
        _maxSessionsPerSignature = options.Value.Retention.MaxSessionsPerSignature;
        _logger = logger;

        var (enabled, interval) = GuardianConfig.Read(
            config, "SessionCompaction", options.Value.Retention.CompactionInterval);

        Enabled = enabled;
        Interval = interval;
    }

    public string Name => "SessionCompaction";
    public GuardianCategory Category => GuardianCategory.Data;
    public TimeSpan Interval { get; }
    public bool Enabled { get; }

    public async Task<GuardianReport> GuardAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            var compacted = await RunSessionCompactionAsync(ct);
            var status = compacted > 0 ? "compacted" : "ok";
            var details = compacted > 0 ? $"{compacted} signatures compacted" : null;
            return new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = status,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Details = details
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionCompaction: compaction run failed");
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

    // ===========================
    // Phase 2: SQLite session compaction (moved from VectorCompactionService)
    // ===========================

    private async Task<int> RunSessionCompactionAsync(CancellationToken ct)
    {
        var overflowing = await _store.GetOverflowingSignaturesAsync(
            _maxSessionsPerSignature, limit: 1000, ct);

        if (overflowing.Count == 0)
        {
            _logger.LogDebug(
                "SessionCompaction: no signatures over session limit ({Max})",
                _maxSessionsPerSignature);
            return 0;
        }

        _logger.LogInformation(
            "SessionCompaction: compacting {Count} signatures over {Max}-session limit",
            overflowing.Count, _maxSessionsPerSignature);

        var compacted = 0;
        foreach (var (signature, _) in overflowing)
        {
            if (ct.IsCancellationRequested) break;

            var result = await _store.CompactSignatureSessionsAsync(
                signature, _maxSessionsPerSignature, ct);

            if (result.HasCentroid && _vectorSearch != null)
                await UpdateHnswEntryForSignatureAsync(result, ct);

            if (result.CompactedCount > 0) compacted++;
        }

        _logger.LogInformation("SessionCompaction: {Count} signatures compacted", compacted);
        return compacted;
    }

    /// <summary>
    ///     Replaces the per-signature entry in the HNSW index with a single
    ///     centroid entry after compaction. Keeps the velocity centroid in the
    ///     metadata so downstream analysis can still see the drift direction.
    /// </summary>
    private async Task UpdateHnswEntryForSignatureAsync(CompactionResult result, CancellationToken ct)
    {
        if (_vectorSearch == null || result.BehavioralCentroid == null) return;

        var all = _vectorSearch.GetAllVectorsSnapshot();
        var remaining = all
            .Where(x => x.Metadata.Signature != result.Signature)
            .ToList();

        // Add the compacted centroid entry with velocity centroid embedded in metadata.
        var centroidMeta = new SessionVectorMetadata
        {
            Signature = result.Signature,
            IsBot = false, // Will be updated by next live session; not derivable from centroid alone.
            BotProbability = 0,
            Timestamp = DateTimeOffset.UtcNow,
            VelocityVector = result.VelocityCentroid,
            VelocityMagnitude = result.VelocityCentroid != null
                ? Analysis.SessionVectorizer.VelocityMagnitude(result.VelocityCentroid)
                : 0f,
            FrequencyFingerprint = result.FrequencyCentroid,
            CompressionLevel = 1, // L1 centroid
            Priority = 1.0
        };

        remaining.Add((result.BehavioralCentroid, centroidMeta));
        await _vectorSearch.ReplaceAllAsync(remaining);
    }
}

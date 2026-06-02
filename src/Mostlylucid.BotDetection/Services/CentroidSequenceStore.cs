using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Services;

/// <summary>A cluster's expected request sequence.</summary>
public sealed record CentroidSequence
{
    public required string CentroidId { get; init; }
    public CentroidType Type { get; init; } = CentroidType.Unknown;
    public RequestState[] ExpectedStates { get; init; } = Array.Empty<RequestState>();
    public double[] TypicalGapsMs { get; init; } = Array.Empty<double>();
    public double[] GapToleranceMs { get; init; } = Array.Empty<double>();
    public int SampleSize { get; init; }
}

/// <summary>
///     Provides centroid-specific expected request chains (Tier 2) keyed by cluster ID,
///     plus a global fallback chain (Tier 1) for new fingerprints.
///     Rebuilt after each clustering run via RebuildAsync.
///     Persisted in the same SQLite DB as sessions.
/// </summary>
public sealed class CentroidSequenceStore
{
    /// <summary>
    ///     Optional delegate for fetching session transition data for a set of cluster members.
    ///     When non-null, <see cref="RebuildAsync"/> uses it to compute centroid chains from real
    ///     observed sessions; when null, falls back to the typical-bot / typical-human templates.
    /// </summary>
    public delegate Task<List<SessionTransitionData>> ClusterSessionLoader(
        IReadOnlyList<string> memberSignatures, int perSignature, CancellationToken ct);

    private readonly Func<DbConnection> _connectionFactory;
    private readonly ILogger<CentroidSequenceStore> _logger;
    private readonly ClusterSessionLoader? _sessionLoader;

    // Global fallback chain (Tier 1): typical human sequence from PageView.
    private CentroidSequence _globalChain = new()
    {
        CentroidId = "global",
        Type = CentroidType.Unknown,
        ExpectedStates =
        [
            RequestState.StaticAsset, RequestState.StaticAsset, RequestState.StaticAsset,
            RequestState.ApiCall, RequestState.SignalR
        ],
        TypicalGapsMs = [200, 100, 100, 500, 1000],
        GapToleranceMs = [500, 300, 300, 1500, 3000],
        SampleSize = 0
    };

    private volatile FrozenDictionary<string, CentroidSequence> _centroidChains =
        FrozenDictionary<string, CentroidSequence>.Empty;

    // Paths where divergence spiked or asset hash changed — suppress divergence scoring.
    // Value is the time staleness was declared; expires after StalenessWindowHours.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _staleEndpoints = new();
    private const int StalenessWindowHours = 1;

    private static readonly RequestState[] TypicalHumanChain =
    [
        RequestState.StaticAsset, RequestState.StaticAsset, RequestState.StaticAsset,
        RequestState.ApiCall, RequestState.SignalR
    ];

    private static readonly RequestState[] TypicalBotChain =
    [
        RequestState.ApiCall, RequestState.ApiCall, RequestState.ApiCall,
        RequestState.NotFound, RequestState.ApiCall
    ];

    private static readonly double[] DefaultHumanGapsMs = [200, 100, 100, 500, 1000];
    private static readonly double[] DefaultHumanTolerancesMs = [500, 300, 300, 1500, 3000];
    private static readonly double[] DefaultBotGapsMs = [50, 50, 50, 50, 50];
    private static readonly double[] DefaultBotTolerancesMs = [100, 100, 100, 100, 100];

    public CentroidSequenceStore(
        Func<DbConnection> connectionFactory,
        ILogger<CentroidSequenceStore> logger,
        ClusterSessionLoader? sessionLoader = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _sessionLoader = sessionLoader;
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    public CentroidSequence GlobalChain => _globalChain;

    /// <summary>
    ///     Replace the global fallback chain with an explicit value.
    ///     Marks the store as ready so callers stop suppressing divergence scoring.
    ///     Used by tests and tools that want to bypass the learned-baseline warm-up.
    /// </summary>
    public void SetGlobalChain(CentroidSequence chain)
    {
        _globalChain = chain;
        IsGlobalReady = true;
    }

    /// <summary>
    ///     True once a site-learned global baseline is available (either learned from recent
    ///     confirmed-human sessions via <see cref="RelearnGlobalAsync"/> or restored from
    ///     SQLite at startup). When false, callers should suppress divergence scoring for
    ///     fingerprints that fall back to the global chain.
    /// </summary>
    public bool IsGlobalReady { get; private set; }

    /// <summary>
    ///     Learn a site-wide global baseline by sampling all recent confirmed-human sessions
    ///     via the configured loader. If fewer than <paramref name="minSessions"/> sessions
    ///     are available or the aggregator returns an empty chain, leaves
    ///     <see cref="IsGlobalReady"/> false so callers can suppress divergence scoring.
    /// </summary>
    public async Task RelearnGlobalAsync(int minSessions, CancellationToken ct = default)
    {
        if (_sessionLoader == null) return;

        // Empty signature list = "broad sample across all signatures".
        var sessions = await _sessionLoader(Array.Empty<string>(), minSessions * 2, ct);
        if (sessions.Count < minSessions)
        {
            IsGlobalReady = false;
            return;
        }

        var chain = SessionChainAggregator.AggregateFromTransitions(
            sessions, chainLength: 5, minTotalTransitions: 10);
        if (chain.Length == 0)
        {
            IsGlobalReady = false;
            return;
        }

        _globalChain = new CentroidSequence
        {
            CentroidId = "global",
            Type = CentroidType.Unknown,
            ExpectedStates = chain,
            TypicalGapsMs = DefaultHumanGapsMs,
            GapToleranceMs = DefaultHumanTolerancesMs,
            SampleSize = sessions.Count
        };
        IsGlobalReady = true;
        await PersistAsync(new[] { _globalChain }, ct);
    }

    /// <summary>
    ///     Mark an endpoint path as stale. ContentSequenceContributor will suppress divergence scoring
    ///     until the staleness window expires or the centroid is rebuilt.
    /// </summary>
    public void MarkEndpointStale(string path)
        => _staleEndpoints[path] = DateTimeOffset.UtcNow;

    /// <summary>Returns true when the path was marked stale within the last <see cref="StalenessWindowHours"/> hour(s).</summary>
    public bool IsEndpointStale(string path)
    {
        if (!_staleEndpoints.TryGetValue(path, out var markedAt))
            return false;
        return DateTimeOffset.UtcNow - markedAt < TimeSpan.FromHours(StalenessWindowHours);
    }

    /// <summary>Clear staleness for a path — call after a successful centroid rebuild for this path.</summary>
    public void ClearEndpointStale(string path) => _staleEndpoints.TryRemove(path, out _);

    /// <summary>
    ///     Look up a centroid chain by cluster ID. Returns null when the chain has insufficient
    ///     sample data (below <paramref name="minSampleSize"/>), so callers fall back to the global chain.
    /// </summary>
    public CentroidSequence? TryGetCentroidChain(string clusterId, int minSampleSize = 20)
    {
        if (_centroidChains.TryGetValue(clusterId, out var chain) && chain.SampleSize >= minSampleSize)
            return chain;
        return null;
    }

    /// <summary>
    ///     Create the centroid_sequences table if absent and load existing chains into memory.
    ///     Call once at startup (e.g. from a hosted service or IHostedLifecycle).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = _connectionFactory();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS centroid_sequences (
                centroid_id   TEXT PRIMARY KEY,
                centroid_type INTEGER NOT NULL,
                sequence_json TEXT NOT NULL,
                sample_size   INTEGER NOT NULL,
                computed_at   TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        await LoadFromDatabaseAsync(conn, ct);
    }

    /// <summary>
    ///     Rebuild chains from a fresh clustering result and persist to SQLite.
    ///     Called by a subscriber to <see cref="BotClusterService.ClustersUpdated"/>.
    /// </summary>
    public async Task RebuildAsync(IReadOnlyList<BotCluster> clusters, CancellationToken ct = default)
    {
        var newChains = new Dictionary<string, CentroidSequence>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var type = DetermineClusterType(cluster);
            var sampleSize = cluster.MemberCount;

            RequestState[] states;
            if (_sessionLoader != null && cluster.MemberSignatures.Count > 0)
            {
                var perSig = Math.Max(1, 200 / Math.Max(1, cluster.MemberSignatures.Count));
                var observed = await _sessionLoader(cluster.MemberSignatures, perSig, ct);
                var modal = SessionChainAggregator.AggregateFromTransitions(
                    observed, chainLength: 5, minTotalTransitions: 10);
                states = modal.Length > 0 ? modal : DefaultChainFor(type);
            }
            else
            {
                states = DefaultChainFor(type);
            }

            var (gaps, tolerances) = type == CentroidType.Bot
                ? (DefaultBotGapsMs, DefaultBotTolerancesMs)
                : (DefaultHumanGapsMs, DefaultHumanTolerancesMs);

            newChains[cluster.ClusterId] = new CentroidSequence
            {
                CentroidId = cluster.ClusterId,
                Type = type,
                ExpectedStates = states,
                TypicalGapsMs = gaps,
                GapToleranceMs = tolerances,
                SampleSize = sampleSize
            };
        }

        _centroidChains = newChains.ToFrozenDictionary();
        await PersistAsync(newChains.Values, ct);
        _logger.LogDebug("CentroidSequenceStore rebuilt with {Count} clusters", newChains.Count);
    }

    private static RequestState[] DefaultChainFor(CentroidType type) =>
        type == CentroidType.Bot ? TypicalBotChain : TypicalHumanChain;

    /// <summary>
    ///     Map a <see cref="BotClusterType"/> to the coarser <see cref="CentroidType"/> used
    ///     by the sequence chain selection logic.
    ///     - BotProduct / BotNetwork / Emergent → Bot
    ///     - HumanTraffic / Safe → Human (Safe = verified-friendly fanout; uses
    ///       the same human chains as normal browser traffic)
    ///     - Mixed / Unknown → Unknown (falls back to human defaults at runtime)
    /// </summary>
    private static CentroidType DetermineClusterType(BotCluster cluster) =>
        cluster.Type switch
        {
            BotClusterType.BotProduct => CentroidType.Bot,
            BotClusterType.BotNetwork => CentroidType.Bot,
            BotClusterType.Emergent => CentroidType.Bot,
            BotClusterType.HumanTraffic => CentroidType.Human,
            BotClusterType.Safe => CentroidType.Human,
            BotClusterType.Mixed => CentroidType.Unknown,
            _ => CentroidType.Unknown
        };

    private async Task PersistAsync(IEnumerable<CentroidSequence> chains, CancellationToken ct)
    {
        await using var conn = _connectionFactory();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var chain in chains)
            {
                var json = JsonSerializer.Serialize(chain, Data.BotDetectionJsonSerializerContext.Default.CentroidSequence);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO centroid_sequences (centroid_id, centroid_type, sequence_json, sample_size, computed_at)
                    VALUES (@id, @type, @json, @size, @at)
                    ON CONFLICT(centroid_id) DO UPDATE SET
                        centroid_type = excluded.centroid_type,
                        sequence_json = excluded.sequence_json,
                        sample_size   = excluded.sample_size,
                        computed_at   = excluded.computed_at;
                    """;
                AddParam(cmd, "@id", chain.CentroidId);
                AddParam(cmd, "@type", (int)chain.Type);
                AddParam(cmd, "@json", json);
                AddParam(cmd, "@size", chain.SampleSize);
                AddParam(cmd, "@at", DateTime.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task LoadFromDatabaseAsync(DbConnection conn, CancellationToken ct)
    {
        var chains = new Dictionary<string, CentroidSequence>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sequence_json FROM centroid_sequences;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            try
            {
                var chain = JsonSerializer.Deserialize(reader.GetString(0), Data.BotDetectionJsonSerializerContext.Default.CentroidSequence);
                if (chain != null)
                {
                    if (chain.CentroidId == "global")
                    {
                        _globalChain = chain;
                        IsGlobalReady = true;
                    }
                    else
                    {
                        chains[chain.CentroidId] = chain;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize centroid sequence");
            }
        }
        _centroidChains = chains.ToFrozenDictionary();
        _logger.LogDebug("CentroidSequenceStore loaded {Count} chains from DB", chains.Count);
    }
}

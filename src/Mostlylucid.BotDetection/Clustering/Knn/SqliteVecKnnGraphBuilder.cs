using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Clustering.Knn;

/// <summary>
///     KNN graph builder backed by an ephemeral in-memory sqlite-vec
///     (vec0) virtual table populated from each cluster cycle's feature
///     vectors. Per cycle: load the extension, create the table, insert
///     centroids, KNN-query top-K per node, compute the full blended
///     similarity for those candidate edges, drop the table.
/// </summary>
/// <remarks>
///     <para>
///         The vec table holds only the behavioural centroid (the
///         large vector input the existing similarity blend already
///         uses for the cosine axis). Candidate pairs surfaced by the
///         KNN query are then weighted via the caller's full similarity
///         function -- so the graph weights are byte-identical to the
///         brute-force builder on the candidate edges it surfaces. We
///         only LOSE edges that the brute-force builder would have kept;
///         we never invent edges.
///     </para>
///     <para>
///         Operator-installable: vec0 is loaded from
///         <see cref="ExtensionPath"/> or the OS library search path.
///         Failure throws -- the caller is expected to catch and fall
///         back to <see cref="BruteForceKnnGraphBuilder"/>. Same
///         pattern as <c>SqliteVecIdentityAnchorIndex</c>.
///     </para>
///     <para>
///         Below <see cref="MinFeaturesForVec"/> features (default 64),
///         the setup cost exceeds the win. The builder throws and the
///         caller falls back to brute-force.
///     </para>
/// </remarks>
public sealed class SqliteVecKnnGraphBuilder : IKnnGraphBuilder
{
    public const int MinFeaturesForVec = 64;
    public const int DefaultCentroidDim = 110;   // matches metastable identity centroid layout

    private readonly ILogger<SqliteVecKnnGraphBuilder> _logger;
    private readonly string? _extensionPath;
    private readonly int _centroidDim;

    public SqliteVecKnnGraphBuilder(
        ILogger<SqliteVecKnnGraphBuilder> logger,
        string? extensionPath = null,
        int centroidDim = DefaultCentroidDim)
    {
        _logger = logger;
        _extensionPath = extensionPath;
        _centroidDim = centroidDim;
    }

    /// <summary>Path passed to <c>SqliteConnection.LoadExtension(string)</c>.</summary>
    public string ExtensionPath => _extensionPath ?? "vec0";

    public Dictionary<int, List<(int Neighbor, double Weight)>> Build(
        IReadOnlyList<BotClusterService.FeatureVector> features,
        Func<BotClusterService.FeatureVector, BotClusterService.FeatureVector, double> similarity,
        int k,
        double minSimilarity,
        CancellationToken ct = default)
    {
        if (features.Count < MinFeaturesForVec)
            throw new InvalidOperationException(
                $"SqliteVec KNN builder needs at least {MinFeaturesForVec} features; got {features.Count}. " +
                "Caller should fall back to brute-force at this scale.");

        // Find the subset of features that have a behavioural centroid of
        // the expected dimension -- only those can participate in vec0 KNN.
        var indexableIndices = new List<int>(features.Count);
        for (var i = 0; i < features.Count; i++)
        {
            var centroid = features[i].BehaviouralCentroid;
            if (centroid is not null && centroid.Length == _centroidDim)
                indexableIndices.Add(i);
        }

        if (indexableIndices.Count < MinFeaturesForVec)
            throw new InvalidOperationException(
                $"Fewer than {MinFeaturesForVec} features have a {_centroidDim}-dim centroid; " +
                $"vec0 path doesn't help. Caller should fall back to brute-force.");

        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        try
        {
            conn.EnableExtensions(true);
            conn.LoadExtension(ExtensionPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "sqlite-vec extension not loadable. " +
                "Install from https://github.com/asg017/sqlite-vec/releases to opt into the vec0 perf path.",
                ex);
        }

        using (var create = conn.CreateCommand())
        {
            create.CommandText =
                $"CREATE VIRTUAL TABLE knn USING vec0(idx INTEGER PRIMARY KEY, centroid FLOAT[{_centroidDim}])";
            create.ExecuteNonQuery();
        }

        // Bulk-insert centroids (one transaction; small per-row cost).
        using (var tx = conn.BeginTransaction())
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO knn(idx, centroid) VALUES (@idx, @vec)";
            var idxParam = insert.Parameters.Add("@idx", SqliteType.Integer);
            var vecParam = insert.Parameters.Add("@vec", SqliteType.Blob);

            foreach (var i in indexableIndices)
            {
                ct.ThrowIfCancellationRequested();
                idxParam.Value = i;
                vecParam.Value = PackFloats(features[i].BehaviouralCentroid!);
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }

        // Top-K query per node, then compute the full similarity for the
        // candidate edges and emit if above threshold.
        var adjacency = new Dictionary<int, List<(int Neighbor, double Weight)>>(features.Count);
        for (var i = 0; i < features.Count; i++) adjacency[i] = new List<(int, double)>();

        using var query = conn.CreateCommand();
        query.CommandText = "SELECT idx FROM knn WHERE centroid MATCH @qv AND k = @k";
        var qvParam = query.Parameters.Add("@qv", SqliteType.Blob);
        var kParam = query.Parameters.Add("@k", SqliteType.Integer);
        // +1 because vec0 returns the query itself in the K results; we'll
        // discard self-matches and keep K real neighbours.
        kParam.Value = k + 1;

        foreach (var i in indexableIndices)
        {
            ct.ThrowIfCancellationRequested();
            qvParam.Value = PackFloats(features[i].BehaviouralCentroid!);

            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var j = reader.GetInt32(0);
                if (j == i) continue;
                // Compute full blended similarity (caller-supplied), apply
                // threshold. Then add the edge symmetrically. Duplicate
                // candidate pairs (i->j and later j->i) re-add the edge
                // with the same weight -- guard against duplicates.
                var weight = similarity(features[i], features[j]);
                if (weight < minSimilarity) continue;

                if (!adjacency[i].Any(e => e.Neighbor == j))
                    adjacency[i].Add((j, weight));
                if (!adjacency[j].Any(e => e.Neighbor == i))
                    adjacency[j].Add((i, weight));
            }
        }

        _logger.LogInformation(
            "vec0 KNN graph: {N} features ({Indexable} indexable), k={K}, edges/node avg={Avg:F1}",
            features.Count, indexableIndices.Count, k,
            adjacency.Count == 0 ? 0 : (double)adjacency.Sum(kv => kv.Value.Count) / adjacency.Count);

        return adjacency;
    }

    /// <summary>
    ///     Pack a float vector as little-endian IEEE 754 bytes -- the
    ///     wire format vec0 expects for a FLOAT[D] column.
    /// </summary>
    private static byte[] PackFloats(float[] vec)
    {
        var bytes = new byte[vec.Length * sizeof(float)];
        var span = bytes.AsSpan();
        for (var i = 0; i < vec.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * sizeof(float), sizeof(float)), vec[i]);
        }
        return bytes;
    }
}

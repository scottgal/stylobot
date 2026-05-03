using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     A single proof-of-work micro-puzzle: find a nonce such that SHA256(seed + nonce)
///     has the required number of leading zero hex characters.
/// </summary>
public sealed record PuzzleSeed(byte[] Seed, int RequiredZeros);

/// <summary>
///     Server-side record of a challenge issued to a client.
///     Single-use: consumed on first successful verification.
/// </summary>
public sealed record ChallengeRecord
{
    public required string Id { get; init; }
    public required string Signature { get; init; }
    public required int PuzzleCount { get; init; }
    public required int RequiredZeros { get; init; }
    public required PuzzleSeed[] Puzzles { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
///     Result of a client solving a PoW challenge, capturing timing metadata
///     for use as detection signals on subsequent requests.
/// </summary>
public sealed record ChallengeVerificationResult
{
    public required string Signature { get; init; }
    public required double TotalSolveDurationMs { get; init; }
    public required int ReportedWorkerCount { get; init; }
    public required int PuzzleCount { get; init; }
    public required double[] PuzzleTimingsMs { get; init; }
    public required double TimingJitter { get; init; }
    public required DateTimeOffset VerifiedAt { get; init; }
}

/// <summary>
///     Server-side store for PoW challenges: creation, single-use consumption, and
///     verification result storage for the feedback loop into detection signals.
/// </summary>
public interface IChallengeStore
{
    /// <summary>Creates a new PoW challenge for the given signature.</summary>
    ChallengeRecord CreateChallenge(string signature, int puzzleCount, int requiredZeros, TimeSpan expiry);

    /// <summary>Validates and consumes a challenge (single-use). Returns null if not found or expired.</summary>
    ChallengeRecord? ValidateAndConsume(string challengeId);

    /// <summary>Records the verification result (solve timing metadata) for the feedback loop.</summary>
    void RecordVerification(ChallengeVerificationResult result);

    /// <summary>Gets the most recent verification result for a signature, if any.</summary>
    ChallengeVerificationResult? GetVerification(string signature);
}

/// <summary>
///     SQLite-backed challenge store. Challenges and verification results are persisted
///     to disk and survive restarts. Follows the same patterns as SqliteSessionStore
///     (WAL mode, SemaphoreSlim write lock, same database directory).
/// </summary>
public sealed class SqliteChallengeStore : IChallengeStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _connectionString;
    private readonly ILogger<SqliteChallengeStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    // Read-through cache for verification results (written once, read on every request for that signature)
    private readonly Services.BoundedCache<string, ChallengeVerificationResult?> _verificationCache = new(maxSize: 1_000, defaultTtl: TimeSpan.FromMinutes(30));

    public SqliteChallengeStore(
        ILogger<SqliteChallengeStore> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        var dbPath = Path.Combine(basePath, "challenges.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-2000;

            CREATE TABLE IF NOT EXISTS challenges (
                id TEXT PRIMARY KEY,
                signature TEXT NOT NULL,
                puzzle_count INTEGER NOT NULL,
                required_zeros INTEGER NOT NULL,
                puzzles_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS verifications (
                signature TEXT PRIMARY KEY,
                total_solve_duration_ms REAL NOT NULL,
                reported_worker_count INTEGER NOT NULL,
                puzzle_count INTEGER NOT NULL,
                puzzle_timings_json TEXT NOT NULL,
                timing_jitter REAL NOT NULL,
                verified_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_challenges_expires ON challenges(expires_at);
            CREATE INDEX IF NOT EXISTS idx_challenges_signature ON challenges(signature);
            CREATE INDEX IF NOT EXISTS idx_verifications_verified ON verifications(verified_at);
            """;
        cmd.ExecuteNonQuery();

        // Clean up expired challenges on startup
        using var cleanCmd = conn.CreateCommand();
        cleanCmd.CommandText = "DELETE FROM challenges WHERE expires_at < @now OR consumed = 1";
        cleanCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        var cleaned = cleanCmd.ExecuteNonQuery();
        if (cleaned > 0) _logger.LogDebug("Cleaned up {Count} expired/consumed challenges on startup", cleaned);

        _initialized = true;
        _logger.LogInformation("Challenge store initialized at {ConnectionString}", _connectionString);
    }

    public ChallengeRecord CreateChallenge(string signature, int puzzleCount, int requiredZeros, TimeSpan expiry)
    {
        EnsureInitialized();

        var puzzles = new PuzzleSeed[puzzleCount];
        for (var i = 0; i < puzzleCount; i++)
        {
            var seed = new byte[16];
            RandomNumberGenerator.Fill(seed);
            puzzles[i] = new PuzzleSeed(seed, requiredZeros);
        }

        var record = new ChallengeRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Signature = signature,
            PuzzleCount = puzzleCount,
            RequiredZeros = requiredZeros,
            Puzzles = puzzles,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(expiry)
        };

        _writeLock.Wait();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO challenges (id, signature, puzzle_count, required_zeros, puzzles_json, created_at, expires_at, consumed)
                VALUES (@id, @sig, @count, @zeros, @puzzles, @created, @expires, 0)
                """;
            cmd.Parameters.AddWithValue("@id", record.Id);
            cmd.Parameters.AddWithValue("@sig", record.Signature);
            cmd.Parameters.AddWithValue("@count", record.PuzzleCount);
            cmd.Parameters.AddWithValue("@zeros", record.RequiredZeros);
            cmd.Parameters.AddWithValue("@puzzles", JsonSerializer.Serialize(
                record.Puzzles.Select(p => new { seed = Convert.ToBase64String(p.Seed), zeros = p.RequiredZeros }), JsonOpts));
            cmd.Parameters.AddWithValue("@created", record.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@expires", record.ExpiresAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogDebug(
            "Created PoW challenge {Id} for {Signature}: {Count} puzzles, {Zeros} zeros, expires {Expiry}",
            record.Id, signature, puzzleCount, requiredZeros, record.ExpiresAt);

        return record;
    }

    public ChallengeRecord? ValidateAndConsume(string challengeId)
    {
        EnsureInitialized();

        _writeLock.Wait();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // Atomic: mark consumed and return in one step
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE challenges SET consumed = 1
                WHERE id = @id AND consumed = 0 AND expires_at > @now
                RETURNING id, signature, puzzle_count, required_zeros, puzzles_json, created_at, expires_at
                """;
            cmd.Parameters.AddWithValue("@id", challengeId);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                _logger.LogDebug("Challenge {Id} not found, expired, or already consumed", challengeId);
                return null;
            }

            var puzzlesJson = reader.GetString(4);
            var puzzleData = JsonSerializer.Deserialize<JsonElement[]>(puzzlesJson);
            var puzzles = puzzleData?.Select(p => new PuzzleSeed(
                Convert.FromBase64String(p.GetProperty("seed").GetString()!),
                p.GetProperty("zeros").GetInt32()
            )).ToArray() ?? [];

            return new ChallengeRecord
            {
                Id = reader.GetString(0),
                Signature = reader.GetString(1),
                PuzzleCount = reader.GetInt32(2),
                RequiredZeros = reader.GetInt32(3),
                Puzzles = puzzles,
                CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
                ExpiresAt = DateTimeOffset.Parse(reader.GetString(6))
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void RecordVerification(ChallengeVerificationResult result)
    {
        EnsureInitialized();

        _writeLock.Wait();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO verifications (signature, total_solve_duration_ms, reported_worker_count, puzzle_count, puzzle_timings_json, timing_jitter, verified_at)
                VALUES (@sig, @dur, @workers, @count, @timings, @jitter, @at)
                ON CONFLICT(signature) DO UPDATE SET
                    total_solve_duration_ms = @dur,
                    reported_worker_count = @workers,
                    puzzle_count = @count,
                    puzzle_timings_json = @timings,
                    timing_jitter = @jitter,
                    verified_at = @at
                """;
            cmd.Parameters.AddWithValue("@sig", result.Signature);
            cmd.Parameters.AddWithValue("@dur", result.TotalSolveDurationMs);
            cmd.Parameters.AddWithValue("@workers", result.ReportedWorkerCount);
            cmd.Parameters.AddWithValue("@count", result.PuzzleCount);
            cmd.Parameters.AddWithValue("@timings", JsonSerializer.Serialize(result.PuzzleTimingsMs, JsonOpts));
            cmd.Parameters.AddWithValue("@jitter", result.TimingJitter);
            cmd.Parameters.AddWithValue("@at", result.VerifiedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }

        // Invalidate cache on write so next read picks up the new verification
        _verificationCache.Set(result.Signature, result);

        _logger.LogDebug(
            "Recorded PoW verification for {Signature}: {Duration:F0}ms, {Workers} workers, jitter={Jitter:F3}",
            result.Signature, result.TotalSolveDurationMs, result.ReportedWorkerCount, result.TimingJitter);
    }

    public ChallengeVerificationResult? GetVerification(string signature)
    {
        // Read-through cache: verification results are written once, read on every request
        if (_verificationCache.TryGet(signature, out var cached)) return cached;

        EnsureInitialized();

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM verifications WHERE signature = @sig";
        cmd.Parameters.AddWithValue("@sig", signature);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            _verificationCache.Set(signature, null); // Cache the miss too
            return null;
        }

        var timingsJson = reader.GetString(reader.GetOrdinal("puzzle_timings_json"));
        var timings = JsonSerializer.Deserialize<double[]>(timingsJson) ?? [];

        var result = new ChallengeVerificationResult
        {
            Signature = reader.GetString(reader.GetOrdinal("signature")),
            TotalSolveDurationMs = reader.GetDouble(reader.GetOrdinal("total_solve_duration_ms")),
            ReportedWorkerCount = reader.GetInt32(reader.GetOrdinal("reported_worker_count")),
            PuzzleCount = reader.GetInt32(reader.GetOrdinal("puzzle_count")),
            PuzzleTimingsMs = timings,
            TimingJitter = reader.GetDouble(reader.GetOrdinal("timing_jitter")),
            VerifiedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("verified_at")))
        };

        _verificationCache.Set(signature, result);
        return result;
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

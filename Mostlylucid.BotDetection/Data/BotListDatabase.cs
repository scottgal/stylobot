using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     SQLite-based bot list storage with automatic updates
/// </summary>
public interface IBotListDatabase
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> IsBot(string userAgent, CancellationToken cancellationToken = default);
    Task<BotInfo?> GetBotInfo(string userAgent, CancellationToken cancellationToken = default);
    Task<bool> IsDatacenterIp(string ipAddress, CancellationToken cancellationToken = default);
    Task UpdateListsAsync(CancellationToken cancellationToken = default);
    Task<DateTime?> GetLastUpdateTimeAsync(string listType, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all bot patterns from the database for caching.
    /// </summary>
    Task<IReadOnlyList<string>> GetBotPatternsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all datacenter IP ranges from the database for caching.
    /// </summary>
    Task<IReadOnlyList<string>> GetDatacenterIpRangesAsync(CancellationToken cancellationToken = default);
}

public class BotInfo
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Url { get; set; }
    public bool IsVerified { get; set; }
}

/// <summary>
///     SQLite database for bot detection lists with caching and auto-updates
/// </summary>
public class BotListDatabase : IBotListDatabase, IDisposable
{
    private const int MaxPatternsPerQuery = 500;
    private readonly string _dbPath;
    private readonly IBotListFetcher _fetcher;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<BotListDatabase> _logger;
    private bool _initialized;

    public BotListDatabase(
        IBotListFetcher fetcher,
        ILogger<BotListDatabase> logger,
        string? dbPath = null)
    {
        _fetcher = fetcher;
        _logger = logger;
        _dbPath = dbPath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing bot detection database at {Path}", _dbPath);

            var directory = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS bot_patterns (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    pattern TEXT NOT NULL,
                    category TEXT NOT NULL,
                    url TEXT,
                    is_verified INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    UNIQUE(pattern)
                );

                CREATE INDEX IF NOT EXISTS idx_bot_patterns_pattern ON bot_patterns(pattern);
                CREATE INDEX IF NOT EXISTS idx_bot_patterns_category ON bot_patterns(category);

                CREATE TABLE IF NOT EXISTS datacenter_ips (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ip_range TEXT NOT NULL UNIQUE,
                    provider TEXT,
                    region TEXT,
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_datacenter_ips_range ON datacenter_ips(ip_range);

                CREATE TABLE IF NOT EXISTS list_updates (
                    list_type TEXT PRIMARY KEY,
                    last_update TEXT NOT NULL,
                    record_count INTEGER NOT NULL
                );
            ";

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;

            var lastUpdate = await GetLastUpdateTimeAsync("bot_patterns", cancellationToken);
            if (lastUpdate == null || (DateTime.UtcNow - lastUpdate.Value).TotalHours > 24)
            {
                _logger.LogInformation("Bot lists are stale or missing, updating...");
                await UpdateListsAsync(cancellationToken);
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> IsBot(string userAgent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return true;

        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT pattern FROM bot_patterns LIMIT {MaxPatternsPerQuery}";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var pattern = reader.GetString(0);
            try
            {
                if (Regex.IsMatch(userAgent, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    return true;
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning("Regex timeout for pattern: {Pattern}", pattern);
            }
        }

        return false;
    }

    public async Task<BotInfo?> GetBotInfo(string userAgent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;

        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT name, pattern, category, url, is_verified FROM bot_patterns LIMIT {MaxPatternsPerQuery}";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var pattern = reader.GetString(1);
            try
            {
                if (Regex.IsMatch(userAgent, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    return new BotInfo
                    {
                        Name = reader.GetString(0),
                        Category = reader.GetString(2),
                        Url = reader.IsDBNull(3) ? null : reader.GetString(3),
                        IsVerified = reader.GetInt32(4) == 1
                    };
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning("Regex timeout for pattern: {Pattern}", pattern);
            }
        }

        return null;
    }

    public async Task<bool> IsDatacenterIp(string ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return false;

        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ip_range FROM datacenter_ips";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var range = reader.GetString(0);
            if (CidrHelper.IsInSubnet(ipAddress, range)) return true;
        }

        return false;
    }

    public async Task UpdateListsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        _logger.LogInformation("Updating bot detection lists from remote sources");

        try
        {
            var matomoPatterns = await _fetcher.GetMatomoBotPatternsAsync(cancellationToken);
            var botPatterns = await _fetcher.GetBotPatternsAsync(cancellationToken);

            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM bot_patterns";
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // Insert Matomo patterns with metadata (if enabled)
                foreach (var pattern in matomoPatterns)
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT OR IGNORE INTO bot_patterns (name, pattern, category, url, is_verified, created_at)
                        VALUES (@name, @pattern, @category, @url, @verified, @created)";

                    cmd.Parameters.AddWithValue("@name", pattern.Name ?? "Unknown");
                    cmd.Parameters.AddWithValue("@pattern", pattern.Pattern ?? "");
                    cmd.Parameters.AddWithValue("@category", pattern.Category ?? "Unknown");
                    cmd.Parameters.AddWithValue("@url", (object?)pattern.Url ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@verified", IsVerifiedBot(pattern.Name) ? 1 : 0);
                    cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("O"));

                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // Insert bot patterns from all enabled sources (IsBot, crawler-user-agents, etc.)
                foreach (var botPattern in botPatterns)
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT OR IGNORE INTO bot_patterns (name, pattern, category, url, is_verified, created_at)
                        VALUES (@name, @pattern, @category, @url, @verified, @created)";

                    cmd.Parameters.AddWithValue("@name", "Bot");
                    cmd.Parameters.AddWithValue("@pattern", botPattern);
                    cmd.Parameters.AddWithValue("@category", "Bot");
                    cmd.Parameters.AddWithValue("@url", DBNull.Value);
                    cmd.Parameters.AddWithValue("@verified", 0);
                    cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("O"));

                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO list_updates (list_type, last_update, record_count)
                        VALUES ('bot_patterns', @update, @count)";

                    cmd.Parameters.AddWithValue("@update", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("@count", matomoPatterns.Count + botPatterns.Count);

                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation("Updated {Count} bot patterns ({MatomoCount} Matomo, {BotCount} general)",
                    matomoPatterns.Count + botPatterns.Count, matomoPatterns.Count, botPatterns.Count);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }

            var ipRanges = await _fetcher.GetDatacenterIpRangesAsync(cancellationToken);

            await using var ipTransaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM datacenter_ips";
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var range in ipRanges)
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT OR IGNORE INTO datacenter_ips (ip_range, provider, region, created_at)
                        VALUES (@range, @provider, @region, @created)";

                    cmd.Parameters.AddWithValue("@range", range);
                    cmd.Parameters.AddWithValue("@provider", DetectProvider(range));
                    cmd.Parameters.AddWithValue("@region", DBNull.Value);
                    cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("O"));

                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO list_updates (list_type, last_update, record_count)
                        VALUES ('datacenter_ips', @update, @count)";

                    cmd.Parameters.AddWithValue("@update", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("@count", ipRanges.Count);

                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await ipTransaction.CommitAsync(cancellationToken);

                _logger.LogInformation("Updated {Count} datacenter IP ranges", ipRanges.Count);
            }
            catch
            {
                await ipTransaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update bot detection lists");
        }
    }

    public async Task<DateTime?> GetLastUpdateTimeAsync(string listType, CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            try
            {
                await InitializeAsync(cancellationToken);
            }
            catch
            {
                return null;
            }

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT last_update FROM list_updates WHERE list_type = @type";
        cmd.Parameters.AddWithValue("@type", listType);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value)
            return null;

        return DateTime.Parse((string)result);
    }

    public async Task<IReadOnlyList<string>> GetBotPatternsAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            try
            {
                await InitializeAsync(cancellationToken);
            }
            catch
            {
                return Array.Empty<string>();
            }

        var patterns = new List<string>();

        try
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT pattern FROM bot_patterns WHERE pattern IS NOT NULL AND pattern != ''";

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var pattern = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(pattern)) patterns.Add(pattern);
            }

            _logger.LogDebug("Retrieved {Count} bot patterns from database", patterns.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve bot patterns from database");
        }

        return patterns;
    }

    public async Task<IReadOnlyList<string>> GetDatacenterIpRangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            try
            {
                await InitializeAsync(cancellationToken);
            }
            catch
            {
                return Array.Empty<string>();
            }

        var ranges = new List<string>();

        try
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT DISTINCT ip_range FROM datacenter_ips WHERE ip_range IS NOT NULL AND ip_range != ''";

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var range = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(range)) ranges.Add(range);
            }

            _logger.LogDebug("Retrieved {Count} IP ranges from database", ranges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve IP ranges from database");
        }

        return ranges;
    }

    public void Dispose()
    {
        _initLock?.Dispose();
    }

    private static bool IsVerifiedBot(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        var verifiedBots = new[] { "Googlebot", "Bingbot", "Slackbot", "DuckDuckBot", "YandexBot" };
        return verifiedBots.Any(vb => name.Contains(vb, StringComparison.OrdinalIgnoreCase));
    }

    private static string DetectProvider(string ipRange)
    {
        if (ipRange.StartsWith("3.") || ipRange.StartsWith("13.") ||
            ipRange.StartsWith("18.") || ipRange.StartsWith("52."))
            return "AWS";

        if (ipRange.StartsWith("20.") || ipRange.StartsWith("40.") ||
            ipRange.StartsWith("104."))
            return "Azure";

        if (ipRange.StartsWith("34.") || ipRange.StartsWith("35."))
            return "GCP";

        if (ipRange.StartsWith("138.") || ipRange.StartsWith("139.") ||
            ipRange.StartsWith("140."))
            return "Oracle";

        return "Unknown";
    }
}
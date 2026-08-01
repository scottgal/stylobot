using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

public class SqliteDashboardEventStoreSchemaTests
{
    [Fact]
    public async Task Detections_table_has_response_bytes_column()
    {
        // DashboardDbPath.GetConnectionString derives the dir from DatabasePath
        // and places dashboard.db inside it.
        var tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var fakeDatabasePath = Path.Combine(tempDir, "botdetection.db");
        var dashboardDbPath = Path.Combine(tempDir, "dashboard.db");

        try
        {
            var options = Options.Create(new BotDetectionOptions
            {
                DatabasePath = fakeDatabasePath
            });
            var logger = NullLogger<SqliteDashboardEventStore>.Instance;
            var store = new SqliteDashboardEventStore(logger, options);

            // GetDetectionsAsync triggers EnsureInitializedAsync internally
            await store.GetDetectionsAsync();

            await using var conn = new SqliteConnection($"Data Source={dashboardDbPath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(detections);";
            await using var reader = await cmd.ExecuteReaderAsync();

            var columns = new List<string>();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));

            columns.Should().Contain("response_bytes");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AddDetectionAsync_persists_response_bytes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-rb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var fakeDatabasePath = Path.Combine(tempDir, "botdetection.db");
        var dashboardDbPath = Path.Combine(tempDir, "dashboard.db");

        try
        {
            var options = Options.Create(new BotDetectionOptions { DatabasePath = fakeDatabasePath });
            var store = new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, options);

            // Trigger init
            await store.GetDetectionsAsync();

            var detection = new DashboardDetectionEvent
            {
                RequestId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                IsBot = false,
                BotProbability = 0.1,
                Confidence = 0.9,
                RiskBand = "Low",
                Method = "GET",
                Path = "/test",
                StatusCode = 200,
                ProcessingTimeMs = 5.0,
                ResponseBytes = 4096
            };

            await store.AddDetectionAsync(detection);

            await using var conn = new SqliteConnection($"Data Source={dashboardDbPath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT response_bytes FROM detections ORDER BY rowid DESC LIMIT 1;";
            var raw = await cmd.ExecuteScalarAsync();

            raw.Should().NotBeNull().And.NotBe(DBNull.Value);
            Convert.ToInt64(raw).Should().Be(4096);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AddDetectionAsync_persists_null_response_bytes_for_chunked_response()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-rb-null-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var fakeDatabasePath = Path.Combine(tempDir, "botdetection.db");
        var dashboardDbPath = Path.Combine(tempDir, "dashboard.db");

        try
        {
            var options = Options.Create(new BotDetectionOptions { DatabasePath = fakeDatabasePath });
            var store = new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, options);

            // Trigger init
            await store.GetDetectionsAsync();

            var detection = new DashboardDetectionEvent
            {
                RequestId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                IsBot = false,
                BotProbability = 0.1,
                Confidence = 0.9,
                RiskBand = "Low",
                Method = "GET",
                Path = "/chunked",
                StatusCode = 200,
                ProcessingTimeMs = 3.0,
                ResponseBytes = null
            };

            await store.AddDetectionAsync(detection);

            await using var conn = new SqliteConnection($"Data Source={dashboardDbPath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT response_bytes FROM detections ORDER BY rowid DESC LIMIT 1;";
            var raw = await cmd.ExecuteScalarAsync();

            // NULL or DBNull — both mean the column correctly stored NULL
            (raw == null || raw == DBNull.Value).Should().BeTrue(
                "response_bytes should be NULL for chunked/streamed responses");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Detections_table_has_upstream_status_code_column()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-usc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var fakeDatabasePath = Path.Combine(tempDir, "botdetection.db");
        var dashboardDbPath = Path.Combine(tempDir, "dashboard.db");

        try
        {
            var options = Options.Create(new BotDetectionOptions { DatabasePath = fakeDatabasePath });
            var store = new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, options);

            await store.GetDetectionsAsync();

            await using var conn = new SqliteConnection($"Data Source={dashboardDbPath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(detections);";
            await using var reader = await cmd.ExecuteReaderAsync();

            var columns = new List<string>();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));

            columns.Should().Contain("upstream_status_code");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AddDetectionAsync_persists_upstream_status_code_when_present()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-usc-val-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var fakeDatabasePath = Path.Combine(tempDir, "botdetection.db");
        var dashboardDbPath = Path.Combine(tempDir, "dashboard.db");

        try
        {
            var options = Options.Create(new BotDetectionOptions { DatabasePath = fakeDatabasePath });
            var store = new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, options);

            await store.GetDetectionsAsync();

            var detection = new DashboardDetectionEvent
            {
                RequestId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                IsBot = false,
                BotProbability = 0.1,
                Confidence = 0.9,
                RiskBand = "Low",
                Method = "GET",
                Path = "/test",
                StatusCode = 200,
                ProcessingTimeMs = 5.0,
                UpstreamStatusCode = 200
            };

            await store.AddDetectionAsync(detection);

            await using var conn = new SqliteConnection($"Data Source={dashboardDbPath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT upstream_status_code FROM detections ORDER BY rowid DESC LIMIT 1;";
            var raw = await cmd.ExecuteScalarAsync();

            raw.Should().NotBeNull().And.NotBe(DBNull.Value);
            Convert.ToInt32(raw).Should().Be(200);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AddDetectionAsync_persists_null_upstream_status_code_when_no_origin_call()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-usc-null-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var fakeDatabasePath = Path.Combine(tempDir, "botdetection.db");
        var dashboardDbPath = Path.Combine(tempDir, "dashboard.db");

        try
        {
            var options = Options.Create(new BotDetectionOptions { DatabasePath = fakeDatabasePath });
            var store = new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, options);

            await store.GetDetectionsAsync();

            var detection = new DashboardDetectionEvent
            {
                RequestId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                IsBot = true,
                BotProbability = 0.95,
                Confidence = 0.95,
                RiskBand = "VeryHigh",
                Method = "GET",
                Path = "/wp-admin",
                StatusCode = 404,
                ProcessingTimeMs = 1.0,
                UpstreamStatusCode = null
            };

            await store.AddDetectionAsync(detection);

            await using var conn = new SqliteConnection($"Data Source={dashboardDbPath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT upstream_status_code FROM detections ORDER BY rowid DESC LIMIT 1;";
            var raw = await cmd.ExecuteScalarAsync();

            (raw == null || raw == DBNull.Value).Should().BeTrue(
                "upstream_status_code should be NULL for honeypot/blocked/throttled traffic that never reached the origin");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, recursive: true);
        }
    }
}

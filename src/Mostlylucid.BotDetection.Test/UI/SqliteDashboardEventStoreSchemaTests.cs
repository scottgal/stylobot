using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
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
                Directory.Delete(tempDir, recursive: true);
        }
    }
}

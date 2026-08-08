using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Sources;
using Mostlylucid.BotDetection.Extensions;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Ruling (overview-, 2026-08-08): <c>list_updates</c> has exactly two rows —
///     <c>bot_patterns</c> and <c>datacenter_ips</c> — not one per declared DataSources entry.
///     <see cref="BotDetectionFetchSourceContributor"/> exposes those two buckets as their own
///     <c>HasLiveState: true</c> sources rather than painting a false per-source timestamp onto the
///     8 individual entries they cover, which must stay <c>HasLiveState: false</c>.
/// </summary>
public sealed class BotListBucketFetchSourceTests
{
    private sealed class NullBotListFetcher : IBotListFetcher
    {
        public Task<List<string>> GetBotPatternsAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetDatacenterIpRangesAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetDatacenterIpRangesByVendorAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>());

        public Task<IReadOnlyList<int>> GetVpnAsnsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<List<BotPattern>> GetMatomoBotPatternsAsync(CancellationToken ct = default) => Task.FromResult(new List<BotPattern>());
        public Task<List<SecurityToolPattern>> GetSecurityToolPatternsAsync(CancellationToken ct = default) => Task.FromResult(new List<SecurityToolPattern>());
    }

    private static string NewTempDbPath() => Path.Combine(Path.GetTempPath(), $"botlist-bucket-test-{Guid.NewGuid():N}.db");

    /// <summary>
    ///     Creates + initializes a <see cref="BotListDatabase"/> against a fresh temp file.
    ///     InitializeAsync self-heals a missing/stale list_updates row by calling UpdateListsAsync
    ///     (even against the empty <see cref="NullBotListFetcher"/>) - so this already writes a
    ///     "now" row to BOTH buckets as a side effect, same as it would on first boot in production.
    ///     Callers that need specific timestamps overwrite via <see cref="OverwriteListUpdateAsync"/>
    ///     AFTER this returns, then reuse this SAME instance (never construct a second one against
    ///     the same file) so a later read doesn't re-trigger the self-heal and clobber the overwrite.
    /// </summary>
    private static async Task<BotListDatabase> NewInitializedDbAsync(string dbPath)
    {
        var db = new BotListDatabase(new NullBotListFetcher(), NullLogger<BotListDatabase>.Instance, dbPath);
        await db.InitializeAsync();
        return db;
    }

    private static async Task OverwriteListUpdateAsync(string dbPath, string listType, DateTime utc)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE list_updates SET last_update = @update WHERE list_type = @type";
        cmd.Parameters.AddWithValue("@type", listType);
        cmd.Parameters.AddWithValue("@update", utc.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static ServiceProvider BuildProvider(BotListDatabase initializedDb)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BotDetection:DatabasePath"] = ""
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        // Registered BEFORE AddBotDetection so its TryAddSingleton no-ops and this ALREADY
        // INITIALIZED instance wins - matches production, where the real BotListUpdateService has
        // already initialized the singleton before anything reads the fetch registry, so reading
        // never re-triggers the self-heal.
        services.AddSingleton(initializedDb);
        services.AddSingleton<IBotListDatabase>(sp => sp.GetRequiredService<BotListDatabase>());
        services.AddBotDetection(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Buckets_declared_with_correct_groups_and_no_false_precision_on_individual_sources()
    {
        var dbPath = NewTempDbPath();
        try
        {
            var db = await NewInitializedDbAsync(dbPath);
            var registry = BuildProvider(db).GetRequiredService<IFetchSourceRegistry>();
            var sources = await registry.GetAllAsync();

            var botPatterns = Assert.Single(sources, s => s.Id == "BotPatternsGroup");
            Assert.True(botPatterns.HasLiveState);
            Assert.Equal(["IsBot", "Matomo", "CrawlerUserAgents"], botPatterns.GroupedSourceIds);

            var datacenterIps = Assert.Single(sources, s => s.Id == "DatacenterIpsGroup");
            Assert.True(datacenterIps.HasLiveState);
            Assert.Equal(
                ["AwsIpRanges", "GcpIpRanges", "AzureIpRanges", "CloudflareIpv4", "CloudflareIpv6"],
                datacenterIps.GroupedSourceIds);

            // The 8 individual sources these buckets cover must NOT claim their own precision.
            foreach (var id in new[] { "IsBot", "Matomo", "CrawlerUserAgents", "AwsIpRanges", "GcpIpRanges", "AzureIpRanges", "CloudflareIpv4", "CloudflareIpv6" })
            {
                var s = Assert.Single(sources, x => x.Id == id);
                Assert.False(s.HasLiveState, $"{id} must stay HasLiveState:false - only the bucket has real precision");
            }
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task BotPatternsGroup_reads_the_bot_patterns_row_and_ignores_the_datacenter_ips_row()
    {
        var dbPath = NewTempDbPath();
        try
        {
            var db = await NewInitializedDbAsync(dbPath); // self-heal writes a "now" row to both buckets first
            var botPatternsAt = new DateTime(2026, 8, 8, 15, 4, 0, DateTimeKind.Utc);
            var datacenterIpsAt = new DateTime(2026, 8, 8, 14, 12, 0, DateTimeKind.Utc);
            await OverwriteListUpdateAsync(dbPath, "bot_patterns", botPatternsAt);
            await OverwriteListUpdateAsync(dbPath, "datacenter_ips", datacenterIpsAt);

            // Reuse the SAME already-initialized instance - a fresh one would re-run InitializeAsync
            // and self-heal right over these overwrites.
            var registry = BuildProvider(db).GetRequiredService<IFetchSourceRegistry>();
            var sources = await registry.GetAllAsync();

            var botPatterns = Assert.Single(sources, s => s.Id == "BotPatternsGroup");
            Assert.Equal(botPatternsAt, botPatterns.LastSuccessUtc!.Value.UtcDateTime);

            var datacenterIps = Assert.Single(sources, s => s.Id == "DatacenterIpsGroup");
            Assert.Equal(datacenterIpsAt, datacenterIps.LastSuccessUtc!.Value.UtcDateTime);

            Assert.Equal(FetchHealthState.Healthy, botPatterns.GetHealthState(DateTimeOffset.UtcNow));
            Assert.Equal(FetchHealthState.Healthy, datacenterIps.GetHealthState(DateTimeOffset.UtcNow));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Stale_bot_patterns_row_reads_as_Stale_against_the_real_24h_gate()
    {
        var dbPath = NewTempDbPath();
        try
        {
            var db = await NewInitializedDbAsync(dbPath);
            await OverwriteListUpdateAsync(dbPath, "bot_patterns", DateTime.UtcNow - TimeSpan.FromDays(10));

            var registry = BuildProvider(db).GetRequiredService<IFetchSourceRegistry>();
            var botPatterns = Assert.Single(await registry.GetAllAsync(), s => s.Id == "BotPatternsGroup");

            Assert.Equal(FetchHealthState.Stale, botPatterns.GetHealthState(DateTimeOffset.UtcNow));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}

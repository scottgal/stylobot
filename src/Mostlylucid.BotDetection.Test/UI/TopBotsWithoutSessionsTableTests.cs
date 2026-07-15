using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression coverage for the <c>stylobot --enable-api</c> topbots 500:
///     <c>SQLite Error 1: 'no such table: sessions'</c>.
///
///     <para>
///     <see cref="SqliteDashboardEventStore.GetTopBotsAsync"/> enriches each row with a
///     <c>last_path</c> read from the session store's <c>sessions</c> table. That table is created by
///     the core session store, and the query assumed both stores share one DB file. The stylobot
///     gateway with <c>--enable-api</c> keeps <c>dashboard.db</c> and <c>sessions.db</c> as separate
///     files, so <c>sessions</c> is absent from the dashboard connection and the query failed at
///     prepare time -- 500ing every <c>/api/v1/topbots</c> call regardless of row count.
///     </para>
///
///     <para>
///     The existing <see cref="SqliteDashboardStoreFixture"/> masked this by creating a stub
///     <c>sessions</c> table, so it only ever exercised the co-located path. These tests use a
///     dashboard-only DB with NO sessions table -- the real gateway topology -- and assert the query
///     degrades <c>last_path</c> to null instead of throwing.
///     </para>
/// </summary>
public class TopBotsWithoutSessionsTableTests
{
    [Fact]
    public async Task GetTopBots_EmptyDashboardDb_WithoutSessionsTable_DoesNotThrow()
    {
        await using var ctx = await NewSessionsLessStoreAsync();

        var act = async () => await ctx.Store.GetTopBotsAsync();

        await act.Should().NotThrowAsync(
            "topbots must not require the session store's table -- the gateway keeps them in separate DB files");
        (await ctx.Store.GetTopBotsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetTopBots_WithSignature_ButNoSessionsTable_ReturnsRow_LastPathNull()
    {
        await using var ctx = await NewSessionsLessStoreAsync();

        await ctx.Store.AddSignatureAsync(new DashboardSignatureEvent
        {
            SignatureId = "sig-topbots-1",
            Timestamp = DateTime.UtcNow,
            PrimarySignature = "sig-topbots-1",
            RiskBand = "VeryHigh",
            BotName = "curl",
            BotType = "Tool",
            BotProbability = 1.0,
            HitCount = 5
        });

        var top = await ctx.Store.GetTopBotsAsync();

        top.Should().ContainSingle(b => b.BotName == "curl",
            "the signature must surface even though the sessions table is absent");
        top.Single(b => b.BotName == "curl").LastPath.Should().BeNull(
            "last_path degrades to null when the sessions table is not in this DB");
    }

    // Like SqliteDashboardStoreFixture but WITHOUT the stub sessions table -- the real --enable-api
    // gateway topology where dashboard.db and sessions.db are separate files.
    private static async Task<StoreCtx> NewSessionsLessStoreAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-nosess-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var opts = Options.Create(new BotDetectionOptions { DatabasePath = Path.Combine(tempDir, "dashboard.db") });
        var store = new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, opts);
        _ = await store.GetDetectionsAsync(); // trigger schema init (creates detections/signatures, NOT sessions)

        // Assert the premise: the dashboard DB genuinely has no sessions table.
        await using (var conn = new SqliteConnection(DashboardDbPath.GetConnectionString(opts.Value)))
        {
            await conn.OpenAsync();
            await using var probe = conn.CreateCommand();
            probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='sessions'";
            (await probe.ExecuteScalarAsync()).Should().BeNull("this test models the sessions-less gateway DB");
        }

        return new StoreCtx(tempDir, store);
    }

    private sealed record StoreCtx(string TempDir, SqliteDashboardEventStore Store) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch { /* best-effort */ }
            return ValueTask.CompletedTask;
        }
    }
}

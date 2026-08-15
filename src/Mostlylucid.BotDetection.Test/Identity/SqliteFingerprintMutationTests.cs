using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     The fingerprint mutation feed — Phase A of the write-path grain redesign
///     (docs/architecture/write-path-grain-design.md §3.2 / §7.5): the fingerprint
///     is the aggregate root; its DURABLE feed is the significant state changes (the
///     needle-movers: created / centroid_drift / verdict_flip / name_change /
///     archetype_evidence / mode_transition), not per-request observations. These
///     tests pin the FOSS SQLite recorders: the birth (<c>created</c>, state_version
///     1, in-tx with the fingerprint row) and genuine name changes (<c>name_change</c>,
///     state_version bumped, alongside the name_history audit row). Non-changes must
///     NOT record mutations.
///     <para>
///     The mutations table + state_version column live in the boot-idempotent DDL
///     corpus (<c>Data/Schema/identity_core.sql</c> + the guarded ALTER in
///     <c>IdentitySchema.MigrateExistingTablesAsync</c>) — the store's own init applies
///     them, mirroring production order. No migration runner, no versioning.
///     </para>
/// </summary>
public sealed class SqliteFingerprintMutationTests : IDisposable
{
    private readonly string _tempDir;

    public SqliteFingerprintMutationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-fp-mutations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task InsertFingerprint_records_created_mutation_at_state_version_1()
    {
        var store = await NewStoreAsync();
        var fp = NewFingerprint("sig-mut-birth", IdentityVectorLayout.DefaultV1().Dimension);

        await store.InsertFingerprintAsync(fp, "sig-mut-birth", CancellationToken.None);

        var mutations = await GetMutationsAsync("sig-mut-birth");
        mutations.Should().HaveCount(1);
        mutations[0].MutationType.Should().Be("created");
        mutations[0].StateVersion.Should().Be(1);
        mutations[0].PayloadJson.Should().NotBeNullOrEmpty("the birth payload carries archetype origin + primary signature + birth band");
        mutations[0].PayloadJson!.Contains("ua-family:headless").Should().BeTrue(
            "the birth payload carries the archetype origin");
        mutations[0].PayloadJson.Contains("sig-mut-birth").Should().BeTrue(
            "the birth payload carries the primary signature");

        var version = await GetStateVersionAsync("sig-mut-birth");
        version.Should().Be(1, "the row insert leaves state_version at its DEFAULT 1; the created delta IS version 1");
    }

    [Fact]
    public async Task Genuine_operator_name_change_records_mutation_and_bumps_state_version()
    {
        var store = await NewStoreAsync();
        const string fpId = "sig-mut-operator";
        await store.InsertFingerprintAsync(NewFingerprint(fpId, IdentityVectorLayout.DefaultV1().Dimension), fpId, CancellationToken.None);

        // Warm the LFU dict like the editor flow does (read-before-write): the
        // store's "real transition" gate reads the PRIOR name from memory, so a
        // cold dict would make the same-name check below look like a transition.
        await store.GetFingerprintAsync(fpId, CancellationToken.None);

        // Synchronous operator pin: real transition.
        var at = DateTime.UtcNow;
        await store.UpdateGivenNameAsync(fpId, "Pin Bot", "test-operator", at, CancellationToken.None);

        var mutations = await GetMutationsAsync(fpId);
        mutations.Should().HaveCount(2, "created + name_change");
        var change = mutations[1];
        change.MutationType.Should().Be("name_change");
        change.StateVersion.Should().Be(2, "the name_change bumps the version past the birth");
        change.PayloadJson.Should().Contain("\"source\":\"operator\"");

        var history = await GetNameHistoryCountAsync(fpId, "operator");
        history.Should().Be(1, "the name_history audit row lands alongside the mutation");

        // Same-name write again: NOT a real transition — no mutation, no history row.
        await store.UpdateGivenNameAsync(fpId, "Pin Bot", "test-operator", DateTime.UtcNow, CancellationToken.None);
        mutations = await GetMutationsAsync(fpId);
        mutations.Should().HaveCount(2, "a same-name write must not record a second name_change");
    }

    [Fact]
    public async Task Induced_and_LLM_name_changes_land_through_the_drainer_with_monotonic_versions()
    {
        var store = await NewStoreAsync();
        const string fpId = "sig-mut-drainer";
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), fpId, CancellationToken.None);
        await store.GetFingerprintAsync(fpId, CancellationToken.None); // warm LFU dict (prior-name gate)

        // Matcher-induced change (write-behind drainer).
        await store.UpdateInducedNameAsync(fpId, "Chrome Induced", DateTime.UtcNow, CancellationToken.None);
        await WaitForAsync(async () => (await GetMutationsAsync(fpId)).Count == 2);

        // LLM change (write-behind drainer).
        await store.UpdateLlmNameAsync(fpId, "Llm Named Bot", description: null, DateTime.UtcNow, CancellationToken.None);
        await WaitForAsync(async () => (await GetMutationsAsync(fpId)).Count == 3);

        var mutations = await GetMutationsAsync(fpId);
        mutations.Select(m => m.StateVersion).Should().Equal(new long[] { 1, 2, 3 },
            "the delta chain is gapless and monotonic per fingerprint");
        mutations[1].MutationType.Should().Be("name_change");
        mutations[1].PayloadJson.Should().Contain("\"source\":\"matcher\"");
        mutations[2].MutationType.Should().Be("name_change");
        mutations[2].PayloadJson.Should().Contain("\"source\":\"llm\"");

        var version = await GetStateVersionAsync(fpId);
        version.Should().Be(3, "the materialized state_version tracks the delta chain head");
    }

    private async Task<SqliteFingerprintStore> NewStoreAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        await store.EnsureInitialisedAsync();
        return store;
    }

    private static Fingerprint NewFingerprint(string id, int dim)
    {
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            ArchetypeOrigin = "ua-family:headless",
            ClaimStatus = "unverified",
            TrustObservations = 0,
        };
    }

    private string ConnectionString
        => $"Data Source={Path.Combine(_tempDir, "fingerprints.db")};Pooling=true";

    private async Task<IReadOnlyList<MutationRow>> GetMutationsAsync(string fingerprintId)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint_id, state_version, mutation_type, payload_json, observed_at
            FROM fingerprint_mutations WHERE fingerprint_id = @id ORDER BY state_version
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        var rows = new List<MutationRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new MutationRow(
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return rows;
    }

    private async Task<long> GetStateVersionAsync(string fingerprintId)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state_version FROM fingerprints WHERE fingerprint_id = @id";
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<int> GetNameHistoryCountAsync(string fingerprintId, string source)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fingerprint_name_history WHERE fingerprint_id = @id AND source = @src";
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        cmd.Parameters.AddWithValue("@src", source);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task WaitForAsync(Func<Task<bool>> predicate, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("WaitFor predicate did not become true within timeout.");
    }

    private sealed record MutationRow(long StateVersion, string MutationType, string? PayloadJson);
}

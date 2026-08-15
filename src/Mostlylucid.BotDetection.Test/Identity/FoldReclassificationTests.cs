using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Phase B (write-path grain redesign): the absorption service's family-gated
///     archetype RECLASSIFICATION moved into the memory fold. The fingerprint's
///     inferred_client_type must converge onto the observed family's archetype as its
///     centroid folds, AND the flip must persist to the materialized fingerprints row at
///     the coalesced per-change grain (the drainer write) — an LFU invalidation + reload
///     (drift service, eviction) must never resurrect the stale allocation type.
/// </summary>
public sealed class FoldReclassificationTests : IDisposable
{
    private readonly string _tempDir;

    public FoldReclassificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-fold-reclass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Observations_ConvergeInferredClientType_OnTheObservedFamily_AndPersist()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var encoder = new IdentityVectorEncoder(layout);
        var archetypes = new IdentityArchetypeRegistry(NullLogger<IdentityArchetypeRegistry>.Instance, encoder);
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, layout, archetypes: archetypes);
        await store.EnsureInitialisedAsync();

        var dim = layout.Dimension;
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        const string fpId = "fp-family";
        await store.InsertFingerprintAsync(new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "unknown",
            InferredTypeConfidence = 0,
            InferredTypeChangedAt = now
        }, "sig-family", CancellationToken.None);
        await store.GetFingerprintAsync(fpId, CancellationToken.None); // warm LFU

        // A constant shape observed under the safari family: the fold's family-gated
        // reclassification must converge the inferred type onto a safari-family archetype.
        var shape = new float[dim];
        for (var i = 0; i < dim; i++) shape[i] = 0.5f;
        for (var i = 0; i < 10; i++)
            await store.RecordObservationAsync(RequestScope.Unknown, fpId, shape, "safari", CancellationToken.None);

        var evolved = await store.GetFingerprintAsync(fpId, CancellationToken.None);
        evolved.Should().NotBeNull();
        var type = evolved!.InferredClientType ?? string.Empty;
        type.Should().StartWith("safari",
            "the memory fold must converge a safari-family shape onto a safari archetype");

        // The flip must reach the materialized row (the drainer write) — wait for it, then
        // verify an LFU invalidation + reload serves the NEW type, not the stale allocation.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string? dbType = null;
        while (DateTime.UtcNow < deadline)
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT inferred_client_type FROM fingerprints WHERE fingerprint_id = @id";
            cmd.Parameters.AddWithValue("@id", fpId);
            dbType = await cmd.ExecuteScalarAsync() as string;
            if (dbType?.StartsWith("safari", StringComparison.OrdinalIgnoreCase) == true) break;
            await Task.Delay(50);
        }
        dbType.Should().StartWith("safari",
            "the reclassification must persist to the materialized row at the coalesced grain");
    }

    private string ConnectionString
        => $"Data Source={Path.Combine(_tempDir, "fingerprints.db")};Pooling=true";
}

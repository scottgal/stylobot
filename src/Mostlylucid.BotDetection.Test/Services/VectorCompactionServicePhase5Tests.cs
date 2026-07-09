using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Phase 5 of the storage data guardian: cross-signature cap enforcement.
///     When distinct signatures exceed <see cref="RetentionOptions.MaxSignatures"/>,
///     the guardian evicts the lowest-value signatures by
///     <see cref="Storage.DecisionNecessity"/> — resolved-and-harmless first,
///     uncertain + risky retained. Driven against a real <see cref="SqliteDetectionArchive"/>
///     so the ordering + cascading delete are exercised end-to-end.
/// </summary>
public sealed class VectorCompactionServicePhase5Tests : IAsyncLifetime
{
    private SqliteDetectionArchive _store = null!;
    private string _dbDir = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"phase5-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "botdetection.db")
        });
        _store = new SqliteDetectionArchive(NullLogger<SqliteDetectionArchive>.Instance, opts);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task GuardAsync_evicts_lowest_value_signatures_when_over_cap()
    {
        var now = DateTime.UtcNow;

        // 3 high-value signatures: uncertain (at threshold) + risky + recent → keep.
        await SeedSignatureAsync("keep-1", botProb: 0.50, riskBand: "Medium", isBot: true, lastSeen: now);
        await SeedSignatureAsync("keep-2", botProb: 0.55, riskBand: "High", isBot: true, lastSeen: now);
        await SeedSignatureAsync("keep-3", botProb: 0.48, riskBand: "Medium", isBot: false, lastSeen: now);

        // 3 low-value signatures: near-certain human, harmless, stale → evict first.
        await SeedSignatureAsync("cold-1", botProb: 0.02, riskBand: "VeryLow", isBot: false, lastSeen: now.AddDays(-40));
        await SeedSignatureAsync("cold-2", botProb: 0.01, riskBand: "VeryLow", isBot: false, lastSeen: now.AddDays(-45));
        await SeedSignatureAsync("cold-3", botProb: 0.03, riskBand: "VeryLow", isBot: false, lastSeen: now.AddDays(-50));

        var svc = NewService(maxSignatures: 3, botThreshold: 0.5);

        var report = await svc.GuardAsync();

        report.Status.Should().Be("evicted");
        (await _store.GetSignatureCountAsync()).Should().Be(3);

        var survivors = (await _store.GetAllSignaturePriorityInfoAsync(100))
            .Select(s => s.Signature)
            .ToHashSet();

        survivors.Should().BeEquivalentTo(new[] { "keep-1", "keep-2", "keep-3" });
    }

    [Fact]
    public async Task GuardAsync_reports_ok_when_under_cap()
    {
        await SeedSignatureAsync("a", lastSeen: DateTime.UtcNow);
        await SeedSignatureAsync("b", lastSeen: DateTime.UtcNow);

        var svc = NewService(maxSignatures: 100, botThreshold: 0.5);

        var report = await svc.GuardAsync();

        report.Status.Should().Be("ok");
        (await _store.GetSignatureCountAsync()).Should().Be(2);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private VectorCompactionService NewService(int maxSignatures, double botThreshold)
    {
        var opts = new BotDetectionOptions();
        opts.Classification.BotFloor = botThreshold; // canonical bot/human boundary
        opts.Retention.MaxSignatures = maxSignatures;
        opts.Retention.MinSignatures = 1;

        return new VectorCompactionService(
            _store,
            Options.Create(opts),
            NullLogger<VectorCompactionService>.Instance);
    }

    private async Task SeedSignatureAsync(
        string sig, double botProb = 0.1, string riskBand = "Low", bool isBot = false, DateTime? lastSeen = null)
    {
        await _store.UpsertSignatureAsync(RequestScope.Unknown, new PersistedSignature
        {
            SignatureId = sig,
            SessionCount = 1,
            TotalRequestCount = 10,
            FirstSeen = (lastSeen ?? DateTime.UtcNow).AddHours(-2),
            LastSeen = lastSeen ?? DateTime.UtcNow,
            IsBot = isBot,
            BotProbability = botProb,
            Confidence = 0.8,
            RiskBand = riskBand
        });
    }

}

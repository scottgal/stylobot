using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Test.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Guardians;

/// <summary>
///     Cross-signature cap enforcement via <see cref="SignatureCapGuardian"/>
///     (Phase 5, extracted from VectorCompactionService).
///
///     When distinct signatures exceed <see cref="RetentionOptions.MaxSignatures"/>,
///     the guardian evicts the lowest-value signatures by
///     <see cref="Storage.DecisionNecessity"/>: resolved-and-harmless (cold) first,
///     uncertain + risky retained. Driven against a real
///     <see cref="SqliteDetectionArchive"/> so the ordering + cascading delete
///     are exercised end-to-end.
/// </summary>
public sealed class SignatureCapGuardianTests : IAsyncLifetime
{
    private SqliteDetectionArchive _store = null!;
    private string _dbDir = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"sigcap-test-{Guid.NewGuid():N}");
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

    // ---- IGuardian contract ----

    [Fact]
    public void Is_a_data_category_guardian_named_SignatureCap()
    {
        var sut = BuildGuardian(maxSignatures: 100, botThreshold: 0.5);

        sut.Should().BeAssignableTo<IGuardian>();
        sut.Name.Should().Be("SignatureCap");
        sut.Category.Should().Be(GuardianCategory.Data);
    }

    [Fact]
    public void Interval_defaults_to_CompactionInterval_from_options()
    {
        var opts = DefaultOpts(maxSignatures: 100, botThreshold: 0.5);
        var sut = BuildGuardian(opts);

        sut.Interval.Should().Be(opts.Value.Retention.CompactionInterval);
    }

    [Fact]
    public void Interval_can_be_overridden_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:SignatureCap:Interval"] = "03:00:00"
            })
            .Build();

        var sut = BuildGuardian(DefaultOpts(maxSignatures: 100, botThreshold: 0.5), config);

        sut.Interval.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void Enabled_defaults_to_true()
    {
        var sut = BuildGuardian(maxSignatures: 100, botThreshold: 0.5);
        sut.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enabled_can_be_set_false_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:SignatureCap:Enabled"] = "false"
            })
            .Build();

        var sut = BuildGuardian(DefaultOpts(maxSignatures: 100, botThreshold: 0.5), config);
        sut.Enabled.Should().BeFalse();
    }

    // ---- Behavioural: eviction ordering ----

    /// <summary>
    ///     Cold, harmless, stale signatures are evicted; uncertain (near threshold)
    ///     and risky signatures are retained even when they are outnumbered.
    /// </summary>
    [Fact]
    public async Task GuardAsync_evicts_lowest_value_signatures_when_over_cap()
    {
        var now = DateTime.UtcNow;

        // 3 high-value signatures: uncertain (at threshold) + risky + recent -- keep.
        await SeedSignatureAsync("keep-1", botProb: 0.50, riskBand: "Medium", isBot: true,  lastSeen: now);
        await SeedSignatureAsync("keep-2", botProb: 0.55, riskBand: "High",   isBot: true,  lastSeen: now);
        await SeedSignatureAsync("keep-3", botProb: 0.48, riskBand: "Medium", isBot: false, lastSeen: now);

        // 3 low-value signatures: near-certain human, harmless, stale -- evict first.
        await SeedSignatureAsync("cold-1", botProb: 0.02, riskBand: "VeryLow", isBot: false, lastSeen: now.AddDays(-40));
        await SeedSignatureAsync("cold-2", botProb: 0.01, riskBand: "VeryLow", isBot: false, lastSeen: now.AddDays(-45));
        await SeedSignatureAsync("cold-3", botProb: 0.03, riskBand: "VeryLow", isBot: false, lastSeen: now.AddDays(-50));

        var sut = BuildGuardian(maxSignatures: 3, botThreshold: 0.5);

        var report = await sut.GuardAsync();

        report.Status.Should().Be("evicted");
        (await _store.GetSignatureCountAsync()).Should().Be(3);

        var survivors = (await _store.GetAllSignaturePriorityInfoAsync(100))
            .Select(s => s.Signature)
            .ToHashSet();

        survivors.Should().BeEquivalentTo(new[] { "keep-1", "keep-2", "keep-3" });
    }

    /// <summary>
    ///     When the count is at or below the cap, no evictions occur and the report
    ///     status is "ok".
    /// </summary>
    [Fact]
    public async Task GuardAsync_reports_ok_when_under_cap()
    {
        await SeedSignatureAsync("a", lastSeen: DateTime.UtcNow);
        await SeedSignatureAsync("b", lastSeen: DateTime.UtcNow);

        var sut = BuildGuardian(maxSignatures: 100, botThreshold: 0.5);

        var report = await sut.GuardAsync();

        report.Status.Should().Be("ok");
        (await _store.GetSignatureCountAsync()).Should().Be(2);
    }

    /// <summary>
    ///     When MaxSignatures is 0 (disabled), the guardian is a no-op regardless
    ///     of how many signatures exist.
    /// </summary>
    [Fact]
    public async Task GuardAsync_is_noop_when_max_signatures_is_zero()
    {
        await SeedSignatureAsync("x", lastSeen: DateTime.UtcNow);
        await SeedSignatureAsync("y", lastSeen: DateTime.UtcNow);
        await SeedSignatureAsync("z", lastSeen: DateTime.UtcNow);

        // maxSignatures: 0 = unlimited (disabled cap).
        var sut = BuildGuardian(maxSignatures: 0, botThreshold: 0.5);

        var report = await sut.GuardAsync();

        report.Status.Should().Be("ok");
        (await _store.GetSignatureCountAsync()).Should().Be(3);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static IOptions<BotDetectionOptions> DefaultOpts(int maxSignatures, double botThreshold)
    {
        var opts = new BotDetectionOptions();
        opts.Classification.BotFloor = botThreshold;
        opts.Retention.MaxSignatures = maxSignatures;
        opts.Retention.MinSignatures = 1;
        return Options.Create(opts);
    }

    private SignatureCapGuardian BuildGuardian(int maxSignatures, double botThreshold) =>
        BuildGuardian(DefaultOpts(maxSignatures, botThreshold));

    private SignatureCapGuardian BuildGuardian(
        IOptions<BotDetectionOptions> opts,
        IConfiguration? config = null) =>
        new(
            _store,
            opts,
            config ?? new ConfigurationBuilder().Build(),
            NullLogger<SignatureCapGuardian>.Instance);

    private async Task SeedSignatureAsync(
        string sig,
        double botProb = 0.1,
        string riskBand = "Low",
        bool isBot = false,
        DateTime? lastSeen = null)
    {
        await LegacySessionSeeder.SeedSignatureAsync(_store, new PersistedSignature
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

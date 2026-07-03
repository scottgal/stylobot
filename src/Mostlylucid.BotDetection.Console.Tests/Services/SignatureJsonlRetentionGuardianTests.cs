using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Console.Services;
using Mostlylucid.BotDetection.Guardians;

namespace Mostlylucid.BotDetection.ConsoleTests.Services;

/// <summary>
///     <see cref="SignatureJsonlRetentionGuardian"/> bounds the gateway's
///     per-day signature JSONL footprint. SignatureLogger appends forever and
///     never rotates; a soak accumulated ~14 GB and stalled boot. These cover the
///     guardian contract, age-based pruning, the byte cap (oldest-first), and the
///     always-spared active file.
/// </summary>
public sealed class SignatureJsonlRetentionGuardianTests : IDisposable
{
    private readonly string _dir;

    public SignatureJsonlRetentionGuardianTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sigjsonl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Is_a_data_category_guardian_with_the_configured_interval()
    {
        var g = NewGuardian(interval: TimeSpan.FromHours(6));

        g.Should().BeAssignableTo<IGuardian>();
        g.Name.Should().Be("SignatureJsonlRetention");
        g.Category.Should().Be(GuardianCategory.Data);
        g.Interval.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task Prunes_files_older_than_the_retention_window()
    {
        WriteSigFile("2020-01-01", 1_000);                       // ancient -> prune
        WriteSigFile(Yesterday(), 1_000);                        // recent -> keep
        WriteSigFile(Today(), 1_000);                            // active -> keep

        var g = NewGuardian(retentionDays: 14);
        var report = await g.GuardAsync();

        report.Status.Should().Be("pruned");
        File.Exists(SigPath("2020-01-01")).Should().BeFalse();
        File.Exists(SigPath(Yesterday())).Should().BeTrue();
        File.Exists(SigPath(Today())).Should().BeTrue();
    }

    [Fact]
    public async Task Never_deletes_todays_active_file_even_under_cap()
    {
        WriteSigFile(Today(), 10_000_000);                       // 10 MB active file

        var g = NewGuardian(retentionDays: 14, maxTotalBytes: 1); // cap 1 byte
        var report = await g.GuardAsync();

        // Today's file is excluded from the candidate set entirely.
        File.Exists(SigPath(Today())).Should().BeTrue();
        report.Status.Should().Be("ok");
    }

    [Fact]
    public async Task Enforces_byte_cap_oldest_first()
    {
        // Three recent (within-retention, not-today) files, 100 KB each.
        WriteSigFile(DaysAgo(3), 100_000);
        WriteSigFile(DaysAgo(2), 100_000);
        WriteSigFile(DaysAgo(1), 100_000);

        // Retention window wide so age never prunes; cap forces oldest-first eviction.
        var g = NewGuardian(retentionDays: 3650, maxTotalBytes: 150_000);
        var report = await g.GuardAsync();

        report.Status.Should().Be("pruned");
        File.Exists(SigPath(DaysAgo(3))).Should().BeFalse(); // oldest evicted
        File.Exists(SigPath(DaysAgo(2))).Should().BeFalse(); // next oldest evicted
        File.Exists(SigPath(DaysAgo(1))).Should().BeTrue();  // newest survives (100KB <= 150KB)
        report.BytesReclaimed.Should().BeGreaterThanOrEqualTo(150_000);
    }

    [Fact]
    public async Task Reports_ok_when_nothing_to_prune()
    {
        WriteSigFile(Yesterday(), 1_000);
        WriteSigFile(Today(), 1_000);

        var g = NewGuardian(retentionDays: 14, maxTotalBytes: 0); // cap disabled
        var report = await g.GuardAsync();

        report.Status.Should().Be("ok");
        report.RowsAfter.Should().Be(report.RowsBefore);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private SignatureJsonlRetentionGuardian NewGuardian(
        int retentionDays = 14, long maxTotalBytes = 512L * 1024 * 1024, TimeSpan? interval = null)
        => new(
            NullLogger<SignatureJsonlRetentionGuardian>.Instance,
            _dir,
            retentionDays,
            maxTotalBytes,
            interval ?? TimeSpan.FromHours(6));

    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");
    private static string Yesterday() => DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
    private static string DaysAgo(int n) => DateTime.UtcNow.AddDays(-n).ToString("yyyy-MM-dd");

    private string SigPath(string date) => Path.Combine(_dir, $"signatures-{date}.jsonl");

    private void WriteSigFile(string date, int sizeBytes)
        => File.WriteAllBytes(SigPath(date), new byte[sizeBytes]);
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     <see cref="VectorCompactionService"/> is now a Data-category
///     <see cref="IGuardian"/> (reframed from the daily CompactionHourUtc gate).
///     The <c>GuardianService</c> walker drives <see cref="VectorCompactionService.GuardAsync"/>
///     on <see cref="RetentionOptions.CompactionInterval"/>, so storage stays
///     bounded in near-real-time. These cover the guardian contract + that a pass
///     runs the store compaction and reports its outcome.
///
///     After the Phase 4 extraction to <c>CentroidRetentionGuardian</c>,
///     centroid stores are no longer injected here.
/// </summary>
public sealed class VectorCompactionServiceTickTests
{
    private static VectorCompactionService NewService(
        List<(string Signature, int SessionCount)> overflowing,
        TimeSpan? interval = null)
    {
        var opts = new BotDetectionOptions();
        if (interval is { } iv) opts.Retention.CompactionInterval = iv;

        var sessionStore = new Mock<IDetectionArchive>(MockBehavior.Loose);
        sessionStore
            .Setup(s => s.GetOverflowingSignaturesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(overflowing);
        sessionStore
            .Setup(s => s.CompactSignatureSessionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sig, int _, CancellationToken _) =>
                new CompactionResult { Signature = sig, CompactedCount = 5 });

        return new VectorCompactionService(
            sessionStore.Object,
            Options.Create(opts),
            NullLogger<VectorCompactionService>.Instance);
    }

    [Fact]
    public void Is_a_data_category_guardian_with_the_configured_interval()
    {
        var svc = NewService([], interval: TimeSpan.FromMinutes(15));

        svc.Should().BeAssignableTo<IGuardian>();
        svc.Name.Should().Be("VectorCompaction");
        svc.Category.Should().Be(GuardianCategory.Data);
        svc.Interval.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task GuardAsync_reports_ok_when_nothing_is_over_the_session_limit()
    {
        var svc = NewService([]); // no overflowing signatures

        var report = await svc.GuardAsync();

        report.GuardianName.Should().Be("VectorCompaction");
        report.Category.Should().Be(GuardianCategory.Data);
        report.Status.Should().Be("ok");
        report.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GuardAsync_reports_ok_when_sessions_overflow_but_no_cap_is_set()
    {
        // Phase 2 (session compaction) was extracted to SessionCompactionGuardian in Task 3.
        // VectorCompactionService now owns Phases 3-5 only. When sessions overflow but
        // MaxSignatures is 0 (cap disabled, the default), GuardAsync returns "ok" because
        // neither Phase 3 (HNSW) nor Phase 5 (cap enforcement) engages on this path.
        // SessionCompactionGuardianTests covers the compaction-count reporting contract.
        var svc = NewService([("sigA", 50), ("sigB", 40)]);

        var report = await svc.GuardAsync();

        report.Status.Should().Be("ok");
        report.GuardianName.Should().Be("VectorCompaction");
    }
}

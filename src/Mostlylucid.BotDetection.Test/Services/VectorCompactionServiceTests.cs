using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Tests for <see cref="VectorCompactionService"/>. After the guardian
///     decomposition this service owns only Phase 5 (cross-signature cap
///     enforcement). Phases 1-4 are covered by their respective guardian tests:
///     <list type="bullet">
///         <item><c>BucketRetentionGuardianTests</c> (Phase 1)</item>
///         <item><c>SessionCompactionGuardianTests</c> (Phase 2)</item>
///         <item><c>HnswCompactionGuardianTests</c> (Phase 3)</item>
///         <item><c>CentroidRetentionGuardianTests</c> (Phase 4)</item>
///     </list>
/// </summary>
public class VectorCompactionServiceTests
{
    // -----------------------------------------------------------------------
    // Helper: build VectorCompactionService (Phase 5 only after decomposition)
    // -----------------------------------------------------------------------

    private static VectorCompactionService Build(int maxSignatures = 0)
    {
        var options = new BotDetectionOptions();
        options.Retention.MaxSignatures = maxSignatures;

        var archiveMock = new Mock<IDetectionArchive>();
        archiveMock
            .Setup(s => s.GetOverflowingSignaturesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Signature, int SessionCount)>());

        return new VectorCompactionService(
            archiveMock.Object,
            Options.Create(options),
            NullLogger<VectorCompactionService>.Instance);
    }

    // -----------------------------------------------------------------------
    // Smoke test: RunCompactionAsync returns 0 now that Phases 1-4 are extracted
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunCompactionAsync_returns_zero_when_all_phases_are_extracted()
    {
        // After decomposition Phases 1-4 run in separate guardians. RunCompactionAsync
        // itself is now a no-op shell that returns 0 (no sessions compacted by this service).
        var svc = Build();

        var result = await svc.RunCompactionAsync(CancellationToken.None);

        Assert.Equal(0, result);
    }
}

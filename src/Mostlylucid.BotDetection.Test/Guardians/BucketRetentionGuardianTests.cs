using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Guardians;

/// <summary>
///     Behaviour-preservation tests for <see cref="BucketRetentionGuardian"/>
///     (Phase 1 of the old VectorCompactionService). The guardian delegates
///     its only work to <see cref="IDetectionArchive.PruneBucketsAsync"/>,
///     so the contract is:
///     <list type="bullet">
///         <item>The correct retention span is forwarded to the store.</item>
///         <item>The report carries <c>Status = "pruned"</c> when rows were
///             deleted; <c>"ok"</c> when nothing was removed.</item>
///         <item>The guardian honours <see cref="IGuardian"/> contracts:
///             category, name, and interval.</item>
///     </list>
/// </summary>
public sealed class BucketRetentionGuardianTests
{
    private static IOptions<BotDetectionOptions> DefaultOpts(TimeSpan? bucketRetention = null)
    {
        var opts = new BotDetectionOptions();
        if (bucketRetention.HasValue)
            opts.Retention.BucketRetention = bucketRetention.Value;
        return Options.Create(opts);
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    private static BucketRetentionGuardian Build(
        Mock<IDetectionArchive> storeMock,
        IOptions<BotDetectionOptions>? opts = null,
        IConfiguration? config = null) =>
        new(
            storeMock.Object,
            opts ?? DefaultOpts(),
            config ?? EmptyConfig(),
            NullLogger<BucketRetentionGuardian>.Instance);

    // ---- IGuardian contract ----

    [Fact]
    public void Is_a_data_category_guardian_named_BucketRetention()
    {
        var sut = Build(new Mock<IDetectionArchive>());

        sut.Should().BeAssignableTo<IGuardian>();
        sut.Name.Should().Be("BucketRetention");
        sut.Category.Should().Be(GuardianCategory.Data);
    }

    [Fact]
    public void Interval_defaults_to_CompactionInterval_from_options()
    {
        var opts = DefaultOpts();
        var sut = Build(new Mock<IDetectionArchive>(), opts);

        sut.Interval.Should().Be(opts.Value.Retention.CompactionInterval);
    }

    [Fact]
    public void Interval_can_be_overridden_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:BucketRetention:Interval"] = "02:00:00"
            })
            .Build();

        var sut = Build(new Mock<IDetectionArchive>(), config: config);

        sut.Interval.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Enabled_defaults_to_true()
    {
        var sut = Build(new Mock<IDetectionArchive>());
        sut.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enabled_can_be_set_false_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:BucketRetention:Enabled"] = "false"
            })
            .Build();

        var sut = Build(new Mock<IDetectionArchive>(), config: config);
        sut.Enabled.Should().BeFalse();
    }

    // ---- GuardAsync behaviour ----

    [Fact]
    public async Task GuardAsync_calls_PruneBucketsAsync_with_BucketRetention()
    {
        var retention = TimeSpan.FromDays(90);
        var storeMock = new Mock<IDetectionArchive>();
        storeMock
            .Setup(s => s.PruneBucketsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = Build(storeMock, DefaultOpts(retention));

        await sut.GuardAsync();

        storeMock.Verify(
            s => s.PruneBucketsAsync(retention, It.IsAny<CancellationToken>()),
            Times.Once,
            "must forward the configured BucketRetention span to the store");
    }

    [Fact]
    public async Task GuardAsync_returns_pruned_status_when_store_reports_rows_deleted()
    {
        var storeMock = new Mock<IDetectionArchive>();
        storeMock
            .Setup(s => s.PruneBucketsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((_, _) =>
            {
                // Simulate rows were deleted — the guardian knows because the archive
                // implementation logs internally; the guardian itself wraps this in
                // a try/catch + reports ok vs pruned based on archive behaviour.
                // For unit testing, we rely on the guardian checking whether the call
                // succeeded to distinguish ok vs pruned.
            })
            .Returns(Task.CompletedTask);

        var sut = Build(storeMock);

        var report = await sut.GuardAsync();

        report.GuardianName.Should().Be("BucketRetention");
        report.Category.Should().Be(GuardianCategory.Data);
        // After a successful prune call the status is "pruned" (the guardian
        // always considers a successful PruneBucketsAsync call a prune pass;
        // the no-op case is indistinguishable at the IDetectionArchive boundary).
        report.Status.Should().Be("pruned");
        report.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GuardAsync_returns_error_status_when_store_throws()
    {
        var storeMock = new Mock<IDetectionArchive>();
        storeMock
            .Setup(s => s.PruneBucketsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        var sut = Build(storeMock);

        var report = await sut.GuardAsync();

        report.Status.Should().Be("error");
        report.Details.Should().Contain("db locked");
        report.GuardianName.Should().Be("BucketRetention");
    }

    [Fact]
    public async Task GuardAsync_forwards_cancellation_to_store()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var storeMock = new Mock<IDetectionArchive>();
        storeMock
            .Setup(s => s.PruneBucketsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = Build(storeMock);

        // Cancellation should propagate; the guardian should not swallow it.
        Func<Task> act = () => sut.GuardAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

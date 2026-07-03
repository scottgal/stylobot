using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Guardians;

namespace Mostlylucid.BotDetection.Test.Guardians;

/// <summary>
///     Unit tests for the FOSS <see cref="GuardianService"/> walker: per-guardian
///     interval gating, error isolation + back-off, and report stamping. No
///     coordinator is passed, so tests drive <see cref="GuardianService.RunDueAsync"/>
///     one beat at a time with an injected clock.
/// </summary>
public sealed class GuardianServiceTests
{
    private sealed class FakeGuardian : IGuardian
    {
        public string Name { get; init; } = "fake";
        public GuardianCategory Category { get; init; } = GuardianCategory.Data;
        public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(20);
        public int Runs;
        public bool Throw;

        public Task<GuardianReport> GuardAsync(CancellationToken ct = default)
        {
            Runs++;
            if (Throw) throw new InvalidOperationException("boom");
            return Task.FromResult(GuardianReport.Ok(this, 1.0));
        }
    }

    private static GuardianService Svc(params IGuardian[] gs) =>
        new(gs, NullLogger<GuardianService>.Instance);

    [Fact]
    public async Task Runs_due_guardian_and_records_report()
    {
        var g = new FakeGuardian();
        var svc = Svc(g);

        await svc.RunDueAsync(DateTimeOffset.UtcNow);

        g.Runs.Should().Be(1);
        svc.LatestReports.Should().ContainKey("fake");
        svc.LatestReports["fake"].Status.Should().Be("ok");
    }

    [Fact]
    public async Task Does_not_rerun_before_interval_elapses()
    {
        var g = new FakeGuardian { Interval = TimeSpan.FromHours(6) };
        var svc = Svc(g);
        var t0 = DateTimeOffset.UtcNow;

        await svc.RunDueAsync(t0);
        await svc.RunDueAsync(t0.AddMinutes(5));   // still inside the interval
        g.Runs.Should().Be(1);

        await svc.RunDueAsync(t0.AddHours(6).AddMinutes(1)); // past the interval
        g.Runs.Should().Be(2);
    }

    [Fact]
    public async Task Stamps_report_time_from_the_walker_not_the_guardian()
    {
        var g = new FakeGuardian();
        var svc = Svc(g);
        var now = DateTimeOffset.UtcNow;

        await svc.RunDueAsync(now);

        svc.LatestReports["fake"].At.Should().Be(now);
    }

    [Fact]
    public async Task Throwing_guardian_is_isolated_recorded_and_backed_off()
    {
        var bad = new FakeGuardian { Name = "bad", Throw = true };
        var good = new FakeGuardian { Name = "good" };
        var svc = Svc(bad, good);
        var t0 = DateTimeOffset.UtcNow;

        await svc.RunDueAsync(t0);
        good.Runs.Should().Be(1);                          // peer unaffected
        svc.LatestReports["bad"].Status.Should().Be("error");

        await svc.RunDueAsync(t0.AddMinutes(1));            // inside the 5-min back-off
        bad.Runs.Should().Be(1);

        await svc.RunDueAsync(t0.AddMinutes(6));            // past the back-off
        bad.Runs.Should().Be(2);
    }

    [Fact]
    public void Guardian_count_reflects_registration()
    {
        var svc = Svc(new FakeGuardian { Name = "a" }, new FakeGuardian { Name = "b" });
        svc.Guardians.Should().HaveCount(2);
        svc.LatestReports.Should().BeEmpty(); // nothing run yet
    }
}

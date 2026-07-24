using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     TDD for <see cref="DashboardMaterializerAdaptiveController"/> -- the
///     measured-cost-vs-budget adaptive signal (superseding an earlier abandoned
///     "tick-overrun ratio" draft). Measures the ACTUAL wall-clock cost of a tick's
///     warm work, smooths it (EMA), and derives a uniform scale factor (never below
///     1.0) that <see cref="DashboardRefreshCadence.ComputeEffectiveIntervalSeconds"/>
///     applies to every page key.
/// </summary>
public sealed class DashboardMaterializerAdaptiveControllerTests
{
    private static DashboardMaterializerAdaptiveController Sut(int budgetMs = 1000, double alpha = 1.0) =>
        new(Options.Create(new DashboardMaterializerOptions
        {
            RefreshCostBudgetMs = budgetMs,
            AdaptiveCostSmoothingAlpha = alpha,
        }));

    [Fact]
    public void No_samples_yet_means_scale_factor_is_1()
    {
        var sut = Sut();
        Assert.Equal(1.0, sut.CurrentScaleFactor);
    }

    [Fact]
    public void Cost_under_budget_keeps_the_scale_factor_at_1()
    {
        var sut = Sut(budgetMs: 1000);
        sut.RecordTickCost(200);
        sut.RecordTickCost(300);
        Assert.Equal(1.0, sut.CurrentScaleFactor);
    }

    [Fact]
    public void Cost_over_budget_rises_the_scale_factor_above_1()
    {
        var sut = Sut(budgetMs: 1000, alpha: 1.0); // alpha=1 -> no smoothing lag, isolates escalation
        sut.RecordTickCost(2000); // 2x budget

        Assert.True(sut.CurrentScaleFactor > 1.0, "cost trending over budget must stretch every page key's interval");
        Assert.Equal(2.0, sut.CurrentScaleFactor, precision: 3);
    }

    [Fact]
    public void Sustained_over_budget_cost_escalates_the_scale_factor_further()
    {
        var sut = Sut(budgetMs: 1000, alpha: 0.5);
        var factors = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            sut.RecordTickCost(3000); // consistently 3x budget
            factors.Add(sut.CurrentScaleFactor);
        }

        // Monotonically non-decreasing while the input stays pinned above budget (EMA
        // climbing toward the sustained value from whatever it started at).
        for (var i = 1; i < factors.Count; i++)
            Assert.True(factors[i] >= factors[i - 1], "scale factor should not fall while cost stays pinned over budget");

        Assert.True(factors[^1] > 1.0);
    }

    [Fact]
    public void Scale_factor_relaxes_back_toward_1_as_cost_falls_back_under_budget()
    {
        var sut = Sut(budgetMs: 1000, alpha: 0.5);

        // Escalate first.
        for (var i = 0; i < 5; i++) sut.RecordTickCost(4000);
        var escalated = sut.CurrentScaleFactor;
        Assert.True(escalated > 1.0);

        // Then sustained under-budget cost should relax it back down.
        var relaxing = new List<double> { escalated };
        for (var i = 0; i < 10; i++)
        {
            sut.RecordTickCost(100); // well under budget
            relaxing.Add(sut.CurrentScaleFactor);
        }

        for (var i = 1; i < relaxing.Count; i++)
            Assert.True(relaxing[i] <= relaxing[i - 1], "scale factor must relax (never re-escalate) while cost stays under budget");

        Assert.True(relaxing[^1] < escalated, "sustained under-budget cost must actually bring the factor down");
    }

    [Fact]
    public void Scale_factor_never_drops_below_1_even_with_zero_cost()
    {
        var sut = Sut(budgetMs: 1000);
        sut.RecordTickCost(0);
        Assert.Equal(1.0, sut.CurrentScaleFactor);
    }

    [Fact]
    public void Negative_measured_cost_is_treated_as_zero_never_pushes_below_1()
    {
        var sut = Sut(budgetMs: 1000);
        sut.RecordTickCost(-500); // defensive: should never happen, but must not corrupt the smoothed estimate
        Assert.Equal(1.0, sut.CurrentScaleFactor);
    }

    [Fact]
    public void Misconfigured_zero_or_negative_budget_disables_throttling_rather_than_dividing_by_zero()
    {
        var sut = Sut(budgetMs: 0);
        sut.RecordTickCost(500);
        Assert.Equal(1.0, sut.CurrentScaleFactor);
    }

    [Fact]
    public void Smoothing_alpha_dampens_a_single_spike_below_the_raw_ratio()
    {
        // alpha=0.3: a single tick massively over budget shouldn't alone jump the scale
        // factor all the way to the raw cost/budget ratio.
        var sut = Sut(budgetMs: 1000, alpha: 0.3);
        sut.RecordTickCost(10_000); // 10x budget in one tick

        Assert.True(sut.CurrentScaleFactor < 10.0, "a single spike must be dampened by the EMA, not applied raw");
        Assert.True(sut.CurrentScaleFactor > 1.0);
    }
}

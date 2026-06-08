using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Decisions;

public class SqlitePolicyDecisionLogTests
{
    private static PolicyDecision SampleDecision(Guid ruleId, Guid? winnerId = null, bool matched = true,
        string? fp = null, PolicyAction? action = null, PolicyMode mode = PolicyMode.Live, long ticks = 4000) =>
        new(
            RuleId: ruleId,
            WinnerRuleId: winnerId ?? ruleId,
            Matched: matched,
            RequestFingerprint: fp ?? "fp-default",
            Action: action ?? new PolicyAction.Block(),
            Mode: mode,
            EvalLatencyTicks: ticks,
            ObservedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task Decision_log_aggregates_by_rule_and_window()
    {
        await using var log = new SqlitePolicyDecisionLog(":memory:");
        var ruleId = Guid.NewGuid();
        for (var i = 0; i < 18; i++)
            await log.AppendAsync(SampleDecision(ruleId, fp: $"fp{i}"));
        await log.FlushAsync();

        var agg = await log.AggregateAsync(ruleId, TimeSpan.FromHours(24));
        Assert.Equal(18, agg.Matched);
        Assert.Equal(18, agg.TotalEvaluations);
        Assert.Equal(18, agg.WinDistribution["block"]);
        Assert.InRange(agg.P50LatencyMicros, 350L, 450L);
    }

    [Fact]
    public async Task Matched_vs_total_distinguishes_lost_rules()
    {
        await using var log = new SqlitePolicyDecisionLog(":memory:");
        var ruleA = Guid.NewGuid();
        var ruleB = Guid.NewGuid();

        await log.AppendAsync(SampleDecision(ruleA, ruleA, true));
        await log.AppendAsync(SampleDecision(ruleA, ruleA, true));
        await log.AppendAsync(SampleDecision(ruleA, ruleB, true));
        await log.AppendAsync(SampleDecision(ruleA, ruleA, false));
        await log.AppendAsync(SampleDecision(ruleA, ruleA, false));
        await log.AppendAsync(SampleDecision(ruleA, ruleA, false));
        await log.FlushAsync();

        var agg = await log.AggregateAsync(ruleA, TimeSpan.FromHours(24));
        Assert.Equal(3, agg.Matched);
        Assert.Equal(6, agg.TotalEvaluations);
    }

    [Fact]
    public async Task Window_excludes_rows_older_than_horizon()
    {
        await using var log = new SqlitePolicyDecisionLog(":memory:");
        var ruleId = Guid.NewGuid();

        var old = SampleDecision(ruleId) with { ObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2) };
        await log.AppendAsync(old);

        await log.AppendAsync(SampleDecision(ruleId));
        await log.FlushAsync();

        var agg = await log.AggregateAsync(ruleId, TimeSpan.FromMinutes(30));
        Assert.Equal(1, agg.Matched);
    }

    [Fact]
    public async Task Get_by_fingerprint_returns_ordered_rows()
    {
        await using var log = new SqlitePolicyDecisionLog(":memory:");
        var fp = "fp-X";
        var ruleId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            await log.AppendAsync(SampleDecision(ruleId, fp: fp) with { ObservedAt = DateTimeOffset.UtcNow.AddSeconds(i) });
        await log.FlushAsync();

        var rows = await log.GetByFingerprintAsync(fp);
        Assert.Equal(5, rows.Count);
        for (var i = 1; i < rows.Count; i++)
            Assert.True(rows[i].ObservedAt >= rows[i - 1].ObservedAt);
    }

    [Fact]
    public async Task Action_kind_lowercase_in_distribution()
    {
        await using var log = new SqlitePolicyDecisionLog(":memory:");
        var ruleId = Guid.NewGuid();
        await log.AppendAsync(SampleDecision(ruleId, action: new PolicyAction.Block()));
        await log.AppendAsync(SampleDecision(ruleId, action: new PolicyAction.Challenge("turnstile")));
        await log.AppendAsync(SampleDecision(ruleId, action: new PolicyAction.Allow()));
        await log.FlushAsync();

        var agg = await log.AggregateAsync(ruleId, TimeSpan.FromHours(24));
        Assert.True(agg.WinDistribution.ContainsKey("block"));
        Assert.True(agg.WinDistribution.ContainsKey("challenge"));
        Assert.True(agg.WinDistribution.ContainsKey("allow"));
    }

    [Fact]
    public async Task In_memory_impl_aggregates_correctly()
    {
        var log = new InMemoryPolicyDecisionLog();
        var ruleId = Guid.NewGuid();
        for (var i = 0; i < 7; i++)
            await log.AppendAsync(SampleDecision(ruleId));

        var agg = await log.AggregateAsync(ruleId, TimeSpan.FromHours(24));
        Assert.Equal(7, agg.Matched);
        Assert.Equal(7, agg.WinDistribution["block"]);
    }
}

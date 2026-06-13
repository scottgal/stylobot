using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Rules;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.Decisions;

public sealed class InMemoryPolicyDecisionLogRecentFingerprintsTests
{
    private static PolicyDecision MakeDecision(string fingerprintId, DateTimeOffset observedAt, PolicyAction? action = null) =>
        new(
            RuleId: Guid.NewGuid(),
            WinnerRuleId: Guid.NewGuid(),
            Matched: true,
            RequestFingerprint: fingerprintId,
            Action: action ?? new PolicyAction.Allow(),
            Mode: PolicyMode.Live,
            EvalLatencyTicks: 1000,
            ObservedAt: observedAt);

    [Fact]
    public async Task GetRecentFingerprintsAsync_returns_distinct_most_recent_first_capped()
    {
        var log = new InMemoryPolicyDecisionLog();
        var now = DateTimeOffset.UtcNow;

        // Three distinct fingerprints, one repeated. Repeat is the most recent
        // overall so the dedupe-then-order pass must surface fpB first.
        await log.AppendAsync(MakeDecision("fpA", now.AddMinutes(-30)));
        await log.AppendAsync(MakeDecision("fpB", now.AddMinutes(-20), new PolicyAction.Block()));
        await log.AppendAsync(MakeDecision("fpC", now.AddMinutes(-10)));
        await log.AppendAsync(MakeDecision("fpB", now.AddMinutes(-1)));

        var ids = await log.GetRecentFingerprintsAsync(limit: 2);

        Assert.Equal(new[] { "fpB", "fpC" }, ids);
    }

    [Fact]
    public async Task GetRecentFingerprintsAsync_returns_empty_when_no_rows()
    {
        var log = new InMemoryPolicyDecisionLog();
        var ids = await log.GetRecentFingerprintsAsync(limit: 25);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetRecentFingerprintsAsync_clamps_limit_to_one_minimum()
    {
        var log = new InMemoryPolicyDecisionLog();
        await log.AppendAsync(MakeDecision("fpZ", DateTimeOffset.UtcNow));

        var ids = await log.GetRecentFingerprintsAsync(limit: 0);
        Assert.Single(ids);
    }
}
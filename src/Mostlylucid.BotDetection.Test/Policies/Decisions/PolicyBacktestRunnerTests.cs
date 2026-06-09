using System.Text.Json;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Decisions;

/// <summary>
///     C8 -- PolicyBacktestRunner coverage. The runner streams the decision
///     log within a window and projects a candidate predicate against each
///     row's signals snapshot, bucketing into would-match / would-enforce /
///     overridden / inconclusive. These tests exercise the SQLite log + the
///     in-memory log so the streaming and the deserialised projection are
///     both covered, and pin the "row without snapshot" caveat path.
/// </summary>
public class PolicyBacktestRunnerTests
{
    private static string Snapshot(params (string key, object value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) dict[k] = v;
        return JsonSerializer.Serialize(dict);
    }

    private static PolicyDecision Row(
        Guid ruleId,
        Guid? winnerId = null,
        bool matched = true,
        string? fp = null,
        PolicyAction? action = null,
        long ticks = 4000,
        string? snapshot = null,
        DateTimeOffset? at = null) =>
        new(
            RuleId: ruleId,
            WinnerRuleId: winnerId ?? ruleId,
            Matched: matched,
            RequestFingerprint: fp ?? "fp-default",
            Action: action ?? new PolicyAction.Block(),
            Mode: PolicyMode.Live,
            EvalLatencyTicks: ticks,
            ObservedAt: at ?? DateTimeOffset.UtcNow,
            SignalsSnapshot: snapshot);

    [Fact]
    public async Task Backtest_projects_proposed_predicate_against_decision_log_window()
    {
        var log = new InMemoryPolicyDecisionLog();
        var ruleId = Guid.NewGuid();
        var fpHit = "fp-bot";
        var fpMiss = "fp-human";

        // Three distinct requests, each with a snapshot. Two match the
        // candidate "bot.type = scraper" and one doesn't.
        await log.AppendAsync(Row(ruleId, fp: fpHit,
            snapshot: Snapshot(("bot.type", "scraper"))));
        await log.AppendAsync(Row(ruleId, fp: fpHit + "-2",
            snapshot: Snapshot(("bot.type", "scraper"))));
        await log.AppendAsync(Row(ruleId, fp: fpMiss,
            snapshot: Snapshot(("bot.type", "human"))));

        var candidate = PredicateParser.Parse("bot.type = scraper");
        var runner = new PolicyBacktestRunner(log);
        var result = await runner.RunAsync(candidate, TimeSpan.FromHours(24));

        Assert.Equal(3, result.TotalRequests);
        Assert.Equal(2, result.WouldMatch);
        Assert.Equal(2, result.WouldEnforce);
        Assert.Equal(0, result.OverriddenByHigherPriority);
        Assert.Equal(2, result.SampleMatchingFingerprints.Count);
        Assert.Null(result.Caveat);
    }

    [Fact]
    public async Task Backtest_returns_zero_when_no_rows_match()
    {
        var log = new InMemoryPolicyDecisionLog();
        var ruleId = Guid.NewGuid();
        await log.AppendAsync(Row(ruleId, fp: "fp-a",
            snapshot: Snapshot(("bot.type", "human"))));
        await log.AppendAsync(Row(ruleId, fp: "fp-b",
            snapshot: Snapshot(("bot.type", "human"))));

        var candidate = PredicateParser.Parse("bot.type = scraper");
        var runner = new PolicyBacktestRunner(log);
        var result = await runner.RunAsync(candidate, TimeSpan.FromHours(24));

        Assert.Equal(2, result.TotalRequests);
        Assert.Equal(0, result.WouldMatch);
        Assert.Equal(0, result.WouldEnforce);
        Assert.Equal(0, result.OverriddenByHigherPriority);
        Assert.Empty(result.SampleMatchingFingerprints);
    }

    [Fact]
    public async Task Backtest_excludes_rows_older_than_window()
    {
        var log = new InMemoryPolicyDecisionLog();
        var ruleId = Guid.NewGuid();

        await log.AppendAsync(Row(ruleId, fp: "fp-old",
            snapshot: Snapshot(("bot.type", "scraper")),
            at: DateTimeOffset.UtcNow - TimeSpan.FromHours(48)));
        await log.AppendAsync(Row(ruleId, fp: "fp-new",
            snapshot: Snapshot(("bot.type", "scraper"))));

        var candidate = PredicateParser.Parse("bot.type = scraper");
        var runner = new PolicyBacktestRunner(log);
        var result = await runner.RunAsync(candidate, TimeSpan.FromHours(24));

        // Only the recent row should appear.
        Assert.Equal(1, result.TotalRequests);
        Assert.Equal(1, result.WouldMatch);
        Assert.Single(result.SampleMatchingFingerprints);
        Assert.Equal("fp-new", result.SampleMatchingFingerprints[0]);
    }

    [Fact]
    public async Task Backtest_classifies_matches_won_by_higher_priority_allow_as_overridden()
    {
        var log = new InMemoryPolicyDecisionLog();
        var allowRuleId = Guid.NewGuid();
        var blockRuleId = Guid.NewGuid();
        var fp = "fp-allow-wins";
        var sameMoment = DateTimeOffset.UtcNow;

        // Two rows for the SAME request: an earlier Allow rule that won
        // (matched=true, winner=self, action=Allow) and a Block rule
        // recorded against the same evaluation (skipped because Allow
        // short-circuited). The candidate is the operator's proposed
        // predicate -- it matches the snapshot, but Allow already won
        // so the candidate would be overridden.
        var snap = Snapshot(("bot.type", "scraper"));
        await log.AppendAsync(Row(allowRuleId,
            winnerId: allowRuleId, matched: true, fp: fp,
            action: new PolicyAction.Allow(), snapshot: snap, at: sameMoment));
        await log.AppendAsync(Row(blockRuleId,
            winnerId: allowRuleId, matched: false, fp: fp,
            action: new PolicyAction.Block(), snapshot: snap, at: sameMoment));

        var candidate = PredicateParser.Parse("bot.type = scraper");
        var runner = new PolicyBacktestRunner(log);
        var result = await runner.RunAsync(candidate, TimeSpan.FromHours(24));

        Assert.Equal(1, result.TotalRequests);
        Assert.Equal(1, result.WouldMatch);
        Assert.Equal(0, result.WouldEnforce);
        Assert.Equal(1, result.OverriddenByHigherPriority);
    }

    [Fact]
    public async Task Backtest_surfaces_caveat_when_no_snapshot_present()
    {
        var log = new InMemoryPolicyDecisionLog();
        var ruleId = Guid.NewGuid();

        // No SignalsSnapshot on either row -- pre-C8-patch state.
        await log.AppendAsync(Row(ruleId, fp: "fp-1", snapshot: null));
        await log.AppendAsync(Row(ruleId, fp: "fp-2", snapshot: null));

        var candidate = PredicateParser.Parse("bot.type = scraper");
        var runner = new PolicyBacktestRunner(log);
        var result = await runner.RunAsync(candidate, TimeSpan.FromHours(24));

        Assert.Equal(2, result.TotalRequests);
        Assert.Equal(0, result.WouldMatch);
        Assert.NotNull(result.Caveat);
        Assert.Contains("snapshot", result.Caveat!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Backtest_caps_sample_fingerprints_at_twenty_five()
    {
        var log = new InMemoryPolicyDecisionLog();
        var ruleId = Guid.NewGuid();
        var snap = Snapshot(("bot.type", "scraper"));

        for (var i = 0; i < 60; i++)
            await log.AppendAsync(Row(ruleId, fp: $"fp-{i}", snapshot: snap,
                at: DateTimeOffset.UtcNow.AddSeconds(i)));

        var candidate = PredicateParser.Parse("bot.type = scraper");
        var runner = new PolicyBacktestRunner(log);
        var result = await runner.RunAsync(candidate, TimeSpan.FromHours(24));

        Assert.Equal(60, result.TotalRequests);
        Assert.Equal(60, result.WouldMatch);
        Assert.Equal(PolicyBacktestRunner.SampleFingerprintCap, result.SampleMatchingFingerprints.Count);
    }

    [Fact]
    public async Task Sqlite_decision_log_streamwindow_orders_by_observed_at_ascending()
    {
        await using var log = new SqlitePolicyDecisionLog(":memory:");
        var ruleId = Guid.NewGuid();

        // Append in reverse-time order; StreamWindowAsync must return them
        // ordered ascending.
        for (var i = 4; i >= 0; i--)
        {
            await log.AppendAsync(Row(ruleId, fp: $"fp-{i}",
                snapshot: Snapshot(("seq", i)),
                at: DateTimeOffset.UtcNow.AddSeconds(i)));
        }
        await log.FlushAsync();

        var observedFps = new List<string>();
        await foreach (var row in log.StreamWindowAsync(TimeSpan.FromHours(24)))
        {
            if (row.RequestFingerprint is { } fp) observedFps.Add(fp);
        }

        Assert.Equal(5, observedFps.Count);
        Assert.Equal(new[] { "fp-0", "fp-1", "fp-2", "fp-3", "fp-4" }, observedFps);
    }

    [Fact]
    public async Task Sqlite_decision_log_streamwindow_respects_window_cutoff()
    {
        await using var log = new SqlitePolicyDecisionLog(":memory:");
        var ruleId = Guid.NewGuid();

        await log.AppendAsync(Row(ruleId, fp: "fp-old",
            snapshot: Snapshot(("bot.type", "scraper")),
            at: DateTimeOffset.UtcNow - TimeSpan.FromHours(2)));
        await log.AppendAsync(Row(ruleId, fp: "fp-new",
            snapshot: Snapshot(("bot.type", "scraper"))));
        await log.FlushAsync();

        var observedFps = new List<string>();
        await foreach (var row in log.StreamWindowAsync(TimeSpan.FromMinutes(30)))
        {
            if (row.RequestFingerprint is { } fp) observedFps.Add(fp);
        }

        Assert.Single(observedFps);
        Assert.Equal("fp-new", observedFps[0]);
    }

    [Fact]
    public async Task Sqlite_decision_log_round_trips_signals_snapshot()
    {
        await using var log = new SqlitePolicyDecisionLog(":memory:");
        var ruleId = Guid.NewGuid();
        var fp = "fp-rt";
        var snap = Snapshot(("bot.type", "scraper"), ("score.bot_probability", 0.83m));

        await log.AppendAsync(Row(ruleId, fp: fp, snapshot: snap));
        await log.FlushAsync();

        var rows = await log.GetByFingerprintAsync(fp);
        Assert.Single(rows);
        Assert.Equal(snap, rows[0].SignalsSnapshot);
    }
}

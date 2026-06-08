using Mostlylucid.BotDetection.Policies.Predicate;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.Predicate;

/// <summary>
///     Coverage for <see cref="PredicateTraceEvaluator"/>. The tests verify
///     that the trace's per-term outcomes mirror the verdict
///     <see cref="PredicateEvaluator.Evaluate"/> would have returned (same
///     short-circuit semantics, same silent-miss-on-unknown-facet rule), plus
///     the extra per-term latency + failure-reason channel the explainer
///     panel depends on.
/// </summary>
public sealed class PredicateTraceEvaluatorTests
{
    [Fact]
    public void Records_per_term_outcomes_and_latency()
    {
        var predicate = PredicateParser.Parse(
            "bot.type in (scraper, crawler) and score.bot_probability >= 0.7");
        var signals = new Dictionary<string, object?>
        {
            ["bot.type"] = "scraper",
            ["score.bot_probability"] = 0.92m
        };

        var trace = PredicateTraceEvaluator.Trace(predicate, signals);

        Assert.True(trace.OverallMatched);
        Assert.Equal(2, trace.Terms.Count);
        Assert.All(trace.Terms, t => Assert.True(t.Matched));
        Assert.All(trace.Terms, t => Assert.True(t.EvalTicks >= 0));
        Assert.True(trace.TotalTicks >= 0);
    }

    [Fact]
    public void Marks_short_circuited_And_children_as_skipped()
    {
        var predicate = PredicateParser.Parse(
            "bot.type = scraper and score.bot_probability >= 0.7");
        var signals = new Dictionary<string, object?>
        {
            ["bot.type"] = "human",
            ["score.bot_probability"] = 0.92m
        };

        var trace = PredicateTraceEvaluator.Trace(predicate, signals);

        Assert.False(trace.OverallMatched);
        Assert.Equal(2, trace.Terms.Count);
        Assert.False(trace.Terms[0].Matched);
        Assert.False(trace.Terms[1].Matched);
        Assert.NotNull(trace.Terms[1].FailureReason);
        Assert.Contains("earlier And child failed", trace.Terms[1].FailureReason!);
        Assert.Equal(0L, trace.Terms[1].EvalTicks);
    }

    [Fact]
    public void Marks_short_circuited_Or_children_as_skipped_when_first_wins()
    {
        var predicate = PredicateParser.Parse(
            "bot.type = scraper or geo.country = CN");
        var signals = new Dictionary<string, object?>
        {
            ["bot.type"] = "scraper",
            ["geo.country"] = "CN"
        };

        var trace = PredicateTraceEvaluator.Trace(predicate, signals);

        Assert.True(trace.OverallMatched);
        Assert.Equal(2, trace.Terms.Count);
        Assert.True(trace.Terms[0].Matched);
        // Second child is skipped because the first Or child already won.
        Assert.False(trace.Terms[1].Matched);
        Assert.NotNull(trace.Terms[1].FailureReason);
        Assert.Contains("earlier Or child won", trace.Terms[1].FailureReason!);
        Assert.Equal(0L, trace.Terms[1].EvalTicks);
    }

    [Fact]
    public void Missing_facet_emits_failure_reason_facet_missing()
    {
        var predicate = PredicateParser.Parse("bot.type = scraper");
        var signals = new Dictionary<string, object?>();

        var trace = PredicateTraceEvaluator.Trace(predicate, signals);

        Assert.False(trace.OverallMatched);
        Assert.Single(trace.Terms);
        Assert.False(trace.Terms[0].Matched);
        Assert.Equal("facet missing", trace.Terms[0].FailureReason);
        Assert.Null(trace.Terms[0].ActualFacetValue);
    }

    [Fact]
    public void Failure_reason_surfaces_actual_and_expected_for_inequality()
    {
        var predicate = PredicateParser.Parse("score.bot_probability >= 0.7");
        var signals = new Dictionary<string, object?>
        {
            ["score.bot_probability"] = 0.42m
        };

        var trace = PredicateTraceEvaluator.Trace(predicate, signals);

        Assert.False(trace.OverallMatched);
        Assert.Single(trace.Terms);
        Assert.NotNull(trace.Terms[0].FailureReason);
        Assert.Contains("0.42", trace.Terms[0].FailureReason!);
        Assert.Contains("0.7", trace.Terms[0].FailureReason!);
    }

    [Fact]
    public void Failure_reason_surfaces_list_expectations_for_in_op()
    {
        var predicate = PredicateParser.Parse("bot.type in (scraper, crawler)");
        var signals = new Dictionary<string, object?>
        {
            ["bot.type"] = "human"
        };

        var trace = PredicateTraceEvaluator.Trace(predicate, signals);

        Assert.False(trace.OverallMatched);
        Assert.Single(trace.Terms);
        Assert.NotNull(trace.Terms[0].FailureReason);
        Assert.Contains("scraper", trace.Terms[0].FailureReason!);
        Assert.Contains("crawler", trace.Terms[0].FailureReason!);
    }

    [Fact]
    public void Overall_verdict_matches_PredicateEvaluator_on_mixed_AndOr()
    {
        var predicate = PredicateParser.Parse(
            "(bot.type = scraper or bot.type = crawler) and score.bot_probability >= 0.5");
        var signals = new Dictionary<string, object?>
        {
            ["bot.type"] = "crawler",
            ["score.bot_probability"] = 0.81m
        };

        var trace = PredicateTraceEvaluator.Trace(predicate, signals);
        var direct = PredicateEvaluator.Evaluate(predicate, signals);

        Assert.Equal(direct, trace.OverallMatched);
        Assert.True(trace.OverallMatched);
    }

    [Fact]
    public void Total_ticks_is_sum_of_term_ticks()
    {
        var predicate = PredicateParser.Parse(
            "bot.type = scraper and geo.country = CN");
        var signals = new Dictionary<string, object?>
        {
            ["bot.type"] = "scraper",
            ["geo.country"] = "CN"
        };

        var trace = PredicateTraceEvaluator.Trace(predicate, signals);

        long sum = 0;
        foreach (var term in trace.Terms) sum += term.EvalTicks;
        Assert.Equal(sum, trace.TotalTicks);
    }
}

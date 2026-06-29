using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Predicate;

/// <summary>
///     Presentation + diagnostic coverage for the <c>InCidr</c> arms in
///     <see cref="PredicateFormatter"/> + <see cref="PredicateTraceEvaluator"/>.
///
///     <para>
///     Sister tests to <c>InCidrEvaluatorTests</c>: the evaluator owns the
///     match decision; these tests pin the human-readable surface (operator
///     token + trace failure reason) so the bidirectional CIDR editor and the
///     policy explainer can rely on a stable format.
///     </para>
/// </summary>
public class InCidrFormatterAndTraceTests
{
    // -------- PredicateFormatter.Format --------

    /// <summary>
    ///     Formatter emits the <c>in_cidr</c> token between facet + value.
    ///     Matches the JSON wire-form spelling in <c>PredicateAst.OpToString</c>
    ///     so the editor's serialised form and the formatter's canonical text
    ///     form agree on the operator name.
    /// </summary>
    [Fact]
    public void Format_renders_in_cidr_term_with_canonical_token()
    {
        Mostlylucid.BotDetection.Policies.Predicate.Predicate term =
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
                SignalKeys.ClientIp, PredicateOp.InCidr, "192.168.0.0/16");

        var formatted = PredicateFormatter.Format(term);

        Assert.Equal("ip.address in_cidr 192.168.0.0/16", formatted);
    }

    /// <summary>
    ///     List values render with the same parenthesised, comma-separated
    ///     style every other set op uses (<c>in</c>, <c>any in</c>, etc.).
    /// </summary>
    [Fact]
    public void Format_renders_in_cidr_term_with_list_value()
    {
        Mostlylucid.BotDetection.Policies.Predicate.Predicate term =
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
                SignalKeys.ClientIp,
                PredicateOp.InCidr,
                new[] { "10.0.0.0/8", "192.168.0.0/16" });

        var formatted = PredicateFormatter.Format(term);

        Assert.Equal("ip.address in_cidr (10.0.0.0/8, 192.168.0.0/16)", formatted);
    }

    // -------- PredicateTraceEvaluator.Trace --------

    /// <summary>
    ///     Matched in_cidr term produces a per-term outcome with
    ///     <c>Matched=true</c> and no failure reason, mirroring every other
    ///     matched-term arm.
    /// </summary>
    [Fact]
    public void Trace_records_matched_in_cidr_outcome_when_ip_is_in_subnet()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = "192.168.1.5"
        };
        Mostlylucid.BotDetection.Policies.Predicate.Predicate term =
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
                SignalKeys.ClientIp, PredicateOp.InCidr, "192.168.0.0/16");

        var trace = PredicateTraceEvaluator.Trace(term, signals);

        Assert.True(trace.OverallMatched);
        Assert.Single(trace.Terms);
        Assert.True(trace.Terms[0].Matched);
        Assert.Null(trace.Terms[0].FailureReason);
        Assert.Equal("192.168.1.5", trace.Terms[0].ActualFacetValue);
    }

    /// <summary>
    ///     Failure reason on a missed in_cidr term names BOTH the actual IP
    ///     and the CIDR it was tested against -- mirrors how the <c>In</c> /
    ///     <c>Matches</c> arms surface actual + expected. The explainer panel
    ///     renders this string verbatim.
    /// </summary>
    [Fact]
    public void Trace_failure_reason_names_actual_ip_and_cidr_on_miss()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = "8.8.8.8"
        };
        Mostlylucid.BotDetection.Policies.Predicate.Predicate term =
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
                SignalKeys.ClientIp, PredicateOp.InCidr, "192.168.0.0/16");

        var trace = PredicateTraceEvaluator.Trace(term, signals);

        Assert.False(trace.OverallMatched);
        Assert.Single(trace.Terms);
        Assert.False(trace.Terms[0].Matched);
        Assert.NotNull(trace.Terms[0].FailureReason);
        Assert.Contains("8.8.8.8", trace.Terms[0].FailureReason!);
        Assert.Contains("192.168.0.0/16", trace.Terms[0].FailureReason!);
    }

    /// <summary>
    ///     A list-valued in_cidr term that doesn't match any CIDR surfaces
    ///     the rendered list in the failure reason, same as the <c>In</c>
    ///     arm's <c>RenderList</c> formatting.
    /// </summary>
    [Fact]
    public void Trace_failure_reason_renders_full_cidr_list_on_miss()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = "8.8.8.8"
        };
        Mostlylucid.BotDetection.Policies.Predicate.Predicate term =
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
                SignalKeys.ClientIp,
                PredicateOp.InCidr,
                new[] { "10.0.0.0/8", "192.168.0.0/16" });

        var trace = PredicateTraceEvaluator.Trace(term, signals);

        Assert.False(trace.OverallMatched);
        Assert.Single(trace.Terms);
        Assert.False(trace.Terms[0].Matched);
        Assert.NotNull(trace.Terms[0].FailureReason);
        Assert.Contains("8.8.8.8", trace.Terms[0].FailureReason!);
        Assert.Contains("10.0.0.0/8", trace.Terms[0].FailureReason!);
        Assert.Contains("192.168.0.0/16", trace.Terms[0].FailureReason!);
    }

    /// <summary>
    ///     Trace verdict tracks <see cref="PredicateEvaluator"/> exactly --
    ///     same match decision for the same inputs. This is the cross-check
    ///     that proves the trace evaluator's arm isn't a parallel
    ///     implementation diverging from the real evaluator.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.5", "192.168.0.0/16", true)]
    [InlineData("8.8.8.8",     "192.168.0.0/16", false)]
    [InlineData("2001:db8::1", "2001:db8::/32",  true)]
    public void Trace_verdict_matches_PredicateEvaluator_for_in_cidr(string ip, string cidr, bool expected)
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = ip
        };
        Mostlylucid.BotDetection.Policies.Predicate.Predicate term =
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
                SignalKeys.ClientIp, PredicateOp.InCidr, cidr);

        var trace = PredicateTraceEvaluator.Trace(term, signals);
        var direct = PredicateEvaluator.Evaluate(term, signals);

        Assert.Equal(expected, direct);
        Assert.Equal(direct, trace.OverallMatched);
    }
}
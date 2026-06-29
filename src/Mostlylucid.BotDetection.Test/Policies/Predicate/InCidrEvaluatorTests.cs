using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Predicate;

/// <summary>
///     Coverage for the <c>InCidr</c> case in <see cref="PredicateEvaluator"/>.
///     Confirms IPv4 + IPv6 single-CIDR matching, list-of-CIDRs (any-match),
///     and the silent-miss / silent-malformed semantics the rest of the
///     evaluator follows (return <c>false</c>, never throw).
///
///     The evaluator delegates to <c>CidrHelper.IsInSubnet</c>; these tests
///     guard the *wiring* (case in the switch + signal-shape contract), not
///     the helper's internal correctness which has its own coverage.
/// </summary>
public class InCidrEvaluatorTests
{
    [Theory]
    [InlineData("192.168.1.5", "192.168.0.0/16", true)]
    [InlineData("10.0.0.42",   "10.0.0.0/8",     true)]
    [InlineData("8.8.8.8",     "192.168.0.0/16", false)]
    [InlineData("172.20.5.5",  "172.16.0.0/12",  true)]
    [InlineData("2001:db8::1", "2001:db8::/32",  true)]
    [InlineData("2001:db8::1", "2001:dead::/32", false)]
    public void InCidr_matches_single_cidr_string(string ip, string cidr, bool expected)
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal) { [SignalKeys.ClientIp] = ip };
        var term = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
            SignalKeys.ClientIp, PredicateOp.InCidr, cidr);

        Assert.Equal(expected, PredicateEvaluator.EvaluateStateless(term, signals));
    }

    [Fact]
    public void InCidr_matches_any_in_list_value()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = "192.168.5.5"
        };
        var term = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
            SignalKeys.ClientIp,
            PredicateOp.InCidr,
            new[] { "10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12" });

        Assert.True(PredicateEvaluator.EvaluateStateless(term, signals));
    }

    [Fact]
    public void InCidr_returns_false_when_no_list_entry_matches()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = "8.8.8.8"
        };
        var term = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
            SignalKeys.ClientIp,
            PredicateOp.InCidr,
            new[] { "10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12" });

        Assert.False(PredicateEvaluator.EvaluateStateless(term, signals));
    }

    [Fact]
    public void InCidr_returns_false_when_signal_missing()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal);
        var term = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
            SignalKeys.ClientIp, PredicateOp.InCidr, "10.0.0.0/8");

        Assert.False(PredicateEvaluator.EvaluateStateless(term, signals));
    }

    [Fact]
    public void InCidr_returns_false_when_signal_null()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal) { [SignalKeys.ClientIp] = null };
        var term = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
            SignalKeys.ClientIp, PredicateOp.InCidr, "10.0.0.0/8");

        Assert.False(PredicateEvaluator.EvaluateStateless(term, signals));
    }

    [Fact]
    public void InCidr_returns_false_for_malformed_cidr()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = "192.168.1.5"
        };
        var term = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
            SignalKeys.ClientIp, PredicateOp.InCidr, "not-a-cidr");

        Assert.False(PredicateEvaluator.EvaluateStateless(term, signals));
    }

    [Fact]
    public void InCidr_returns_false_for_malformed_ip_value()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = "not-an-ip"
        };
        var term = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
            SignalKeys.ClientIp, PredicateOp.InCidr, "10.0.0.0/8");

        Assert.False(PredicateEvaluator.EvaluateStateless(term, signals));
    }

    [Fact]
    public void InCidr_returns_false_for_empty_list_value()
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = "192.168.1.5"
        };
        var term = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
            SignalKeys.ClientIp, PredicateOp.InCidr, Array.Empty<string>());

        Assert.False(PredicateEvaluator.EvaluateStateless(term, signals));
    }

    [Fact]
    public void InCidr_returns_false_for_non_string_value_object()
    {
        // Defensive: if a misconfigured/loaded predicate has a non-string CIDR
        // value, the evaluator must follow the "silent miss" convention.
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SignalKeys.ClientIp] = "192.168.1.5"
        };
        var term = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
            SignalKeys.ClientIp, PredicateOp.InCidr, 42);

        Assert.False(PredicateEvaluator.EvaluateStateless(term, signals));
    }
}
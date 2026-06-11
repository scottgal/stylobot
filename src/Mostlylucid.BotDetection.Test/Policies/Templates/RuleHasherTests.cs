using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     Pin <see cref="RuleHasher"/>'s determinism and per-field sensitivity.
///     The materializer's override-divergence detection depends on these two
///     properties: same rule -> same hash (no false positives) and any change
///     to predicate/action/priority/mode -> different hash (no false negatives).
/// </summary>
public class RuleHasherTests
{
    private static PolicyRule Make(
        Mostlylucid.BotDetection.Policies.Predicate.Predicate predicate,
        PolicyAction action,
        int priority,
        PolicyMode mode) => new(
        Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Scope: PolicyScope.Wildcard(),
        Priority: priority,
        Predicate: predicate,
        Action: action,
        Mode: mode,
        Notes: "",
        Source: "test",
        CreatedAt: DateTimeOffset.UnixEpoch,
        RevisionId: Guid.NewGuid());

    private static Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term IsBot(bool value) =>
        new("is_bot", PredicateOp.Eq, value);

    [Fact]
    public void Same_rule_returns_same_hex_hash()
    {
        var r = Make(IsBot(true), new PolicyAction.Block(), 100, PolicyMode.Live);

        var a = RuleHasher.Hash(r);
        var b = RuleHasher.Hash(r);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Two_structurally_equal_rules_return_same_hash()
    {
        var r1 = Make(IsBot(true), new PolicyAction.Block(), 100, PolicyMode.Live);
        var r2 = Make(IsBot(true), new PolicyAction.Block(), 100, PolicyMode.Live);

        Assert.Equal(RuleHasher.Hash(r1), RuleHasher.Hash(r2));
    }

    [Fact]
    public void Predicate_change_alters_hash()
    {
        var a = RuleHasher.Hash(Make(IsBot(true), new PolicyAction.Block(), 100, PolicyMode.Live));
        var b = RuleHasher.Hash(Make(IsBot(false), new PolicyAction.Block(), 100, PolicyMode.Live));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Action_change_alters_hash()
    {
        var a = RuleHasher.Hash(Make(IsBot(true), new PolicyAction.Block(), 100, PolicyMode.Live));
        var b = RuleHasher.Hash(Make(IsBot(true), new PolicyAction.Allow(), 100, PolicyMode.Live));
        var c = RuleHasher.Hash(Make(IsBot(true), new PolicyAction.RateLimit(60), 100, PolicyMode.Live));
        var d = RuleHasher.Hash(Make(IsBot(true), new PolicyAction.RateLimit(30), 100, PolicyMode.Live));

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(c, d);
    }

    [Fact]
    public void Priority_change_alters_hash()
    {
        var a = RuleHasher.Hash(Make(IsBot(true), new PolicyAction.Block(), 100, PolicyMode.Live));
        var b = RuleHasher.Hash(Make(IsBot(true), new PolicyAction.Block(), 200, PolicyMode.Live));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Mode_change_alters_hash()
    {
        var a = RuleHasher.Hash(Make(IsBot(true), new PolicyAction.Block(), 100, PolicyMode.Live));
        var b = RuleHasher.Hash(Make(IsBot(true), new PolicyAction.Block(), 100, PolicyMode.Observe));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_is_lowercase_hex_of_sha256()
    {
        var r = Make(IsBot(true), new PolicyAction.Block(), 100, PolicyMode.Live);
        var hex = RuleHasher.Hash(r);

        Assert.Equal(64, hex.Length);
        Assert.All(hex, c => Assert.True(char.IsDigit(c) || (c >= 'a' && c <= 'f'),
            $"Hash must be lowercase hex, found '{c}'"));
    }
}

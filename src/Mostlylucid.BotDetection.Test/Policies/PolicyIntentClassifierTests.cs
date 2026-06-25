using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Pred = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies;

public sealed class PolicyIntentClassifierTests
{
    private static readonly Pred NonLockdown =
        new Pred.Term("identity.kind", PredicateOp.Eq, "human");

    private static PolicyRule MakeRule(PolicyAction action, PolicyMode mode, Pred predicate)
        => new(
            Id: Guid.NewGuid(),
            Scope: PolicyScope.Wildcard(),
            Priority: 100,
            Predicate: predicate,
            Action: action,
            Mode: mode,
            Notes: string.Empty,
            Source: "test",
            CreatedAt: DateTimeOffset.UnixEpoch,
            RevisionId: Guid.NewGuid());

    [Fact]
    public void Observe_Mode_Wins_Over_Block_Action()
    {
        var rule = MakeRule(new PolicyAction.Block(), PolicyMode.Observe, NonLockdown);
        Assert.Equal(PolicyIntentKind.Observe, new PolicyIntentClassifier().Classify(rule));
    }

    [Fact]
    public void Lockdown_Predicate_Wins_Over_Block_Action_In_Live_Mode()
    {
        var pred = new Pred.Term("org.lockdown", PredicateOp.Eq, true);
        var rule = MakeRule(new PolicyAction.Block(), PolicyMode.Live, pred);
        Assert.Equal(PolicyIntentKind.Lockdown, new PolicyIntentClassifier().Classify(rule));
    }

    [Fact]
    public void Lockdown_Predicate_Wins_Even_When_Anded()
    {
        var pred = new Pred.And(new Pred[]
        {
            new Pred.Term("org.lockdown", PredicateOp.Eq, true),
            new Pred.Term("ua.is_bot", PredicateOp.Eq, true),
        });
        var rule = MakeRule(new PolicyAction.Block(), PolicyMode.Live, pred);
        Assert.Equal(PolicyIntentKind.Lockdown, new PolicyIntentClassifier().Classify(rule));
    }

    [Fact]
    public void Different_Org_Signal_Does_Not_Trip_Lockdown()
    {
        var pred = new Pred.Term("org.lockdown_imminent", PredicateOp.Eq, true);
        var rule = MakeRule(new PolicyAction.Block(), PolicyMode.Live, pred);
        Assert.Equal(PolicyIntentKind.Block, new PolicyIntentClassifier().Classify(rule));
    }

    [Fact]
    public void Default_Delegates_To_Action_Intent()
    {
        var rule = MakeRule(new PolicyAction.RateLimit(60), PolicyMode.Live, NonLockdown);
        Assert.Equal(PolicyIntentKind.Throttle, new PolicyIntentClassifier().Classify(rule));
    }
}
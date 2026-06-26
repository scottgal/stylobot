using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Atoms;
using Mostlylucid.BotDetection.UI.Options;
using Mostlylucid.BotDetection.UI.Services;
using Pred = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies;

public sealed class PolicyStackPostureClassifierTests
{
    private static readonly Pred NonLockdown =
        new Pred.Term("identity.kind", PredicateOp.Eq, "human");

    private PolicyStackPostureClassifier NewClassifier(PostureClassifierOptions? o = null)
        => new(new PolicyIntentClassifier(), Options.Create(o ?? new PostureClassifierOptions()));

    private static PolicyRule R(PolicyAction action, PolicyMode mode, Pred? predicate = null) => new(
        Id: Guid.NewGuid(),
        Scope: PolicyScope.Wildcard(),
        Priority: 100,
        Predicate: predicate ?? NonLockdown,
        Action: action,
        Mode: mode,
        Notes: string.Empty,
        Source: "test",
        CreatedAt: DateTimeOffset.UnixEpoch,
        RevisionId: Guid.NewGuid());

    private static PolicyStackHitSnapshot EmptySnapshot()
        => new(new Dictionary<PolicyIntentKind, int>());

    [Fact]
    public void Zero_Block_Live_Rules_Is_Permissive()
    {
        var rules = new[] { R(new PolicyAction.Allow(), PolicyMode.Live) };
        Assert.Equal(PostureLevel.Permissive, NewClassifier().Classify(rules, EmptySnapshot()).Level);
    }

    [Fact]
    public void One_Block_Live_Rule_Is_Balanced()
    {
        var rules = new[] { R(new PolicyAction.Block(), PolicyMode.Live) };
        Assert.Equal(PostureLevel.Balanced, NewClassifier().Classify(rules, EmptySnapshot()).Level);
    }

    [Fact]
    public void Three_Block_Live_Rules_Are_Strict()
    {
        var rules = new[]
        {
            R(new PolicyAction.Block(), PolicyMode.Live),
            R(new PolicyAction.Block(), PolicyMode.Live),
            R(new PolicyAction.Block(), PolicyMode.Live),
        };
        Assert.Equal(PostureLevel.Strict, NewClassifier().Classify(rules, EmptySnapshot()).Level);
    }

    [Fact]
    public void Lockdown_Predicate_Beats_Strict_Count()
    {
        var lockdownPred = new Pred.Term("org.lockdown", PredicateOp.Eq, true);
        var rules = new[] { R(new PolicyAction.Block(), PolicyMode.Live, lockdownPred) };
        Assert.Equal(PostureLevel.Lockdown, NewClassifier().Classify(rules, EmptySnapshot()).Level);
    }

    [Fact]
    public void Permissive_With_Observe_Hits_Suggests_Promote()
    {
        var rules = new[] { R(new PolicyAction.Block(), PolicyMode.Observe) };
        var snapshot = new PolicyStackHitSnapshot(new Dictionary<PolicyIntentKind, int>
        {
            { PolicyIntentKind.Observe, 5 },
        });
        var posture = NewClassifier().Classify(rules, snapshot);
        Assert.Equal(PostureLevel.Permissive, posture.Level);
        Assert.NotNull(posture.SuggestedReason);
        Assert.Contains("promote", posture.SuggestedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Permissive_Zero_Activity_Suggests_Lockdown_Template()
    {
        var rules = new[] { R(new PolicyAction.Allow(), PolicyMode.Live) };
        var posture = NewClassifier().Classify(rules, EmptySnapshot());
        Assert.Equal(PostureLevel.Permissive, posture.Level);
        Assert.Equal("lockdown-mode", posture.SuggestedTemplateId);
    }
}
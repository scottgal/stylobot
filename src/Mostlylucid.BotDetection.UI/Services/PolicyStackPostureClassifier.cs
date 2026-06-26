using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Atoms;
using Mostlylucid.BotDetection.UI.Options;

namespace Mostlylucid.BotDetection.UI.Services;

public enum PostureLevel
{
    Permissive,
    Balanced,
    Strict,
    Lockdown,
}

public sealed record PolicyStackPosture(
    PostureLevel Level,
    int BlocksLast24h,
    int ChallengesLast24h,
    int ThrottlesLast24h,
    int AllowsLast24h,
    int ObservesLast24h,
    string? SuggestedTemplateId,
    string? SuggestedReason);

public sealed class PolicyStackPostureClassifier
{
    private readonly PolicyIntentClassifier _intentClassifier;
    private readonly PostureClassifierOptions _options;

    public PolicyStackPostureClassifier(
        PolicyIntentClassifier intentClassifier,
        IOptions<PostureClassifierOptions> options)
    {
        _intentClassifier = intentClassifier;
        _options = options.Value;
    }

    public PolicyStackPosture Classify(IReadOnlyList<PolicyRule> rules, PolicyStackHitSnapshot stats)
    {
        var liveIntents = new List<PolicyIntentKind>();
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i].Mode == PolicyMode.Live)
                liveIntents.Add(_intentClassifier.Classify(rules[i]));
        }

        var blockCount = 0;
        var hasChallenge = false;
        var hasLockdown = false;
        foreach (var intent in liveIntents)
        {
            if (intent == PolicyIntentKind.Lockdown) hasLockdown = true;
            else if (intent == PolicyIntentKind.Block) blockCount++;
            else if (intent == PolicyIntentKind.Challenge) hasChallenge = true;
        }

        PostureLevel level;
        if (hasLockdown) level = PostureLevel.Lockdown;
        else if (blockCount >= _options.StrictThresholdBlockRules) level = PostureLevel.Strict;
        else if (blockCount >= _options.BalancedThresholdBlockRules || hasChallenge) level = PostureLevel.Balanced;
        else level = PostureLevel.Permissive;

        string? suggestedId = null;
        string? suggestedReason = null;
        if (_options.EnableSuggestions)
        {
            if (level == PostureLevel.Permissive
                && stats.Counts.GetValueOrDefault(PolicyIntentKind.Observe) > 0)
            {
                suggestedReason = "You have Observe rules firing -- promote them to Live to start enforcing.";
            }
            else if (level == PostureLevel.Permissive)
            {
                suggestedId = "lockdown-mode";
                suggestedReason = "Apply the Lockdown template during off-hours.";
            }
            // Balanced + diverged-rule suggestion deferred: divergence lives on
            // IRuleDivergenceProbe (queried by rule id), not RuleOrigin, so it
            // can't be derived from the rules list alone. Add a probe-aware arm
            // in a follow-up if/when the suggestion surfaces alongside divergence.
        }

        return new PolicyStackPosture(
            Level: level,
            BlocksLast24h: stats.Counts.GetValueOrDefault(PolicyIntentKind.Block),
            ChallengesLast24h: stats.Counts.GetValueOrDefault(PolicyIntentKind.Challenge),
            ThrottlesLast24h: stats.Counts.GetValueOrDefault(PolicyIntentKind.Throttle),
            AllowsLast24h: stats.Counts.GetValueOrDefault(PolicyIntentKind.Allow),
            ObservesLast24h: stats.Counts.GetValueOrDefault(PolicyIntentKind.Observe),
            SuggestedTemplateId: suggestedId,
            SuggestedReason: suggestedReason);
    }
}

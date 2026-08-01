using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.EndpointPolicies;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     FOSS half of the /dashboard/policies effective view: surfaces the config-baseline enforcement
///     that the hand-rolled <c>SbPolicyStack</c> never showed. Reads <c>BotDetectionOptions</c>
///     (<see cref="BotDetectionOptions.BotTypeActionPolicies"/> + <see cref="BotDetectionOptions.DefaultActionPolicyName"/>),
///     resolves each action-policy name to its <see cref="ActionType"/> via
///     <see cref="IActionPolicyRegistry"/> for a data-driven verdict colour (never a hardcoded name
///     list), and runs every row through <see cref="IEffectivePolicyConfigOverlay"/> so a commercial
///     host can overlay the override value / supersede stamp / edit gate additively.
///
///     This produces only the <see cref="EffectivePolicyRowSource.Config"/> rows; the existing
///     SbPolicyStack keeps rendering the <see cref="EffectivePolicyRowSource.Policy"/> rows unchanged,
///     so the operator sees both layers. Rows sort most-severe-first (Block, then Challenge/Throttle,
///     then the rest) with the global default last.
/// </summary>
public sealed class EffectivePolicyComposer : IConfigBaselineProvider
{
    private readonly IOptions<BotDetectionOptions> _options;
    private readonly IOptions<DetectionPolicyOptions> _detectionPolicies;
    private readonly IActionPolicyRegistry _registry;
    private readonly IEffectivePolicyConfigOverlay _overlay;

    public EffectivePolicyComposer(
        IOptions<BotDetectionOptions> options,
        IOptions<DetectionPolicyOptions> detectionPolicies,
        IActionPolicyRegistry registry,
        IEffectivePolicyConfigOverlay overlay)
    {
        _options = options;
        _detectionPolicies = detectionPolicies;
        _registry = registry;
        _overlay = overlay;
    }

    /// <summary>
    ///     <see cref="IConfigBaselineProvider"/> local implementation: composes in-process from local
    ///     config. Synchronous under the hood; the async signature exists for the remote reader.
    /// </summary>
    public Task<IReadOnlyList<EffectivePolicyRowViewModel>> GetConfigRowsAsync(bool canEdit, CancellationToken ct = default)
        => Task.FromResult(ComposeConfigRows(canEdit));

    /// <summary>
    ///     Compose the config-baseline rows. <paramref name="canEdit"/> is the same
    ///     <c>IPolicyCanEditPolicy.CanEdit</c> flag the policy rows use: FOSS (AlwaysReadOnly) passes
    ///     false, commercial (LicenseAware) passes true.
    /// </summary>
    public IReadOnlyList<EffectivePolicyRowViewModel> ComposeConfigRows(bool canEdit)
    {
        var opts = _options.Value;

        var rows = opts.BotTypeActionPolicies
            .Select(kv =>
            {
                var policy = _registry.GetPolicy(kv.Value);
                var row = _overlay.Apply(new ConfigPolicyRowViewModel(
                    Target: kv.Key,
                    ResultingAction: kv.Value,
                    ActionColorClass: ColorFor(policy?.ActionType),
                    ConfigKey: $"BotDetection:BotTypeActionPolicies:{kv.Key}",
                    Source: EffectivePolicyConfigSource.BotTypeActionPolicies,
                    SupersededByRuleId: null,
                    CanEdit: canEdit,
                    Description: DescriptionFor(policy)));
                return (row, rank: SeverityRank(policy?.ActionType));
            })
            .OrderBy(t => t.rank)
            .ThenBy(t => t.row.Target, StringComparer.OrdinalIgnoreCase)
            .Select(t => EffectivePolicyRowViewModel.ForConfig(t.row))
            .ToList();

        rows.Add(EffectivePolicyRowViewModel.ForConfig(ComposeDefaultRow(opts.DefaultActionPolicyName, canEdit)));
        rows.AddRange(_detectionPolicies.Value.Rules.Select((rule, index) =>
        {
            var policy = _registry.GetPolicy(rule.Action);
            var target = rule.Name ?? $"Detection rule {index + 1}";
            if (!string.IsNullOrWhiteSpace(rule.Path)) target += $" ({rule.Path})";
            return EffectivePolicyRowViewModel.ForConfig(_overlay.Apply(new ConfigPolicyRowViewModel(
                Target: target,
                ResultingAction: rule.Action,
                ActionColorClass: ColorFor(policy?.ActionType),
                ConfigKey: $"BotDetection:DetectionPolicies:Rules:{index}:Action",
                Source: EffectivePolicyConfigSource.DetectionPolicyRule,
                SupersededByRuleId: null,
                CanEdit: canEdit,
                Description: DescriptionFor(policy))));
        }));
        return rows;
    }

    private ConfigPolicyRowViewModel ComposeDefaultRow(string? defaultName, bool canEdit)
    {
        var hasDefault = !string.IsNullOrEmpty(defaultName);
        var policy = hasDefault ? _registry.GetPolicy(defaultName!) : null;
        return _overlay.Apply(new ConfigPolicyRowViewModel(
            Target: "(default)",
            ResultingAction: hasDefault ? defaultName! : "(none)",
            ActionColorClass: ColorFor(policy?.ActionType),
            ConfigKey: "BotDetection:DefaultActionPolicyName",
            Source: EffectivePolicyConfigSource.DefaultActionPolicy,
            SupersededByRuleId: null,
            CanEdit: canEdit,
            Description: DescriptionFor(policy)));
    }

    /// <summary>
    ///     Short plain-language description of what a policy actually does, read from the resolved
    ///     <see cref="IActionPolicy"/> instance's own configured fields -- never from parsing the
    ///     policy's operator-chosen name (<c>feedback_no_word_lists</c>: no name-string switch to
    ///     maintain as new named policy instances get added). Concrete policy classes (matched by
    ///     type, not name) contribute the real configured numbers; anything else -- including test
    ///     doubles and any future <see cref="IActionPolicy"/> implementation -- falls back to a
    ///     generic <see cref="ActionType"/>-level description.
    /// </summary>
    private static string DescriptionFor(IActionPolicy? policy) => policy switch
    {
        null => "Unresolved policy name",
        BlockActionPolicy b => $"{b.Options.StatusCode} immediately",
        RateLimitActionPolicy r => $"Cap request rate to {r.Options.RequestsPerMinute}/min",
        ThrottleActionPolicy t => $"Slows responses {t.Options.BaseDelayMs}-{t.Options.MaxDelayMs}ms",
        ChallengeActionPolicy => "Interactive challenge (CAPTCHA/JS) required",
        LogOnlyActionPolicy => "Observe only, no action taken",
        SilentDropActionPolicy => "Connection dropped, no response sent",
        RedirectActionPolicy rd => $"Redirects to {rd.Options.TargetUrl}",
        _ => policy.ActionType switch
        {
            ActionType.Block => "Denies the request",
            ActionType.Throttle => "Slows the response down",
            ActionType.Challenge => "Requires a challenge to proceed",
            ActionType.Redirect => "Redirects the request",
            ActionType.LogOnly => "Observe only, no action taken",
            ActionType.Escalate => "Escalates for further review",
            _ => "Custom handling"
        }
    };

    // Canonical dashboard verdict palette, dispatched on the policy's ActionType:
    //   error = danger (Block), warning = caution (Challenge / Throttle / Redirect),
    //   info = neutral (LogOnly / Escalate / Custom / unresolved name).
    private static string ColorFor(ActionType? type) => type switch
    {
        ActionType.Block => "verdict-error",
        ActionType.Challenge => "verdict-warning",
        ActionType.Throttle => "verdict-warning",
        ActionType.Redirect => "verdict-warning",
        _ => "verdict-info"
    };

    // Most-severe-first ordering within the config baseline.
    private static int SeverityRank(ActionType? type) => type switch
    {
        ActionType.Block => 0,
        ActionType.Challenge => 1,
        ActionType.Throttle => 2,
        ActionType.Redirect => 3,
        ActionType.Escalate => 4,
        ActionType.LogOnly => 5,
        ActionType.Custom => 6,
        _ => 7
    };
}

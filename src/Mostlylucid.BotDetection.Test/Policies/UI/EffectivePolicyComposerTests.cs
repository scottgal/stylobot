using FluentAssertions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Pins <see cref="EffectivePolicyComposer"/> -- the FOSS half of the effective-policy view.
///     It surfaces the config-baseline enforcement (BotTypeActionPolicies + DefaultActionPolicyName)
///     that /dashboard/policies previously never showed, so a config-default throttle (the operator's
///     "silent throttle": <c>Scraper -> throttle-aggressive</c>) is finally visible with provenance.
///
///     The row colour is derived from the policy's <see cref="ActionType"/> via
///     <see cref="IActionPolicyRegistry"/> (data-driven), never a hardcoded name list. Overlay is the
///     FOSS passthrough here -- rows stay read-only with no override/superseded stamp.
/// </summary>
public sealed class EffectivePolicyComposerTests
{
    private static IOptions<BotDetectionOptions> Options(BotDetectionOptions opts)
        => Microsoft.Extensions.Options.Options.Create(opts);

    private static IActionPolicy Stub(string name, ActionType type)
    {
        var p = new Mock<IActionPolicy>();
        p.SetupGet(x => x.Name).Returns(name);
        p.SetupGet(x => x.ActionType).Returns(type);
        return p.Object;
    }

    /// <summary>A registry that resolves the given names to the given kinds; unknown names return null.</summary>
    private static IActionPolicyRegistry Registry(params (string Name, ActionType Type)[] policies)
    {
        var mock = new Mock<IActionPolicyRegistry>();
        foreach (var (name, type) in policies)
            mock.Setup(r => r.GetPolicy(name)).Returns(Stub(name, type));
        return mock.Object;
    }

    private static EffectivePolicyComposer NewComposer(
        BotDetectionOptions opts, IActionPolicyRegistry registry) =>
        new(Options(opts),
            Microsoft.Extensions.Options.Options.Create(new Mostlylucid.BotDetection.EndpointPolicies.DetectionPolicyOptions()),
            registry,
            new PassthroughEffectivePolicyConfigOverlay());

    [Fact]
    public void Emits_a_row_per_BotTypeActionPolicies_entry_plus_the_global_default()
    {
        var opts = new BotDetectionOptions
        {
            BotTypeActionPolicies = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Scraper"] = "throttle-aggressive",
                ["MaliciousBot"] = "block-hard",
            },
            DefaultActionPolicyName = "throttle-stealth"
        };
        var composer = NewComposer(opts, Registry(
            ("throttle-aggressive", ActionType.Throttle),
            ("block-hard", ActionType.Block),
            ("throttle-stealth", ActionType.Throttle)));

        var rows = composer.ComposeConfigRows(canEdit: false);

        rows.Should().OnlyContain(r => r.Source == EffectivePolicyRowSource.Config && r.ConfigRow != null);
        rows.Select(r => r.ConfigRow!.Source).Should()
            .Contain(EffectivePolicyConfigSource.DefaultActionPolicy, "the global fallback must be visible")
            .And.Contain(EffectivePolicyConfigSource.BotTypeActionPolicies);
        rows.Should().HaveCount(3, "two bot-type rows + one default row");
    }

    [Fact]
    public void Scraper_silent_throttle_row_is_present_keyed_and_colored_from_action_type()
    {
        // The exact operator case: a Scraper throttle that was invisible because it's a config default.
        var opts = new BotDetectionOptions(); // real defaults include Scraper -> throttle-aggressive
        var composer = NewComposer(opts, Registry(("throttle-aggressive", ActionType.Throttle)));

        var scraper = composer.ComposeConfigRows(canEdit: false)
            .Select(r => r.ConfigRow!)
            .Single(c => c.Target == "Scraper");

        scraper.ResultingAction.Should().Be("throttle-aggressive");
        scraper.ActionColorClass.Should().Be("verdict-warning", "Throttle is a caution-class action");
        scraper.ConfigKey.Should().Be("BotDetection:BotTypeActionPolicies:Scraper",
            "the commercial edit control targets this exact IConfiguration key");
        scraper.Source.Should().Be(EffectivePolicyConfigSource.BotTypeActionPolicies);
    }

    [Fact]
    public void Block_actions_color_error_and_sort_ahead_of_throttles()
    {
        var opts = new BotDetectionOptions
        {
            BotTypeActionPolicies = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Scraper"] = "throttle-aggressive",
                ["MaliciousBot"] = "block-hard",
            },
            DefaultActionPolicyName = "throttle-stealth"
        };
        var composer = NewComposer(opts, Registry(
            ("throttle-aggressive", ActionType.Throttle),
            ("block-hard", ActionType.Block),
            ("throttle-stealth", ActionType.Throttle)));

        var rows = composer.ComposeConfigRows(canEdit: false).Select(r => r.ConfigRow!).ToList();

        var malicious = rows.Single(c => c.Target == "MaliciousBot");
        malicious.ActionColorClass.Should().Be("verdict-error", "Block is the danger class");
        rows.FindIndex(c => c.Target == "MaliciousBot").Should()
            .BeLessThan(rows.FindIndex(c => c.Target == "Scraper"), "most severe enforcement sorts first");
        rows[^1].Source.Should().Be(EffectivePolicyConfigSource.DefaultActionPolicy, "the default row sorts last");
    }

    [Fact]
    public void Unknown_policy_name_falls_back_to_neutral_info_not_a_crash()
    {
        var opts = new BotDetectionOptions
        {
            BotTypeActionPolicies = new(StringComparer.OrdinalIgnoreCase) { ["Scraper"] = "made-up-policy" },
            DefaultActionPolicyName = null
        };
        var composer = NewComposer(opts, Registry()); // registry resolves nothing

        var rows = composer.ComposeConfigRows(canEdit: false).Select(r => r.ConfigRow!).ToList();

        rows.Single(c => c.Target == "Scraper").ActionColorClass.Should().Be("verdict-info");
        rows.Single(c => c.Source == EffectivePolicyConfigSource.DefaultActionPolicy)
            .ResultingAction.Should().Be("(none)", "a null DefaultActionPolicyName renders honestly, not as a crash");
    }

    [Fact]
    public void Passthrough_overlay_keeps_rows_readonly_and_not_superseded()
    {
        var opts = new BotDetectionOptions
        {
            BotTypeActionPolicies = new(StringComparer.OrdinalIgnoreCase) { ["Scraper"] = "throttle-aggressive" }
        };
        var composer = NewComposer(opts, Registry(("throttle-aggressive", ActionType.Throttle)));

        var rows = composer.ComposeConfigRows(canEdit: false).Select(r => r.ConfigRow!).ToList();

        rows.Should().OnlyContain(c => c.CanEdit == false, "FOSS AlwaysReadOnly => no edit affordance");
        rows.Should().OnlyContain(c => c.SupersededByRuleId == null,
            "the FOSS passthrough overlay never stamps supersede -- that is the commercial resolver's job");
    }

    [Fact]
    public void CanEdit_true_threads_into_every_config_row()
    {
        var opts = new BotDetectionOptions
        {
            BotTypeActionPolicies = new(StringComparer.OrdinalIgnoreCase) { ["Scraper"] = "throttle-aggressive" }
        };
        var composer = NewComposer(opts, Registry(("throttle-aggressive", ActionType.Throttle)));

        var rows = composer.ComposeConfigRows(canEdit: true).Select(r => r.ConfigRow!).ToList();

        rows.Should().OnlyContain(c => c.CanEdit, "canEdit=true (commercial LicenseAware) enables the config-row edit control");
    }

    [Fact]
    public void Description_is_read_from_the_real_policys_own_configured_fields_not_the_name()
    {
        // Real concrete policy instances (not mocks) so the type-pattern-matched
        // description path actually runs -- proves the numbers come from the
        // policy's own Options, never from parsing "block-hard"/"throttle-stealth".
        var block = new BlockActionPolicy("block-hard", new BlockActionOptions { StatusCode = 403 });
        var throttle = new ThrottleActionPolicy("throttle-stealth", new ThrottleActionOptions { BaseDelayMs = 800, MaxDelayMs = 3000 });

        var mock = new Mock<IActionPolicyRegistry>();
        mock.Setup(r => r.GetPolicy("block-hard")).Returns(block);
        mock.Setup(r => r.GetPolicy("throttle-stealth")).Returns(throttle);

        var opts = new BotDetectionOptions
        {
            BotTypeActionPolicies = new(StringComparer.OrdinalIgnoreCase)
            {
                ["MaliciousBot"] = "block-hard",
                ["Scraper"] = "throttle-stealth",
            },
            DefaultActionPolicyName = null
        };
        var composer = NewComposer(opts, mock.Object);

        var rows = composer.ComposeConfigRows(canEdit: false).Select(r => r.ConfigRow!).ToList();

        rows.Single(c => c.Target == "MaliciousBot").Description.Should().Be("403 immediately");
        rows.Single(c => c.Target == "Scraper").Description.Should().Be("Slows responses 800-3000ms");
    }

    [Fact]
    public void Unresolved_policy_name_gets_an_honest_description_not_a_crash()
    {
        var opts = new BotDetectionOptions
        {
            BotTypeActionPolicies = new(StringComparer.OrdinalIgnoreCase) { ["Scraper"] = "made-up-policy" },
            DefaultActionPolicyName = null
        };
        var composer = NewComposer(opts, Registry());

        var rows = composer.ComposeConfigRows(canEdit: false).Select(r => r.ConfigRow!).ToList();

        rows.Single(c => c.Target == "Scraper").Description.Should().Be("Unresolved policy name");
    }
}

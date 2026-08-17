using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Locks the field-length contract on built-in action-policy names. Regression for a prod
///     data-loss incident (2026-08-17): two built-in policy names ("rate-limit-monitoring" at 21
///     chars, "extract-markdown-cache-ai" at 25) overflowed a downstream fixed-length field.
///     FOSS cannot see every consumer's schema, so this is the durable, FOSS-side half of the
///     fix: new built-in names are held to the contract; the two existing over-length names stay
///     registered forever (a customer may already have copied one into their own config) but get
///     a short alias that resolves to the SAME policy instance, and FOSS's own shipped defaults
///     have moved to the alias.
/// </summary>
public class ActionPolicyRegistryNameLengthTests
{
    private static ActionPolicyRegistry NewRegistry() => new(
        Options.Create(new BotDetectionOptions()),
        Array.Empty<IActionPolicyFactory>());

    [Fact]
    public void Every_built_in_policy_name_fits_the_contract_or_is_an_explicit_legacy_exception()
    {
        var registry = NewRegistry();
        var overLength = registry.GetAllPolicies().Keys
            .Where(name => name.Length > ActionPolicyRegistry.MaxBuiltInPolicyNameLength)
            .Where(name => !ActionPolicyRegistry.LegacyOverLengthPolicyNames.Contains(name))
            .ToList();

        Assert.True(overLength.Count == 0,
            $"New built-in polic{(overLength.Count == 1 ? "y" : "ies")} over " +
            $"{ActionPolicyRegistry.MaxBuiltInPolicyNameLength} chars, not in the documented " +
            $"exception list: {string.Join(", ", overLength)}. Shorten the name (or alias it) " +
            "rather than adding to LegacyOverLengthPolicyNames.");
    }

    [Fact]
    public void Rate_limit_monitor_alias_resolves_to_the_same_instance_as_the_legacy_name()
    {
        var registry = NewRegistry();

        var canonical = registry.GetPolicy("rate-limit-monitoring");
        var alias = registry.GetPolicy("rate-limit-monitor");

        Assert.NotNull(canonical);
        Assert.Same(canonical, alias);
    }

    [Fact]
    public void Legacy_over_length_names_are_still_resolvable()
    {
        // The whole point of aliasing instead of renaming: a customer's existing config
        // referencing the long name must keep working forever.
        var registry = NewRegistry();

        Assert.NotNull(registry.GetPolicy("rate-limit-monitoring"));
    }
}

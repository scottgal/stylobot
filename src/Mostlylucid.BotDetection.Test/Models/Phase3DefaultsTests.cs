using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Models;

/// <summary>
///     Pin the phase-3 user-facing defaults (full BotType coverage,
///     DefaultActionPolicyName, ObserveOnly opt-in). A future refactor that
///     accidentally reverts the 6.8 posture trips these tests instead of
///     silently flipping the product back to "detect but do nothing".
/// </summary>
public class Phase3DefaultsTests
{
    [Fact]
    public void DefaultActionPolicyName_IsThrottleStealth()
    {
        var options = new BotDetectionOptions();
        // Visible bots that escape per-type routing get silently slowed,
        // not hard-blocked. Operators wanting strict default-deny set
        // DefaultActionPolicyName = "block" in config.
        Assert.Equal("throttle-stealth", options.DefaultActionPolicyName);
    }

    [Fact]
    public void ObserveOnly_DefaultsOff()
    {
        var options = new BotDetectionOptions();
        Assert.False(options.ObserveOnly);
    }

    [Theory]
    [InlineData("MaliciousBot",   "block-hard")]
    [InlineData("ExploitScanner", "block-hard")]
    [InlineData("ClickFraud",     "block-hard")]
    [InlineData("Tool",           "throttle-tools")]
    [InlineData("Scraper",        "throttle-aggressive")]
    [InlineData("AiBot",          "rate-limit-ai")]
    [InlineData("SearchEngine",   "rate-limit-search")]
    [InlineData("GoodBot",        "rate-limit-search")]
    [InlineData("VerifiedBot",    "rate-limit-search")]
    [InlineData("SocialMediaBot", "rate-limit-social")]
    [InlineData("MonitoringBot",  "rate-limit-monitoring")]
    public void BotTypeActionPolicies_DefaultCoversEveryType(string botType, string expectedPolicy)
    {
        var options = new BotDetectionOptions();

        Assert.True(options.BotTypeActionPolicies.TryGetValue(botType, out var actual),
            $"BotType '{botType}' should have a default policy mapping in 6.8+");
        Assert.Equal(expectedPolicy, actual);
    }

    [Fact]
    public void BotTypeActionPolicies_UnknownIsIntentionallyOmitted()
    {
        // BotType.Unknown means "we couldn't classify" -- routing it through
        // a specific policy is the wrong call. The orchestrator falls through
        // to DefaultActionPolicyName for Unknown.
        var options = new BotDetectionOptions();
        Assert.False(options.BotTypeActionPolicies.ContainsKey("Unknown"));
    }

    [Fact]
    public void BotTypeActionPolicies_NamesAreRegisteredBuiltIns()
    {
        // Every value in the default map must be a built-in policy registered
        // by ActionPolicyRegistry.RegisterBuiltInPolicies. A typo or rename
        // would silently fall through to DefaultActionPolicyName at runtime;
        // catch it at construction time instead.
        var options = new BotDetectionOptions();
        var registry = new Mostlylucid.BotDetection.Actions.ActionPolicyRegistry(
            Microsoft.Extensions.Options.Options.Create(options),
            Array.Empty<Mostlylucid.BotDetection.Actions.IActionPolicyFactory>());

        foreach (var (botType, policyName) in options.BotTypeActionPolicies)
        {
            Assert.NotNull(registry.GetPolicy(policyName));
        }
    }
}

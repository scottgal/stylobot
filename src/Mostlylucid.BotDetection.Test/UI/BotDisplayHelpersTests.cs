using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Locks in behaviour for the helpers extracted out of SbTopBots/Default.cshtml
///     and _RecentActivity.cshtml in commit ac60cf0. These previously lived inside
///     Razor @functions blocks where they could not be exercised from any test
///     harness; the original extraction had to be verified visually on staging.
///     New cases here are cheap insurance against the next prefix-rename in
///     ContributingDetectors silently breaking the operator-facing labels.
/// </summary>
public class BotDisplayHelpersTests
{
    [Theory]
    [InlineData(0.95, "Bot")]
    [InlineData(0.85, "Scanner")]
    [InlineData(0.70, "Crawler")]
    [InlineData(0.10, "Probe")]
    public void DeterministicIntent_returns_band_for_probability(double prob, string expected)
    {
        Assert.Equal(expected, BotDisplayHelpers.DeterministicIntent(prob));
    }

    [Fact]
    public void ResolvedThreat_honours_upstream_band_when_present()
    {
        Assert.Equal("VeryHigh", BotDisplayHelpers.ResolvedThreat("VeryHigh", 0.10, isBot: true));
    }

    [Fact]
    public void ResolvedThreat_returns_empty_for_non_bot()
    {
        Assert.Equal("", BotDisplayHelpers.ResolvedThreat(null, 0.99, isBot: false));
    }

    [Fact]
    public void ResolvedThreat_treats_None_band_as_None_not_probability_derived()
    {
        // A 98%-bot Googlebot with no upstream threat band should NOT read as
        // "High threat" -- bot probability and threat are orthogonal axes, and
        // deriving threat from probability was the source of the
        // "100% Bot · THREAT 0.0" UX contradiction the dashboard used to show.
        Assert.Equal("None", BotDisplayHelpers.ResolvedThreat("None", 0.98, isBot: true));
        Assert.Equal("None", BotDisplayHelpers.ResolvedThreat(null, 0.98, isBot: true));
        Assert.Equal("None", BotDisplayHelpers.ResolvedThreat("", 0.98, isBot: true));
    }

    [Theory]
    [InlineData("Critical", true,  "Hostile")]
    [InlineData("High",     true,  "Hostile")]
    [InlineData("Elevated", true,  "Suspicious")]
    [InlineData("Medium",   true,  "Suspicious")]
    [InlineData("Low",      true,  "Benign")]
    [InlineData("None",     true,  "Benign")]
    [InlineData(null,       true,  "Benign")]
    [InlineData("High",     false, "")]
    public void IntentLabel_maps_band_to_categorical(string? band, bool isBot, string expected)
    {
        Assert.Equal(expected, BotDisplayHelpers.IntentLabel(band, isBot));
    }

    [Theory]
    [InlineData("/.env",                   "ENV Scanner")]
    [InlineData("/wp-config.php",          "Config Scanner")]
    [InlineData("/wp-admin/setup-config",  "Admin Scanner")]
    [InlineData("/.git/HEAD",              "Backup Scanner")]
    [InlineData("/api/v1/users",           "API Scanner")]
    [InlineData("/wp-login.php",           "Auth Scanner")]
    [InlineData("/random.php",             "App Scanner")]
    [InlineData("/something",              "Path Scanner")]
    public void CategorizePath_classifies_known_targets(string path, string expected)
    {
        Assert.Equal(expected, BotDisplayHelpers.CategorizePath(path));
    }

    [Theory]
    [InlineData("CN", "Chinese")]
    [InlineData("us", "US")]
    [InlineData("ZZ", "ZZ")] // unknown -> raw code
    public void CountryAdjective_maps_or_falls_through(string code, string expected)
    {
        Assert.Equal(expected, BotDisplayHelpers.CountryAdjective(code));
    }

    [Fact]
    public void DescriptiveBotName_pins_credential_attack_above_path_classification()
    {
        var bot = MakeBot(
            reasons: ["Severe login brute-forcing: 47 failed login attempts"],
            lastPath: "/.env",
            countryCode: "CN");
        Assert.Equal("Chinese Credential Attack", BotDisplayHelpers.DescriptiveBotName(bot));
    }

    [Fact]
    public void DescriptiveBotName_prepends_burst_modifier_to_path_activity()
    {
        var bot = MakeBot(
            reasons: ["Burst detected: 30 requests in 5 seconds"],
            lastPath: "/.env",
            countryCode: "");
        Assert.Equal("Rapid ENV Scanner", BotDisplayHelpers.DescriptiveBotName(bot));
    }

    [Fact]
    public void DescriptiveBotName_falls_back_to_riskband_label_when_no_signals()
    {
        var bot = MakeBot(reasons: [], lastPath: "", countryCode: "", riskBand: "High");
        Assert.Equal("Scanner", BotDisplayHelpers.DescriptiveBotName(bot));
    }

    [Fact]
    public void DescriptiveBotName_treats_low_probability_visitor_as_Visitor_not_Suspicious()
    {
        // Regression guard for the "1%-probability operator was labelled 'Suspicious Client'"
        // bug the extraction was originally fixing.
        var bot = MakeBot(reasons: [], lastPath: "", countryCode: "", botProbability: 0.01);
        Assert.Equal("Visitor", BotDisplayHelpers.DescriptiveBotName(bot));
    }

    [Fact]
    public void DescriptiveBotName_skips_country_for_XX_placeholder()
    {
        var bot = MakeBot(reasons: [], lastPath: "/.env", countryCode: "XX");
        Assert.Equal("ENV Scanner", BotDisplayHelpers.DescriptiveBotName(bot));
    }

    private static DashboardTopBotEntry MakeBot(
        List<string> reasons,
        string lastPath = "",
        string countryCode = "",
        string? riskBand = null,
        double botProbability = 0.85)
        => new()
        {
            PrimarySignature = "test",
            BotProbability = botProbability,
            TopReasons = reasons,
            LastPath = lastPath,
            CountryCode = countryCode,
            RiskBand = riskBand
        };
}

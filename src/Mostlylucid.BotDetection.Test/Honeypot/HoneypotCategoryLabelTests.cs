using Mostlylucid.BotDetection.Honeypot;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Honeypot;

/// <summary>
///     Pins the operator-facing label table that the Honeypot tab renders
///     for each <see cref="HoneypotCategory"/>. Labels appear in screenshots
///     and runbooks; reverts to a free-form string get caught here.
/// </summary>
public class HoneypotCategoryLabelTests
{
    [Theory]
    [InlineData(HoneypotCategory.Credentials,    "credentials theft")]
    [InlineData(HoneypotCategory.Config,         "config file leak")]
    [InlineData(HoneypotCategory.VersionControl, "version-control exposure")]
    [InlineData(HoneypotCategory.Database,       "database admin probe")]
    [InlineData(HoneypotCategory.Webshell,       "webshell upload")]
    [InlineData(HoneypotCategory.Admin,          "admin-panel probe")]
    [InlineData(HoneypotCategory.Debug,          "debug-endpoint probe")]
    [InlineData(HoneypotCategory.Backup,         "database/backup dump")]
    [InlineData(HoneypotCategory.Metadata,       "metadata SSRF probe")]
    [InlineData(HoneypotCategory.PathTraversal,  "path-traversal probe")]
    [InlineData(HoneypotCategory.BuildArtifact,  "build-artifact probe")]
    [InlineData(HoneypotCategory.ApiDoc,         "api-doc enumeration")]
    [InlineData(HoneypotCategory.Cgi,            "CGI probe")]
    [InlineData(HoneypotCategory.Cms,            "CMS probe")]
    public void EveryCategory_HasStableLabel(HoneypotCategory category, string expected)
    {
        var label = SqliteDashboardEventStore.LabelForCategory(category, HoneypotTier.Always);
        Assert.Equal(expected, label);
    }

    [Fact]
    public void NoneCategory_FallsBackToTierLabel()
    {
        Assert.Equal(
            "always-honeypot",
            SqliteDashboardEventStore.LabelForCategory(HoneypotCategory.None, HoneypotTier.Always));
        Assert.Equal(
            "probable scanner",
            SqliteDashboardEventStore.LabelForCategory(HoneypotCategory.None, HoneypotTier.Probable));
    }

    [Fact]
    public void EveryEnumValue_HasANonEmptyLabel()
    {
        // Any new HoneypotCategory added without a matching label entry
        // falls through to the tier default. That's fine for Probable, but
        // a Tier-1 path with no specific label is a regression signal --
        // catch it here.
        foreach (HoneypotCategory cat in Enum.GetValues(typeof(HoneypotCategory)))
        {
            if (cat == HoneypotCategory.None) continue;
            var label = SqliteDashboardEventStore.LabelForCategory(cat, HoneypotTier.Always);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual("always-honeypot", label);
        }
    }
}

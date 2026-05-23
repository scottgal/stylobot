using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Test.SiteProfiles;

public class SiteProfileCatalogTests
{
    private readonly SiteProfileCatalog _catalog =
        new(NullLogger<SiteProfileCatalog>.Instance);

    [Fact]
    public void LoadsEmbeddedProfiles()
    {
        Assert.NotEmpty(_catalog.Profiles);
        Assert.True(_catalog.Profiles.ContainsKey("generic"));
        Assert.True(_catalog.Profiles.ContainsKey("wordpress"));
        Assert.True(_catalog.Profiles.ContainsKey("aspnet"));
    }

    [Fact]
    public void WordpressProfile_HasFrameworkPathsForRealEndpoints()
    {
        var wp = _catalog.Get("wordpress");
        Assert.NotNull(wp);
        Assert.Contains("/wp-login.php", wp!.Honeypot.FrameworkPaths);
        Assert.Contains("/wp-admin*", wp.Honeypot.FrameworkPaths);
        Assert.Contains("/xmlrpc.php", wp.Honeypot.FrameworkPaths);
    }

    [Fact]
    public void WordpressProfile_HasTier1ConfigBackups()
    {
        var wp = _catalog.Get("wordpress");
        Assert.NotNull(wp);
        Assert.Contains("/wp-config.php.bak", wp!.Honeypot.AdditionalTier1);
        Assert.Contains("/wp-content/debug.log", wp.Honeypot.AdditionalTier1);
    }

    [Fact]
    public void WordpressProfile_HasSuggestedPacks()
    {
        var wp = _catalog.Get("wordpress");
        Assert.NotNull(wp);
        Assert.NotEmpty(wp!.SuggestedPacks);
        Assert.Contains(wp.SuggestedPacks, p => p.Id == "wordpress" && p.Tier == "foss");
    }

    [Fact]
    public void GenericProfile_IsEmptyFallback()
    {
        var generic = _catalog.Get("generic");
        Assert.NotNull(generic);
        Assert.Empty(generic!.Honeypot.FrameworkPaths);
        Assert.Empty(generic.Honeypot.AdditionalTier1);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        Assert.Null(_catalog.Get("nonexistent"));
    }
}

using Mostlylucid.BotDetection.Honeypot;

namespace Mostlylucid.BotDetection.Test.Honeypot;

public class HoneypotCategoryTests
{
    [Theory]
    [InlineData("/.aws/credentials", HoneypotCategory.Credentials)]
    [InlineData("/.ssh/id_rsa", HoneypotCategory.Credentials)]
    [InlineData("/.kube/config", HoneypotCategory.Credentials)]
    [InlineData("/credentials.json", HoneypotCategory.Credentials)]
    [InlineData("/id_rsa", HoneypotCategory.Credentials)]
    [InlineData("/.env", HoneypotCategory.Config)]
    [InlineData("/.env.local.save", HoneypotCategory.Config)]
    [InlineData("/wp-config.php.bak", HoneypotCategory.Config)]
    [InlineData("/.git/config", HoneypotCategory.VersionControl)]
    [InlineData("/.git/HEAD", HoneypotCategory.VersionControl)]
    [InlineData("/.svn/entries", HoneypotCategory.VersionControl)]
    [InlineData("/c99.php", HoneypotCategory.Webshell)]
    [InlineData("/backdoor.php", HoneypotCategory.Webshell)]
    [InlineData("/backup.sql", HoneypotCategory.Backup)]
    [InlineData("/site.tar.gz", HoneypotCategory.Backup)]
    [InlineData("/etc/passwd", HoneypotCategory.PathTraversal)]
    [InlineData("/proc/self/environ", HoneypotCategory.PathTraversal)]
    [InlineData("/latest/meta-data", HoneypotCategory.Metadata)]
    [InlineData("/computeMetadata/v1", HoneypotCategory.Metadata)]
    public void Tier1Paths_HaveExpectedCategory(string path, HoneypotCategory expected)
    {
        var result = HoneypotPathDefinitions.ClassifyDetailed(path.ToLowerInvariant());

        Assert.Equal(HoneypotTier.Always, result.Tier);
        Assert.Equal(expected, result.Category);
    }

    [Theory]
    [InlineData("/wp-login.php", HoneypotCategory.Admin)]
    [InlineData("/wp-admin", HoneypotCategory.Admin)]
    [InlineData("/grafana", HoneypotCategory.Admin)]
    [InlineData("/actuator", HoneypotCategory.Admin)]
    [InlineData("/phpmyadmin", HoneypotCategory.Database)]
    [InlineData("/adminer.php", HoneypotCategory.Database)]
    [InlineData("/swagger.json", HoneypotCategory.ApiDoc)]
    [InlineData("/v3/api-docs", HoneypotCategory.ApiDoc)]
    [InlineData("/web.config", HoneypotCategory.Config)]
    [InlineData("/appsettings.json", HoneypotCategory.Config)]
    [InlineData("/composer.lock", HoneypotCategory.BuildArtifact)]
    [InlineData("/.idea", HoneypotCategory.BuildArtifact)]
    [InlineData("/.DS_Store", HoneypotCategory.BuildArtifact)]
    [InlineData("/elmah.axd", HoneypotCategory.Debug)]
    [InlineData("/phpinfo.php", HoneypotCategory.Debug)]
    [InlineData("/cgi-bin", HoneypotCategory.Cgi)]
    [InlineData("/sites/default/files", HoneypotCategory.Cms)]
    public void Tier2Paths_HaveExpectedCategory(string path, HoneypotCategory expected)
    {
        var result = HoneypotPathDefinitions.ClassifyDetailed(path.ToLowerInvariant());

        Assert.Equal(HoneypotTier.Probable, result.Tier);
        Assert.Equal(expected, result.Category);
    }

    [Theory]
    [InlineData("/anything.sql", HoneypotCategory.Database)]
    [InlineData("/dump.tar.gz", HoneypotCategory.Backup)]
    [InlineData("/file.bak", HoneypotCategory.Backup)]
    [InlineData("/keys/server.key", HoneypotCategory.Credentials)]
    [InlineData("/cert.pem", HoneypotCategory.Credentials)]
    [InlineData("/data.sqlite", HoneypotCategory.Database)]
    [InlineData("/whatever.ini", HoneypotCategory.Config)]
    [InlineData("/app.log", HoneypotCategory.Backup)]
    public void SuspiciousExtension_MapsToCategory(string path, HoneypotCategory expected)
    {
        var result = HoneypotPathDefinitions.ClassifyDetailed(path.ToLowerInvariant());

        Assert.Equal(HoneypotTier.Probable, result.Tier);
        Assert.Equal(expected, result.Category);
    }

    [Fact]
    public void NoMatch_ReturnsNoneCategoryAndNoneTier()
    {
        var result = HoneypotPathDefinitions.ClassifyDetailed("/products/123");

        Assert.Equal(HoneypotTier.None, result.Tier);
        Assert.Equal(HoneypotCategory.None, result.Category);
        Assert.Null(result.Pattern);
    }

    [Fact]
    public void GetPathsByCategory_ReturnsEntriesForThatCategory()
    {
        var creds = HoneypotPathDefinitions.GetPathsByCategory(HoneypotCategory.Credentials);

        Assert.Contains("/.aws/credentials", (IReadOnlySet<string>)creds);
        Assert.Contains("/.ssh/id_rsa", (IReadOnlySet<string>)creds);
        Assert.Contains("/credentials.json", (IReadOnlySet<string>)creds);
        Assert.DoesNotContain("/wp-login.php", (IReadOnlySet<string>)creds);
    }

    [Fact]
    public void GetPathsByCategory_UnknownCategory_ReturnsEmpty()
    {
        // None is the sentinel; no paths are tagged with it.
        var none = HoneypotPathDefinitions.GetPathsByCategory(HoneypotCategory.None);
        Assert.Empty(none);
    }

    [Fact]
    public void GetAllPaths_UnionAcrossTiers_HasNoDuplicates()
    {
        var all = HoneypotPathDefinitions.GetAllPaths();
        var distinct = all.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        Assert.Equal(distinct, all.Count);
        Assert.True(all.Count > 100);  // we have well over a hundred entries
    }

    [Fact]
    public void CategoryForPattern_KnownEntry_ReturnsCategory()
    {
        Assert.Equal(
            HoneypotCategory.Credentials,
            HoneypotPathDefinitions.CategoryForPattern("/.aws/credentials"));
        Assert.Equal(
            HoneypotCategory.Config,
            HoneypotPathDefinitions.CategoryForPattern("*/.env*"));
    }

    [Fact]
    public void CategoryForPattern_UnknownPattern_ReturnsNone()
    {
        Assert.Equal(
            HoneypotCategory.None,
            HoneypotPathDefinitions.CategoryForPattern("/never-in-catalog"));
    }

    [Fact]
    public void EveryCatalogEntry_HasANonNoneCategory()
    {
        // Sanity check: a future contributor who adds a path but forgets
        // to give it a category should fail this test.
        foreach (var path in HoneypotPathDefinitions.GetAllPaths())
        {
            var cat = HoneypotPathDefinitions.CategoryForPattern(path);
            Assert.NotEqual(HoneypotCategory.None, cat);
        }
    }

    [Fact]
    public void BackCompat_ClassifyStillReturnsBareTier()
    {
        // The old single-out overload remains; existing consumers compile.
        var tier = HoneypotPathDefinitions.Classify("/.aws/credentials", out var pattern);

        Assert.Equal(HoneypotTier.Always, tier);
        Assert.Equal("/.aws/credentials", pattern);
    }
}

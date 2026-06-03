using Mostlylucid.BotDetection.Honeypot;

namespace Mostlylucid.BotDetection.Test.Honeypot;

public class HoneypotPathDefinitionsTests
{
    [Theory]
    [InlineData("/.aws/credentials")]
    [InlineData("/.aws/config")]
    [InlineData("/.ssh/id_rsa")]
    [InlineData("/.ssh/id_ed25519")]
    [InlineData("/.ssh/authorized_keys")]
    [InlineData("/.kube/config")]
    [InlineData("/.docker/config.json")]
    [InlineData("/etc/passwd")]
    [InlineData("/proc/self/environ")]
    [InlineData("/computeMetadata/v1")]
    [InlineData("/backup.sql")]
    [InlineData("/dump.sql")]
    [InlineData("/site.tar.gz")]
    [InlineData("/wp-config.php.bak")]
    [InlineData("/wp-config.php.save")]
    [InlineData("/c99.php")]
    [InlineData("/credentials.json")]
    public void Tier1_KnownPaths_ClassifiedAsAlways(string path)
    {
        var tier = HoneypotPathDefinitions.Classify(path.ToLowerInvariant(), out var matched);

        Assert.Equal(HoneypotTier.Always, tier);
        Assert.NotNull(matched);
    }

    [Theory]
    [InlineData("/.env")]
    [InlineData("/.env.local")]
    [InlineData("/.env.production")]
    [InlineData("/.env.local.save")]            // the exact bug the user reported
    [InlineData("/.env.production.bak")]
    [InlineData("/.env.staging.old")]
    [InlineData("/.env.development.swp")]
    public void Tier1_EnvFamily_GlobMatchesAnySuffix(string path)
    {
        var tier = HoneypotPathDefinitions.Classify(path.ToLowerInvariant(), out var matched);

        Assert.Equal(HoneypotTier.Always, tier);
        Assert.Equal("*/.env*", matched);
    }

    [Theory]
    [InlineData("/app/.env")]
    [InlineData("/backend/.env")]
    [InlineData("/laravel/.env")]
    [InlineData("/admin/.env.production.bak")]
    [InlineData("/config/.env.local")]
    public void Tier1_EnvFamily_SubdirVariantsMatch(string path)
    {
        // Bug A regression: live env-scanner sig 9z3avO7sKTd7NAYY896Yog
        // hit /app/.env and /backend/.env. The pre-fix catalog pattern
        // /.env* only matched root-level paths.
        var tier = HoneypotPathDefinitions.Classify(path.ToLowerInvariant(), out var matched);

        Assert.Equal(HoneypotTier.Always, tier);
        Assert.Equal("*/.env*", matched);
    }

    [Theory]
    [InlineData("/file.envrc")]    // contains ".env" but no "/.env" substring
    [InlineData("/foo.env.bar")]   // same: must not false-match
    [InlineData("/uploads/file.envtest")]
    public void EnvLookalike_PathsDoNotMatch(string path)
    {
        // Substring matcher must require a leading "/" before ".env" --
        // arbitrary filenames containing ".env" should NOT trip the honeypot.
        var tier = HoneypotPathDefinitions.Classify(path.ToLowerInvariant(), out _);

        Assert.Equal(HoneypotTier.None, tier);
    }

    [Theory]
    [InlineData("/wp-login.php")]
    [InlineData("/wp-admin")]
    [InlineData("/xmlrpc.php")]
    [InlineData("/phpmyadmin")]
    [InlineData("/actuator")]
    [InlineData("/swagger.json")]
    [InlineData("/v3/api-docs")]
    public void Tier2_KnownPaths_ClassifiedAsProbable(string path)
    {
        var tier = HoneypotPathDefinitions.Classify(path.ToLowerInvariant(), out var matched);

        Assert.Equal(HoneypotTier.Probable, tier);
        Assert.NotNull(matched);
    }

    [Theory]
    [InlineData("/wp-admin/users.php")]
    [InlineData("/wp-content/uploads/2024/file.jpg")]
    [InlineData("/actuator/health")]
    [InlineData("/actuator/env")]
    [InlineData("/phpmyadmin/index.php")]
    [InlineData("/pma/sql.php")]
    public void Tier2_GlobAndSegmentPrefix_MatchesDescendants(string path)
    {
        var tier = HoneypotPathDefinitions.Classify(path.ToLowerInvariant(), out _);
        Assert.Equal(HoneypotTier.Probable, tier);
    }

    [Theory]
    [InlineData("/products/123")]
    [InlineData("/api/users/me")]
    [InlineData("/robots.txt")]               // intentionally NOT a honeypot
    [InlineData("/sitemap.xml")]              // ditto
    [InlineData("/index.html")]
    [InlineData("/wp-administrator-tools")]   // segment-boundary check: NOT /wp-admin
    public void NormalPaths_DoNotMatch(string path)
    {
        var tier = HoneypotPathDefinitions.Classify(path.ToLowerInvariant(), out _);
        Assert.Equal(HoneypotTier.None, tier);
    }

    [Theory]
    [InlineData("/anything.sql")]
    [InlineData("/dump.tar.gz")]
    [InlineData("/reports/q4.bak")]
    [InlineData("/files/private.pem")]
    [InlineData("/keys/server.key")]
    [InlineData("/data.sqlite")]
    public void SuspiciousExtension_FallbackMatchesAsTier2(string path)
    {
        var tier = HoneypotPathDefinitions.Classify(path.ToLowerInvariant(), out var matched);

        Assert.Equal(HoneypotTier.Probable, tier);
        Assert.NotNull(matched);
        Assert.StartsWith("*.", matched);
    }

    [Fact]
    public void NormalizePath_DoubleEncoding_Resolved()
    {
        var normalized = HoneypotPathDefinitions.NormalizePath("/%252e%252e/secret");

        Assert.Equal("/secret", normalized);
    }

    [Fact]
    public void NormalizePath_BackslashSeparator_NormalisedToForwardSlash()
    {
        var normalized = HoneypotPathDefinitions.NormalizePath("/.aws\\credentials");

        Assert.Equal("/.aws/credentials", normalized);
    }

    [Fact]
    public void NormalizePath_DotDot_Collapsed()
    {
        var normalized = HoneypotPathDefinitions.NormalizePath("/api/../.aws/credentials");

        Assert.Equal("/.aws/credentials", normalized);
    }

    [Fact]
    public void NormalizePath_RepeatedSlashes_Collapsed()
    {
        var normalized = HoneypotPathDefinitions.NormalizePath("//.aws///credentials");

        Assert.Equal("/.aws/credentials", normalized);
    }

    [Fact]
    public void NormalizePath_LowercasesPath()
    {
        var normalized = HoneypotPathDefinitions.NormalizePath("/WP-LOGIN.PHP");

        Assert.Equal("/wp-login.php", normalized);
    }

    [Fact]
    public void NormalizedThenClassified_AwsCredentialsHitsTier1()
    {
        var normalized = HoneypotPathDefinitions.NormalizePath("/.aws/credentials");
        var tier = HoneypotPathDefinitions.Classify(normalized, out _);

        Assert.Equal(HoneypotTier.Always, tier);
    }

    [Fact]
    public void NormalizedThenClassified_EnvLocalSaveHitsTier1()
    {
        var normalized = HoneypotPathDefinitions.NormalizePath("/.env.local.save");
        var tier = HoneypotPathDefinitions.Classify(normalized, out var matched);

        Assert.Equal(HoneypotTier.Always, tier);
        Assert.Equal("*/.env*", matched);
    }
}

using Mostlylucid.BotDetection.ThreatIntel;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

public class EndpointRiskClassifierTests
{
    [Theory]
    [InlineData("/admin", EndpointRisk.Sensitive)]
    [InlineData("/admin/", EndpointRisk.Sensitive)]
    [InlineData("/admin/login", EndpointRisk.Sensitive)]
    [InlineData("/administrator/", EndpointRisk.Sensitive)]
    [InlineData("/login", EndpointRisk.Sensitive)]
    [InlineData("/login.php", EndpointRisk.Sensitive)]
    [InlineData("/wp-login.php", EndpointRisk.Sensitive)]
    [InlineData("/wp-admin/", EndpointRisk.Sensitive)]
    [InlineData("/signin", EndpointRisk.Sensitive)]
    [InlineData("/register", EndpointRisk.Sensitive)]
    [InlineData("/checkout", EndpointRisk.Sensitive)]
    [InlineData("/payment", EndpointRisk.Sensitive)]
    [InlineData("/billing", EndpointRisk.Sensitive)]
    [InlineData("/api/auth", EndpointRisk.Sensitive)]
    [InlineData("/api/token", EndpointRisk.Sensitive)]
    [InlineData("/api/tokens/list", EndpointRisk.Sensitive)]
    [InlineData("/oauth/authorize", EndpointRisk.Sensitive)]
    [InlineData("/.env", EndpointRisk.Sensitive)]
    [InlineData("/.env.local", EndpointRisk.Sensitive)]
    [InlineData("/.git/config", EndpointRisk.Sensitive)]
    [InlineData("/config.php", EndpointRisk.Sensitive)]
    [InlineData("/phpmyadmin/", EndpointRisk.Sensitive)]
    [InlineData("/credentials.json", EndpointRisk.Sensitive)]
    [InlineData("/secrets", EndpointRisk.Sensitive)]
    public void Classify_RecognisesSensitivePaths(string path, EndpointRisk expected)
    {
        Assert.Equal(expected, EndpointRiskClassifier.Classify(path));
    }

    [Theory]
    [InlineData("/static/logo.png")]
    [InlineData("/assets/main.css")]
    [InlineData("/dist/app.js")]
    [InlineData("/fonts/Inter.woff2")]
    [InlineData("/favicon.ico")]
    [InlineData("/robots.txt")]
    [InlineData("/manifest.json.map")]   // .map → static
    public void Classify_RecognisesStaticAssets(string path)
    {
        Assert.Equal(EndpointRisk.Static, EndpointRiskClassifier.Classify(path));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/blog/my-post")]
    [InlineData("/products/123")]
    [InlineData("/about")]
    [InlineData("/api/products")]              // not /api/auth, /api/token, etc.
    [InlineData("/search?q=foo")]
    [InlineData("/manifest.json")]             // .json without sensitive prefix → Normal
    public void Classify_DefaultsToNormal(string path)
    {
        Assert.Equal(EndpointRisk.Normal, EndpointRiskClassifier.Classify(path));
    }

    [Fact]
    public void Classify_NullOrEmpty_Normal()
    {
        Assert.Equal(EndpointRisk.Normal, EndpointRiskClassifier.Classify(null));
        Assert.Equal(EndpointRisk.Normal, EndpointRiskClassifier.Classify(""));
    }

    [Fact]
    public void Classify_StripsQueryString()
    {
        Assert.Equal(EndpointRisk.Sensitive, EndpointRiskClassifier.Classify("/admin?next=/"));
        Assert.Equal(EndpointRisk.Static, EndpointRiskClassifier.Classify("/static/logo.png?v=2"));
    }
}

using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Test.Models;

/// <summary>
///     Comprehensive tests for GeoRoutingOptions
/// </summary>
public class GeoRoutingOptionsTests
{
    #region Default Value Tests

    [Fact]
    public void Constructor_SetsDefaultEnabledToTrue()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.True(options.Enabled);
    }

    [Fact]
    public void Constructor_SetsDefaultTestModeDisabled()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.False(options.EnableTestMode);
    }

    [Fact]
    public void Constructor_SetsDefaultAllowedCountriesToNull()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.Null(options.AllowedCountries);
    }

    [Fact]
    public void Constructor_SetsDefaultBlockedCountriesToNull()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.Null(options.BlockedCountries);
    }

    [Fact]
    public void Constructor_SetsDefaultWhitelistedIpsToNull()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.Null(options.WhitelistedIps);
    }

    [Fact]
    public void Constructor_SetsDefaultRouteToRoot()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.Equal("/", options.DefaultRoute);
    }

    [Fact]
    public void Constructor_InitializesCountryRoutesEmpty()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.NotNull(options.CountryRoutes);
        Assert.Empty(options.CountryRoutes);
    }

    [Fact]
    public void Constructor_SetsDefaultBlockedStatusCode()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.Equal(451, options.BlockedStatusCode); // Unavailable For Legal Reasons
    }

    [Fact]
    public void Constructor_SetsDefaultBlockedPagePathToNull()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.Null(options.BlockedPagePath);
    }

    [Fact]
    public void Constructor_SetsDefaultBlockVpnsToFalse()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.False(options.BlockVpns);
    }

    [Fact]
    public void Constructor_SetsDefaultBlockHostingToFalse()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.False(options.BlockHosting);
    }

    [Fact]
    public void Constructor_SetsDefaultAddCountryHeaderToTrue()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.True(options.AddCountryHeader);
    }

    [Fact]
    public void Constructor_SetsDefaultStoreInContextToTrue()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.True(options.StoreInContext);
    }

    [Fact]
    public void Constructor_SetsDefaultEnableAutoRoutingToFalse()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.False(options.EnableAutoRouting);
    }

    [Fact]
    public void Constructor_SetsDefaultOnBlockedToNull()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.Null(options.OnBlocked);
    }

    [Fact]
    public void Constructor_SetsDefaultOnRoutedToNull()
    {
        // Act
        var options = new GeoRoutingOptions();

        // Assert
        Assert.Null(options.OnRouted);
    }

    #endregion

    #region Property Setting Tests - AllowedCountries

    [Fact]
    public void AllowedCountries_CanBeSetToSingleCountry()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.AllowedCountries = new[] { "US" };

        // Assert
        Assert.Single(options.AllowedCountries);
        Assert.Contains("US", options.AllowedCountries);
    }

    [Fact]
    public void AllowedCountries_CanBeSetToMultipleCountries()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.AllowedCountries = new[] { "US", "GB", "CA", "AU" };

        // Assert
        Assert.Equal(4, options.AllowedCountries.Length);
        Assert.Contains("US", options.AllowedCountries);
        Assert.Contains("GB", options.AllowedCountries);
        Assert.Contains("CA", options.AllowedCountries);
        Assert.Contains("AU", options.AllowedCountries);
    }

    [Fact]
    public void AllowedCountries_CanBeSetToEmpty()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.AllowedCountries = Array.Empty<string>();

        // Assert
        Assert.Empty(options.AllowedCountries);
    }

    #endregion

    #region Property Setting Tests - BlockedCountries

    [Fact]
    public void BlockedCountries_CanBeSetToSingleCountry()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.BlockedCountries = new[] { "KP" };

        // Assert
        Assert.Single(options.BlockedCountries);
        Assert.Contains("KP", options.BlockedCountries);
    }

    [Fact]
    public void BlockedCountries_CanBeSetToMultipleCountries()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.BlockedCountries = new[] { "KP", "IR", "SY", "CU" };

        // Assert
        Assert.Equal(4, options.BlockedCountries.Length);
    }

    #endregion

    #region Property Setting Tests - WhitelistedIps

    [Fact]
    public void WhitelistedIps_CanBeSetToSingleIp()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.WhitelistedIps = new[] { "192.168.1.1" };

        // Assert
        Assert.Single(options.WhitelistedIps);
    }

    [Fact]
    public void WhitelistedIps_CanBeSetToCidrRange()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.WhitelistedIps = new[] { "10.0.0.0/8", "192.168.0.0/16" };

        // Assert
        Assert.Equal(2, options.WhitelistedIps.Length);
    }

    #endregion

    #region Property Setting Tests - CountryRoutes

    [Fact]
    public void CountryRoutes_CanAddRoutes()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.CountryRoutes["US"] = "/en-us";
        options.CountryRoutes["GB"] = "/en-gb";
        options.CountryRoutes["FR"] = "/fr";

        // Assert
        Assert.Equal(3, options.CountryRoutes.Count);
        Assert.Equal("/en-us", options.CountryRoutes["US"]);
        Assert.Equal("/en-gb", options.CountryRoutes["GB"]);
        Assert.Equal("/fr", options.CountryRoutes["FR"]);
    }

    [Fact]
    public void CountryRoutes_CanBeReplaced()
    {
        // Arrange
        var options = new GeoRoutingOptions();
        var newRoutes = new Dictionary<string, string>
        {
            ["DE"] = "/de",
            ["ES"] = "/es"
        };

        // Act
        options.CountryRoutes = newRoutes;

        // Assert
        Assert.Equal(2, options.CountryRoutes.Count);
        Assert.Equal("/de", options.CountryRoutes["DE"]);
    }

    #endregion

    #region Property Setting Tests - Status Codes

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(451)]
    [InlineData(503)]
    public void BlockedStatusCode_CanBeSet(int statusCode)
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.BlockedStatusCode = statusCode;

        // Assert
        Assert.Equal(statusCode, options.BlockedStatusCode);
    }

    [Fact]
    public void BlockedPagePath_CanBeSet()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.BlockedPagePath = "/blocked";

        // Assert
        Assert.Equal("/blocked", options.BlockedPagePath);
    }

    #endregion

    #region Property Setting Tests - Boolean Flags

    [Fact]
    public void Enabled_CanBeDisabled()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.Enabled = false;

        // Assert
        Assert.False(options.Enabled);
    }

    [Fact]
    public void EnableTestMode_CanBeEnabled()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.EnableTestMode = true;

        // Assert
        Assert.True(options.EnableTestMode);
    }

    [Fact]
    public void BlockVpns_CanBeEnabled()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.BlockVpns = true;

        // Assert
        Assert.True(options.BlockVpns);
    }

    [Fact]
    public void BlockHosting_CanBeEnabled()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.BlockHosting = true;

        // Assert
        Assert.True(options.BlockHosting);
    }

    [Fact]
    public void AddCountryHeader_CanBeDisabled()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.AddCountryHeader = false;

        // Assert
        Assert.False(options.AddCountryHeader);
    }

    [Fact]
    public void StoreInContext_CanBeDisabled()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.StoreInContext = false;

        // Assert
        Assert.False(options.StoreInContext);
    }

    [Fact]
    public void EnableAutoRouting_CanBeEnabled()
    {
        // Arrange
        var options = new GeoRoutingOptions();

        // Act
        options.EnableAutoRouting = true;

        // Assert
        Assert.True(options.EnableAutoRouting);
    }

    #endregion

    #region Complete Configuration Tests

    [Fact]
    public void CompleteConfiguration_AllowedCountriesOnly()
    {
        // Act
        var options = new GeoRoutingOptions
        {
            Enabled = true,
            AllowedCountries = new[] { "US", "CA", "GB" },
            BlockedStatusCode = 403,
            AddCountryHeader = true,
            StoreInContext = true
        };

        // Assert
        Assert.True(options.Enabled);
        Assert.Equal(3, options.AllowedCountries!.Length);
        Assert.Null(options.BlockedCountries);
    }

    [Fact]
    public void CompleteConfiguration_BlockedCountriesOnly()
    {
        // Act
        var options = new GeoRoutingOptions
        {
            Enabled = true,
            BlockedCountries = new[] { "KP", "IR", "SY" },
            BlockedStatusCode = 451,
            BlockedPagePath = "/blocked"
        };

        // Assert
        Assert.True(options.Enabled);
        Assert.Equal(3, options.BlockedCountries!.Length);
        Assert.Null(options.AllowedCountries);
    }

    [Fact]
    public void CompleteConfiguration_WithRouting()
    {
        // Act
        var options = new GeoRoutingOptions
        {
            Enabled = true,
            EnableAutoRouting = true,
            DefaultRoute = "/en",
            CountryRoutes = new Dictionary<string, string>
            {
                ["US"] = "/en-us",
                ["GB"] = "/en-gb",
                ["FR"] = "/fr",
                ["DE"] = "/de"
            }
        };

        // Assert
        Assert.True(options.EnableAutoRouting);
        Assert.Equal("/en", options.DefaultRoute);
        Assert.Equal(4, options.CountryRoutes.Count);
    }

    [Fact]
    public void CompleteConfiguration_SecurityFocused()
    {
        // Act
        var options = new GeoRoutingOptions
        {
            Enabled = true,
            BlockVpns = true,
            BlockHosting = true,
            BlockedCountries = new[] { "KP", "IR" },
            WhitelistedIps = new[] { "10.0.0.0/8" },
            BlockedStatusCode = 403
        };

        // Assert
        Assert.True(options.BlockVpns);
        Assert.True(options.BlockHosting);
        Assert.NotNull(options.BlockedCountries);
        Assert.NotNull(options.WhitelistedIps);
    }

    #endregion
}
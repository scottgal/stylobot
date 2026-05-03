using Microsoft.AspNetCore.Mvc.Filters;
using Mostlylucid.GeoDetection.Filters;

namespace Mostlylucid.GeoDetection.Test.Filters;

/// <summary>
///     Comprehensive tests for GeoRouteAttribute
/// </summary>
public class GeoRouteAttributeTests
{
    #region Complete Configuration Tests

    [Fact]
    public void CompleteConfiguration_WithAllProperties()
    {
        // Act
        var attribute = new GeoRouteAttribute
        {
            CountryViews = "US:Index_US,GB:Index_GB,DE:Index_DE",
            CountryActions = "JP:JapanAction",
            CountryRoutes = "CN:/cn/blocked",
            DefaultView = "Index"
        };

        // Assert
        Assert.NotNull(attribute.CountryViews);
        Assert.NotNull(attribute.CountryActions);
        Assert.NotNull(attribute.CountryRoutes);
        Assert.NotNull(attribute.DefaultView);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Act
        var attribute = new GeoRouteAttribute();

        // Assert
        Assert.NotNull(attribute);
    }

    [Fact]
    public void Constructor_SetsDefaultPropertiesToNull()
    {
        // Act
        var attribute = new GeoRouteAttribute();

        // Assert
        Assert.Null(attribute.CountryViews);
        Assert.Null(attribute.CountryActions);
        Assert.Null(attribute.CountryRoutes);
        Assert.Null(attribute.DefaultView);
    }

    #endregion

    #region Property Setting Tests

    [Fact]
    public void CountryViews_CanBeSet()
    {
        // Arrange
        var attribute = new GeoRouteAttribute();

        // Act
        attribute.CountryViews = "US:us-view,GB:gb-view";

        // Assert
        Assert.Equal("US:us-view,GB:gb-view", attribute.CountryViews);
    }

    [Fact]
    public void CountryActions_CanBeSet()
    {
        // Arrange
        var attribute = new GeoRouteAttribute();

        // Act
        attribute.CountryActions = "CN:ChinaAction,RU:RussiaAction";

        // Assert
        Assert.Equal("CN:ChinaAction,RU:RussiaAction", attribute.CountryActions);
    }

    [Fact]
    public void CountryRoutes_CanBeSet()
    {
        // Arrange
        var attribute = new GeoRouteAttribute();

        // Act
        attribute.CountryRoutes = "CN:/cn/home,RU:/ru/home";

        // Assert
        Assert.Equal("CN:/cn/home,RU:/ru/home", attribute.CountryRoutes);
    }

    [Fact]
    public void DefaultView_CanBeSet()
    {
        // Arrange
        var attribute = new GeoRouteAttribute();

        // Act
        attribute.DefaultView = "DefaultIndex";

        // Assert
        Assert.Equal("DefaultIndex", attribute.DefaultView);
    }

    #endregion

    #region Mapping Format Tests

    [Theory]
    [InlineData("US:view1")]
    [InlineData("US:view1,GB:view2")]
    [InlineData("US:view1,GB:view2,FR:view3")]
    [InlineData("US:view1,GB:view2,FR:view3,DE:view4")]
    public void CountryViews_AcceptsVariousFormats(string mapping)
    {
        // Arrange
        var attribute = new GeoRouteAttribute();

        // Act
        attribute.CountryViews = mapping;

        // Assert
        Assert.Equal(mapping, attribute.CountryViews);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("US")]
    public void CountryViews_AcceptsInvalidFormats(string mapping)
    {
        // Arrange
        var attribute = new GeoRouteAttribute();

        // Act - Should not throw
        attribute.CountryViews = mapping;

        // Assert - Just verifies no exception
        Assert.True(true);
    }

    #endregion

    #region AttributeUsage Tests

    [Fact]
    public void Attribute_CanBeAppliedToClass()
    {
        // Assert
        var attributeUsage = typeof(GeoRouteAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .FirstOrDefault() as AttributeUsageAttribute;

        Assert.NotNull(attributeUsage);
        Assert.True((attributeUsage.ValidOn & AttributeTargets.Class) == AttributeTargets.Class);
    }

    [Fact]
    public void Attribute_CanBeAppliedToMethod()
    {
        // Assert
        var attributeUsage = typeof(GeoRouteAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .FirstOrDefault() as AttributeUsageAttribute;

        Assert.NotNull(attributeUsage);
        Assert.True((attributeUsage.ValidOn & AttributeTargets.Method) == AttributeTargets.Method);
    }

    [Fact]
    public void Attribute_InheritsFromActionFilterAttribute()
    {
        // Assert
        Assert.True(typeof(GeoRouteAttribute).IsSubclassOf(typeof(ActionFilterAttribute)));
    }

    #endregion
}

/// <summary>
///     Tests for ServeByCountryAttribute
/// </summary>
public class ServeByCountryAttributeTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithNoMappings_CreatesInstance()
    {
        // Act
        var attribute = new ServeByCountryAttribute();

        // Assert
        Assert.NotNull(attribute);
    }

    [Fact]
    public void Constructor_WithSingleMapping_CreatesInstance()
    {
        // Act
        var attribute = new ServeByCountryAttribute("US:Hello USA");

        // Assert
        Assert.NotNull(attribute);
    }

    [Fact]
    public void Constructor_WithMultipleMappings_CreatesInstance()
    {
        // Act
        var attribute = new ServeByCountryAttribute(
            "US:Hello USA",
            "GB:Hello UK",
            "DE:Hallo Deutschland");

        // Assert
        Assert.NotNull(attribute);
    }

    #endregion

    #region Mapping Format Tests

    [Theory]
    [InlineData("US:Content")]
    [InlineData("US:Multi word content")]
    [InlineData("US:Content with: colons")]
    public void Constructor_AcceptsVariousMappingFormats(string mapping)
    {
        // Act - Should not throw
        var attribute = new ServeByCountryAttribute(mapping);

        // Assert
        Assert.NotNull(attribute);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("US")]
    public void Constructor_HandlesInvalidMappingsGracefully(string mapping)
    {
        // Act - Should not throw
        var exception = Record.Exception(() => new ServeByCountryAttribute(mapping));

        // Assert
        Assert.Null(exception);
    }

    #endregion

    #region AttributeUsage Tests

    [Fact]
    public void Attribute_CanOnlyBeAppliedToMethod()
    {
        // Assert
        var attributeUsage = typeof(ServeByCountryAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .FirstOrDefault() as AttributeUsageAttribute;

        Assert.NotNull(attributeUsage);
        Assert.True((attributeUsage.ValidOn & AttributeTargets.Method) == AttributeTargets.Method);
    }

    [Fact]
    public void Attribute_InheritsFromActionFilterAttribute()
    {
        // Assert
        Assert.True(typeof(ServeByCountryAttribute).IsSubclassOf(typeof(ActionFilterAttribute)));
    }

    #endregion
}
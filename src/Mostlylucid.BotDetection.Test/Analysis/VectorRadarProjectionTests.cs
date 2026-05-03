using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Test.Analysis;

/// <summary>
///     Tests for VectorRadarProjection - projects 129-dim vectors into 8 radar axes.
/// </summary>
public class VectorRadarProjectionTests
{
    [Fact]
    public void AxisLabels_Has8Entries()
    {
        Assert.Equal(8, VectorRadarProjection.AxisLabels.Length);
    }

    [Fact]
    public void Project_NullVector_ReturnsNull()
    {
        var result = VectorRadarProjection.Project(new float[10]);
        Assert.Null(result); // Too short
    }

    [Fact]
    public void Project_ValidVector_Returns8Axes()
    {
        var vector = new float[129];
        // Set some values
        vector[0] = 0.5f; // PageView -> PageView transition
        vector[110] = 0.3f; // Timing regularity
        vector[114] = 0.5f; // Request rate

        var axes = VectorRadarProjection.Project(vector);

        Assert.NotNull(axes);
        Assert.Equal(8, axes!.Length);
    }

    [Fact]
    public void Project_AllZeros_HasMinimumPolygon()
    {
        var vector = new float[129];
        var axes = VectorRadarProjection.Project(vector);

        Assert.NotNull(axes);
        // Every axis should have minimum 0.05 for polygon visibility
        foreach (var axis in axes!)
            Assert.True(axis >= 0.05, $"Axis should be >= 0.05, was {axis}");
    }

    [Fact]
    public void Project_AxesAreClamped01()
    {
        var vector = new float[129];
        // Set extreme values
        for (int i = 0; i < 129; i++)
            vector[i] = 10f;

        var axes = VectorRadarProjection.Project(vector);

        Assert.NotNull(axes);
        foreach (var axis in axes!)
            Assert.True(axis <= 1.0, $"Axis should be <= 1.0, was {axis}");
    }

    [Fact]
    public void Project_HighNavigation_ReflectedInAxis0()
    {
        var vector = new float[129];
        // High PageView transitions (row 0, dims 0-9)
        for (int i = 0; i < 10; i++)
            vector[i] = 0.3f;

        var axes = VectorRadarProjection.Project(vector);

        Assert.NotNull(axes);
        // Navigation axis should be higher than default
        Assert.True(axes![0] > 0.1, $"Navigation axis should reflect PageView transitions, was {axes[0]}");
    }

    [Fact]
    public void Project_OldVector126Dims_ReturnsNull()
    {
        // 126-dim vectors (pre-transition-timing) should still work
        // but without transition timing features
        var vector = new float[118]; // Need at least 118 for temporal features
        vector[110] = 0.5f;

        var axes = VectorRadarProjection.Project(vector);

        Assert.NotNull(axes);
        Assert.Equal(8, axes!.Length);
        // Timing anomaly axis (index 7) should be at minimum since dims 126-128 don't exist
        Assert.Equal(0.05, axes[7], 0.01);
    }

    [Fact]
    public void Project_RealSessionVector_ProducesReasonableShape()
    {
        // Create a realistic session vector
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, DateTimeOffset.UtcNow, "/", 200),
            new(RequestState.StaticAsset, DateTimeOffset.UtcNow.AddMilliseconds(500), "/css/x.css", 200),
            new(RequestState.ApiCall, DateTimeOffset.UtcNow.AddMilliseconds(1500), "/api/data", 200),
            new(RequestState.PageView, DateTimeOffset.UtcNow.AddMilliseconds(3000), "/about", 200),
            new(RequestState.StaticAsset, DateTimeOffset.UtcNow.AddMilliseconds(3500), "/js/app.js", 200),
            new(RequestState.FormSubmit, DateTimeOffset.UtcNow.AddMilliseconds(8000), "/contact", 200)
        };

        var vector = SessionVectorizer.Encode(requests);
        var axes = VectorRadarProjection.Project(vector);

        Assert.NotNull(axes);
        Assert.Equal(8, axes!.Length);

        // All axes should be in valid range
        foreach (var axis in axes)
        {
            Assert.True(axis >= 0.05 && axis <= 1.0, $"Axis {axis} out of range");
        }
    }
}

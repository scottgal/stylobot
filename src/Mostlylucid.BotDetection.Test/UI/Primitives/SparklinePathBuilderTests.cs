using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI.Primitives;

public class SparklinePathBuilderTests
{
    [Fact]
    public void Build_returns_empty_path_for_empty_data()
    {
        var path = SparklinePathBuilder.Build(System.Array.Empty<int>(), width: 60, height: 18);
        Assert.Equal("", path);
    }

    [Fact]
    public void Build_flat_zero_data_renders_baseline()
    {
        var path = SparklinePathBuilder.Build(new[] { 0, 0, 0, 0 }, width: 60, height: 18);
        // 4 points spread across width 60, all y=18 (bottom). Spacing 60/(4-1)=20.
        Assert.Equal("M0,18 L20,18 L40,18 L60,18", path);
    }

    [Fact]
    public void Build_scales_to_max_value()
    {
        var path = SparklinePathBuilder.Build(new[] { 0, 10, 5, 0 }, width: 60, height: 18);
        // max=10 -> y maps 10->0, 5->9, 0->18. Spacing 20.
        Assert.Equal("M0,18 L20,0 L40,9 L60,18", path);
    }

    [Fact]
    public void Build_with_explicit_max_uses_supplied_value()
    {
        // explicit max=20 so all values are 0-50% of the height range
        var path = SparklinePathBuilder.Build(new[] { 0, 20, 10, 0 }, width: 60, height: 18, max: 20);
        Assert.Equal("M0,18 L20,0 L40,9 L60,18", path);
    }

    [Fact]
    public void Build_single_point_renders_baseline_point()
    {
        // One point has no horizontal extent; emit a tiny line at the baseline.
        var path = SparklinePathBuilder.Build(new[] { 5 }, width: 60, height: 18);
        Assert.Equal("M0,0 L0,0", path);
    }
}

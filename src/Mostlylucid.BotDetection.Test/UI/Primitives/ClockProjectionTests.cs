using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI.Primitives;

public class ClockProjectionTests
{
    [Fact]
    public void ProjectMarkov_zero_input_returns_four_zeros()
    {
        var result = ClockProjection.ProjectMarkovTo4Axes(new float[10]);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, result);
    }

    [Fact]
    public void ProjectMarkov_null_input_returns_four_zeros()
    {
        var result = ClockProjection.ProjectMarkovTo4Axes(null!);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, result);
    }

    [Fact]
    public void ProjectMarkov_short_input_returns_four_zeros()
    {
        var result = ClockProjection.ProjectMarkovTo4Axes(new float[3]);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, result);
    }

    [Fact]
    public void ProjectMarkov_isolates_asset_share()
    {
        var freqs = new float[10];
        freqs[2] = 0.4f;
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(0.4, result[0], 5);
        Assert.Equal(0.0, result[1], 5);
        Assert.Equal(0.0, result[2], 5);
        Assert.Equal(0.0, result[3], 5);
    }

    [Fact]
    public void ProjectMarkov_sums_realtime_channels()
    {
        var freqs = new float[10];
        freqs[3] = 0.2f;   // WS
        freqs[4] = 0.10f;  // SignalR
        freqs[5] = 0.05f;  // SSE
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(0.35, result[1], 5);
    }

    [Fact]
    public void ProjectMarkov_sums_form_and_search()
    {
        var freqs = new float[10];
        freqs[6] = 0.3f;   // Form
        freqs[9] = 0.2f;   // Search
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(0.5, result[2], 5);
    }

    [Fact]
    public void ProjectMarkov_passes_404_share_through()
    {
        var freqs = new float[10];
        freqs[8] = 0.7f;
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(0.7, result[3], 5);
    }

    [Fact]
    public void ProjectMarkov_clamps_realtime_to_one()
    {
        var freqs = new float[10];
        freqs[3] = 0.7f;
        freqs[4] = 0.7f;
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(1.0, result[1], 5);
    }
}

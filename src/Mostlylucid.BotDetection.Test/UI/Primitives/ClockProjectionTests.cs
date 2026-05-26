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

    [Fact]
    public void Compose12Axes_places_each_source_at_its_clock_hour()
    {
        // Distinct values per slot so any swap shows up clearly.
        var semantic = new[] { 0.10, 0.11, 0.12, 0.13, 0.14, 0.15, 0.16, 0.17 };
        var markov   = new[] { 0.21, 0.22, 0.23, 0.24 };

        var clock = ClockProjection.Compose12Axes(semantic, markov);

        // Layout is grouped by behavioural quadrant (Footprint / Surface / Cadence /
        // Signal) so a visitor paints a single fat lobe rather than scattered spikes.
        Assert.Equal(12, clock.Length);
        // Footprint -- what they navigate
        Assert.Equal(0.10, clock[0],  5); // 12 Browsing       ← semantic[0]
        Assert.Equal(0.17, clock[1],  5); //  1 Path Diversity ← semantic[7]
        Assert.Equal(0.21, clock[2],  5); //  2 Asset Share    ← markov[0]
        // Surface -- how they interact
        Assert.Equal(0.22, clock[3],  5); //  3 Realtime       ← markov[1]
        Assert.Equal(0.23, clock[4],  5); //  4 Form/Search    ← markov[2]
        Assert.Equal(0.11, clock[5],  5); //  5 API Activity   ← semantic[1]
        // Cadence -- speed / rhythm
        Assert.Equal(0.13, clock[6],  5); //  6 Auth Pressure  ← semantic[3]
        Assert.Equal(0.15, clock[7],  5); //  7 Burst Speed    ← semantic[5]
        Assert.Equal(0.14, clock[8],  5); //  8 Timing         ← semantic[4]
        // Signal -- anomaly / identity tells
        Assert.Equal(0.24, clock[9],  5); //  9 404 Share      ← markov[3]
        Assert.Equal(0.12, clock[10], 5); // 10 Scan/Probe     ← semantic[2]
        Assert.Equal(0.16, clock[11], 5); // 11 Fingerprint    ← semantic[6]
    }

    [Fact]
    public void Compose12Axes_null_semantic_yields_zero_for_semantic_hours_only()
    {
        var markov = new[] { 0.5, 0.5, 0.5, 0.5 };
        var clock = ClockProjection.Compose12Axes(null!, markov);

        Assert.Equal(0.0, clock[0]);   // 12 semantic
        Assert.Equal(0.5, clock[2]);   //  2 markov
        Assert.Equal(0.0, clock[5]);   //  5 semantic
        Assert.Equal(0.5, clock[9]);   //  9 markov
    }

    [Fact]
    public void Compose12Axes_null_markov_yields_zero_for_markov_hours_only()
    {
        var semantic = new[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 };
        var clock = ClockProjection.Compose12Axes(semantic, null!);

        Assert.Equal(0.5, clock[0]);   // 12 semantic
        Assert.Equal(0.0, clock[2]);   //  2 markov
        Assert.Equal(0.5, clock[5]);   //  5 semantic
        Assert.Equal(0.0, clock[9]);   //  9 markov
    }

    [Fact]
    public void Compose12Axes_clamps_inputs_to_zero_one()
    {
        // Pin out-of-range values at the source indices that map to the assertions below:
        //   semantic[0] (clock[0])  = 1.5  → 1.0
        //   semantic[2] (clock[10]) = -0.2 → 0.0
        //   markov[0]   (clock[2])  = 2.0  → 1.0
        //   markov[3]   (clock[9])  = -1.0 → 0.0
        var semantic = new[] { 1.5, 0.5, -0.2, 0.5, 0.5, 0.5, 0.5, 0.5 };
        var markov   = new[] { 2.0, 0.5, 0.5, -1.0 };
        var clock = ClockProjection.Compose12Axes(semantic, markov);

        Assert.Equal(1.0, clock[0]);
        Assert.Equal(0.0, clock[10]);
        Assert.Equal(1.0, clock[2]);
        Assert.Equal(0.0, clock[9]);
    }
}

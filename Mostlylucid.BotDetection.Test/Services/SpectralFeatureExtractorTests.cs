using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Tests for <see cref="SpectralFeatureExtractor" /> static helper.
///     Validates FFT-based spectral feature extraction and temporal correlation.
/// </summary>
public class SpectralFeatureExtractorTests
{
    #region Helper Methods

    /// <summary>
    ///     Generates an array of constant-interval values (bot-like timing).
    /// </summary>
    private static double[] ConstantIntervals(int count, double value = 1.0)
    {
        var intervals = new double[count];
        Array.Fill(intervals, value);
        return intervals;
    }

    /// <summary>
    ///     Generates an array of pseudo-random intervals (human-like timing).
    /// </summary>
    private static double[] RandomIntervals(int count, int seed = 42)
    {
        var rng = new Random(seed);
        var intervals = new double[count];
        for (var i = 0; i < count; i++)
            intervals[i] = 0.5 + rng.NextDouble() * 5.0; // 0.5s to 5.5s
        return intervals;
    }

    /// <summary>
    ///     Generates alternating intervals: [a, b, a, b, ...].
    /// </summary>
    private static double[] AlternatingIntervals(int count, double a = 1.0, double b = 3.0)
    {
        var intervals = new double[count];
        for (var i = 0; i < count; i++)
            intervals[i] = i % 2 == 0 ? a : b;
        return intervals;
    }

    /// <summary>
    ///     Asserts that all numeric properties of a <see cref="SpectralFeatures" /> instance
    ///     fall within the [0,1] range.
    /// </summary>
    private static void AssertAllOutputsBounded(SpectralFeatures features)
    {
        Assert.InRange(features.DominantFrequency, 0.0, 1.0);
        Assert.InRange(features.SpectralEntropy, 0.0, 1.0);
        Assert.InRange(features.HarmonicRatio, 0.0, 1.0);
        Assert.InRange(features.SpectralCentroid, 0.0, 1.0);
        Assert.InRange(features.PeakToAvgRatio, 0.0, 1.0);
    }

    /// <summary>
    ///     Asserts that the features match the known InsufficientData defaults.
    /// </summary>
    private static void AssertInsufficientDataDefaults(SpectralFeatures features)
    {
        Assert.Equal(0.0, features.DominantFrequency);
        Assert.Equal(1.0, features.SpectralEntropy);
        Assert.Equal(0.0, features.HarmonicRatio);
        Assert.Equal(0.5, features.SpectralCentroid);
        Assert.Equal(0.0, features.PeakToAvgRatio);
        Assert.False(features.HasSufficientData);
    }

    #endregion

    #region Extract — Insufficient Data

    [Fact]
    public void Extract_NullInput_ReturnsInsufficientData()
    {
        // Act
        var result = SpectralFeatureExtractor.Extract(null!);

        // Assert
        AssertInsufficientDataDefaults(result);
    }

    [Fact]
    public void Extract_EmptyArray_ReturnsInsufficientData()
    {
        // Act
        var result = SpectralFeatureExtractor.Extract([]);

        // Assert
        AssertInsufficientDataDefaults(result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Extract_TooFewIntervals_ReturnsInsufficientData(int count)
    {
        // Arrange
        var intervals = ConstantIntervals(count);

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert
        Assert.False(result.HasSufficientData);
        AssertInsufficientDataDefaults(result);
    }

    [Fact]
    public void Extract_InsufficientDataDefaults_HaveExpectedValues()
    {
        // Act — use null to trigger the InsufficientData return
        var result = SpectralFeatureExtractor.Extract(null!);

        // Assert — verify each default precisely
        Assert.Equal(0.0, result.DominantFrequency);
        Assert.Equal(1.0, result.SpectralEntropy);
        Assert.Equal(0.0, result.HarmonicRatio);
        Assert.Equal(0.5, result.SpectralCentroid);
        Assert.Equal(0.0, result.PeakToAvgRatio);
        Assert.False(result.HasSufficientData);
    }

    #endregion

    #region Extract — Sufficient Data

    [Fact]
    public void Extract_ExactlyEightIntervals_HasSufficientData()
    {
        // Arrange — exactly MinIntervals = 8
        var intervals = RandomIntervals(8);

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert
        Assert.True(result.HasSufficientData);
        AssertAllOutputsBounded(result);
    }

    [Fact]
    public void Extract_ConstantIntervals_HighSpectralEntropy()
    {
        // Arrange — all identical values; DC is the only energy source
        var intervals = ConstantIntervals(16);

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert
        Assert.True(result.HasSufficientData);
        // A constant signal has all energy at DC (index 0), which is excluded
        // from the magnitude spectrum. With near-zero remaining energy, the
        // implementation defaults spectral entropy to 1.0.
        Assert.Equal(1.0, result.SpectralEntropy);
        AssertAllOutputsBounded(result);
    }

    [Fact]
    public void Extract_RandomIntervals_HighSpectralEntropy()
    {
        // Arrange — varied intervals simulate a human visitor
        var intervals = RandomIntervals(32);

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert
        Assert.True(result.HasSufficientData);
        // Random noise distributes energy broadly, so entropy should be high.
        Assert.InRange(result.SpectralEntropy, 0.5, 1.0);
        AssertAllOutputsBounded(result);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void Extract_VariousLengths_AllOutputsBounded(int count)
    {
        // Arrange
        var intervals = RandomIntervals(count, seed: count);

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert
        Assert.True(result.HasSufficientData);
        AssertAllOutputsBounded(result);
    }

    [Fact]
    public void Extract_LargeInput_CompletesWithoutError()
    {
        // Arrange — 100 intervals
        var intervals = RandomIntervals(100);

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert
        Assert.True(result.HasSufficientData);
        AssertAllOutputsBounded(result);
    }

    #endregion

    #region Extract — Edge Cases

    [Fact]
    public void Extract_NegativeIntervals_DoesNotCrash()
    {
        // Arrange — negative values are unusual but should not throw
        var intervals = new double[] { -1.0, -2.0, -0.5, -3.0, -1.5, -2.5, -0.8, -1.2 };

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert
        Assert.True(result.HasSufficientData);
        AssertAllOutputsBounded(result);
    }

    [Fact]
    public void Extract_AllZeroIntervals_DoesNotCrash()
    {
        // Arrange — all zeros (degenerate input)
        var intervals = new double[16];

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert — zero input has zero energy everywhere
        Assert.True(result.HasSufficientData);
        // All outputs should still be valid numbers in [0,1]
        AssertAllOutputsBounded(result);
    }

    [Fact]
    public void Extract_VeryLargeValues_DoesNotOverflow()
    {
        // Arrange
        var intervals = new double[16];
        for (var i = 0; i < intervals.Length; i++)
            intervals[i] = 1e10 + i * 1e8;

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert
        Assert.True(result.HasSufficientData);
        AssertAllOutputsBounded(result);
    }

    [Fact]
    public void Extract_SingleValueRepeated_EntropyDefaultsToOne()
    {
        // Arrange — same value repeated many times
        var intervals = ConstantIntervals(32, 2.5);

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert — constant signal has all energy at DC (excluded from spectrum),
        // so non-DC totalEnergy is near zero and entropy defaults to 1.0.
        Assert.True(result.HasSufficientData);
        Assert.Equal(1.0, result.SpectralEntropy);
    }

    [Fact]
    public void Extract_AlternatingValues_DetectsDominantFrequency()
    {
        // Arrange — alternating pattern creates a clear frequency component
        var intervals = AlternatingIntervals(32, 1.0, 3.0);

        // Act
        var result = SpectralFeatureExtractor.Extract(intervals);

        // Assert
        Assert.True(result.HasSufficientData);
        // The alternating pattern should produce a strong dominant frequency
        Assert.True(result.DominantFrequency > 0.0,
            "Alternating pattern should have a non-zero dominant frequency.");
        // Should also show a prominent peak relative to average
        Assert.True(result.PeakToAvgRatio > 0.0,
            "Alternating pattern should produce a non-trivial peak-to-average ratio.");
        AssertAllOutputsBounded(result);
    }

    #endregion

    #region ComputeTemporalCorrelation — Null / Short Input

    [Fact]
    public void ComputeTemporalCorrelation_NullFirstArray_ReturnsZero()
    {
        // Arrange
        var b = RandomIntervals(16);

        // Act
        var result = SpectralFeatureExtractor.ComputeTemporalCorrelation(null!, b);

        // Assert
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeTemporalCorrelation_NullSecondArray_ReturnsZero()
    {
        // Arrange
        var a = RandomIntervals(16);

        // Act
        var result = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, null!);

        // Assert
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeTemporalCorrelation_BothNull_ReturnsZero()
    {
        // Act
        var result = SpectralFeatureExtractor.ComputeTemporalCorrelation(null!, null!);

        // Assert
        Assert.Equal(0.0, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void ComputeTemporalCorrelation_TooFewInFirstArray_ReturnsZero(int count)
    {
        // Arrange
        var a = ConstantIntervals(count);
        var b = RandomIntervals(16);

        // Act
        var result = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, b);

        // Assert
        Assert.Equal(0.0, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void ComputeTemporalCorrelation_TooFewInSecondArray_ReturnsZero(int count)
    {
        // Arrange
        var a = RandomIntervals(16);
        var b = ConstantIntervals(count);

        // Act
        var result = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, b);

        // Assert
        Assert.Equal(0.0, result);
    }

    #endregion

    #region ComputeTemporalCorrelation — Valid Input

    [Fact]
    public void ComputeTemporalCorrelation_IdenticalArrays_HighCorrelation()
    {
        // Arrange
        var intervals = RandomIntervals(16);

        // Act
        var result = SpectralFeatureExtractor.ComputeTemporalCorrelation(intervals, intervals);

        // Assert — auto-correlation should be very high
        Assert.InRange(result, 0.8, 1.0);
    }

    [Fact]
    public void ComputeTemporalCorrelation_DifferentArrays_StillBounded()
    {
        // Arrange — two different random sequences
        var a = RandomIntervals(16, seed: 1);
        var b = RandomIntervals(16, seed: 999);

        // Act
        var correlationDiff = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, b);

        // Assert — output is always bounded to [0,1] regardless of input
        Assert.InRange(correlationDiff, 0.0, 1.0);
    }

    [Fact]
    public void ComputeTemporalCorrelation_StructurallyDifferent_StillBounded()
    {
        // Arrange — use arrays with deliberately different structure
        var ascending = new double[64];
        for (var i = 0; i < ascending.Length; i++)
            ascending[i] = 0.1 + i * 0.1;

        var alternating = AlternatingIntervals(64, 0.5, 5.0);

        // Act
        var correlationDiff = SpectralFeatureExtractor.ComputeTemporalCorrelation(ascending, alternating);

        // Assert — regardless of structure, output is always bounded [0,1]
        Assert.InRange(correlationDiff, 0.0, 1.0);
    }

    [Fact]
    public void ComputeTemporalCorrelation_ZeroMeanSignals_IdenticalCorrelatesHigherThanDifferent()
    {
        // Arrange — zero-mean signals avoid DC dominance that can mask structural
        // differences in the cross-correlation normalization.
        var a = new double[64];
        var b = new double[64];
        var rngA = new Random(123);
        var rngB = new Random(456);
        for (var i = 0; i < 64; i++)
        {
            a[i] = rngA.NextDouble() - 0.5; // mean ~0
            b[i] = rngB.NextDouble() - 0.5; // mean ~0
        }

        // Act
        var correlationSame = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, a);
        var correlationDiff = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, b);

        // Assert — identical should correlate higher than different with zero-mean data
        Assert.True(correlationDiff <= correlationSame,
            $"Different zero-mean arrays ({correlationDiff:F4}) should not exceed identical ({correlationSame:F4}).");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void ComputeTemporalCorrelation_VariousLengths_OutputBounded(int count)
    {
        // Arrange
        var a = RandomIntervals(count, seed: count);
        var b = RandomIntervals(count, seed: count + 1);

        // Act
        var result = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, b);

        // Assert
        Assert.InRange(result, 0.0, 1.0);
    }

    [Fact]
    public void ComputeTemporalCorrelation_LongArrays_CompletesWithinCap()
    {
        // Arrange — 200 elements, exceeding the internal 128-element cap
        var a = RandomIntervals(200, seed: 10);
        var b = RandomIntervals(200, seed: 20);

        // Act
        var result = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, b);

        // Assert — should complete without error, output bounded
        Assert.InRange(result, 0.0, 1.0);
    }

    [Fact]
    public void ComputeTemporalCorrelation_Symmetry()
    {
        // Arrange
        var a = RandomIntervals(16, seed: 100);
        var b = RandomIntervals(16, seed: 200);

        // Act
        var resultAB = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, b);
        var resultBA = SpectralFeatureExtractor.ComputeTemporalCorrelation(b, a);

        // Assert — cross-correlation magnitude should be symmetric
        Assert.Equal(resultAB, resultBA, precision: 10);
    }

    [Fact]
    public void ComputeTemporalCorrelation_DifferentLengthArrays_Works()
    {
        // Arrange — arrays with different lengths (both >= 8)
        var a = RandomIntervals(10, seed: 50);
        var b = RandomIntervals(20, seed: 60);

        // Act
        var result = SpectralFeatureExtractor.ComputeTemporalCorrelation(a, b);

        // Assert
        Assert.InRange(result, 0.0, 1.0);
    }

    #endregion

    #region Extract — Periodic vs Random Comparison

    [Fact]
    public void Extract_PeriodicVsRandom_PeriodicHasLowerEntropy()
    {
        // Arrange — alternating pattern has energy concentrated at one frequency;
        // random intervals spread energy across many frequencies.
        var periodic = AlternatingIntervals(32, 1.0, 3.0);
        var random = RandomIntervals(32);

        // Act
        var periodicResult = SpectralFeatureExtractor.Extract(periodic);
        var randomResult = SpectralFeatureExtractor.Extract(random);

        // Assert — periodic (bot-like) should have lower entropy than random (human-like)
        Assert.True(periodicResult.SpectralEntropy < randomResult.SpectralEntropy,
            $"Periodic entropy ({periodicResult.SpectralEntropy:F4}) should be less than random ({randomResult.SpectralEntropy:F4}).");
    }

    [Fact]
    public void Extract_ConstantIntervals_EntropyEqualsOne_BecauseDcIsExcluded()
    {
        // Arrange — constant signal has all energy at DC which is excluded
        var constant = ConstantIntervals(32);
        var random = RandomIntervals(32);

        // Act
        var constantResult = SpectralFeatureExtractor.Extract(constant);
        var randomResult = SpectralFeatureExtractor.Extract(random);

        // Assert — counter-intuitive: constant actually has HIGHER entropy (1.0)
        // because after removing DC there is no meaningful frequency content,
        // causing the default entropy of 1.0 to be returned.
        Assert.Equal(1.0, constantResult.SpectralEntropy);
        Assert.True(randomResult.SpectralEntropy < 1.0,
            "Random intervals should have entropy below 1.0 since they produce real frequency content.");
    }

    #endregion
}

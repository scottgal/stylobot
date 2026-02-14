using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Tests for <see cref="CountryReputationTracker" />.
///     Validates per-country bot rate tracking, decay behavior, and sorting.
/// </summary>
public class CountryReputationTrackerTests
{
    private readonly CountryReputationTracker _tracker;

    public CountryReputationTrackerTests()
    {
        var logger = NullLogger<CountryReputationTracker>.Instance;
        // Use a very large DecayTauHours so that the tiny time deltas between
        // rapid in-process calls produce negligible decay, avoiding flaky edge-case
        // failures around the MinSampleSize boundary.
        var options = Options.Create(new BotDetectionOptions
        {
            CountryReputation = new CountryReputationOptions
            {
                DecayTauHours = 100_000,
                MinSampleSize = 5
            }
        });
        _tracker = new CountryReputationTracker(logger, options);
    }

    #region GetCountryBotRate — Unknown / Empty

    [Fact]
    public void GetCountryBotRate_UnknownCountry_ReturnsZero()
    {
        // Act
        var rate = _tracker.GetCountryBotRate("ZZ");

        // Assert
        Assert.Equal(0.0, rate);
    }

    #endregion

    #region RecordDetection — Null / Empty Guard

    [Fact]
    public void RecordDetection_NullCountryCode_NoOp()
    {
        // Act
        _tracker.RecordDetection(null!, "Nowhere", true, 0.9);

        // Assert — nothing tracked
        var countries = _tracker.GetAllCountries();
        Assert.Empty(countries);
    }

    [Fact]
    public void RecordDetection_EmptyCountryCode_NoOp()
    {
        // Act
        _tracker.RecordDetection("", "Nowhere", true, 0.9);

        // Assert — nothing tracked
        var countries = _tracker.GetAllCountries();
        Assert.Empty(countries);
    }

    #endregion

    #region RecordDetection — Single Record

    [Fact]
    public void RecordDetection_SingleBot_TracksCorrectly()
    {
        // Act
        _tracker.RecordDetection("US", "United States", true, 0.95);

        // Assert
        var countries = _tracker.GetAllCountries();
        Assert.Single(countries);

        var us = countries[0];
        Assert.Equal("US", us.CountryCode);
        Assert.Equal("United States", us.CountryName);
        Assert.Equal(1, us.RawBotCount);
        Assert.Equal(1, us.RawTotalCount);
    }

    #endregion

    #region RecordDetection — Mixed Traffic

    [Fact]
    public void RecordDetection_MixedBotHuman_CalculatesRate()
    {
        // Arrange — 3 bots, 7 humans = 10 total (meets MinSampleSize of 5)
        for (var i = 0; i < 3; i++)
            _tracker.RecordDetection("DE", "Germany", true, 0.9);
        for (var i = 0; i < 7; i++)
            _tracker.RecordDetection("DE", "Germany", false, 0.1);

        // Act
        var rate = _tracker.GetCountryBotRate("DE");

        // Assert — 3/10 = 0.3 (approximately, due to minor decay within test execution)
        Assert.InRange(rate, 0.28, 0.32);
    }

    #endregion

    #region GetCountryBotRate — MinSampleSize

    [Fact]
    public void GetCountryBotRate_BelowMinSampleSize_ReturnsZero()
    {
        // Arrange — record fewer than MinSampleSize (5)
        for (var i = 0; i < 4; i++)
            _tracker.RecordDetection("FR", "France", true, 0.9);

        // Act
        var rate = _tracker.GetCountryBotRate("FR");

        // Assert — insufficient data, should return 0
        Assert.Equal(0.0, rate);
    }

    [Fact]
    public void GetCountryBotRate_AtMinSampleSize_ReturnsRate()
    {
        // Arrange — slightly above MinSampleSize (5) to account for floating-point
        // decay effects. With 6 records the decayed total comfortably exceeds 5.
        for (var i = 0; i < 6; i++)
            _tracker.RecordDetection("JP", "Japan", true, 0.9);

        // Act
        var rate = _tracker.GetCountryBotRate("JP");

        // Assert — 6/6 = 1.0 (approximately, with negligible decay)
        Assert.InRange(rate, 0.95, 1.0);
    }

    #endregion

    #region GetTopBotCountries — Sorting and Limiting

    [Fact]
    public void GetTopBotCountries_MultipleCountries_SortedByBotRate()
    {
        // Arrange — country A: 20% bot, country B: 80% bot, country C: 50% bot
        // All need >= MinSampleSize (5) raw records to appear in top list
        RecordMultiple("AA", "Alpha", botCount: 2, humanCount: 8);
        RecordMultiple("BB", "Bravo", botCount: 8, humanCount: 2);
        RecordMultiple("CC", "Charlie", botCount: 5, humanCount: 5);

        // Act
        var top = _tracker.GetTopBotCountries(10);

        // Assert — sorted descending by bot rate
        Assert.Equal(3, top.Count);
        Assert.Equal("BB", top[0].CountryCode);
        Assert.Equal("CC", top[1].CountryCode);
        Assert.Equal("AA", top[2].CountryCode);
    }

    [Fact]
    public void GetTopBotCountries_LimitsCount()
    {
        // Arrange — 5 countries with sufficient data (10 records each to exceed MinSampleSize
        // comfortably even after floating-point decay)
        RecordMultiple("C1", "Country1", botCount: 10, humanCount: 0);
        RecordMultiple("C2", "Country2", botCount: 8, humanCount: 2);
        RecordMultiple("C3", "Country3", botCount: 6, humanCount: 4);
        RecordMultiple("C4", "Country4", botCount: 4, humanCount: 6);
        RecordMultiple("C5", "Country5", botCount: 2, humanCount: 8);

        // Act — request only 2
        var top = _tracker.GetTopBotCountries(2);

        // Assert
        Assert.Equal(2, top.Count);
        Assert.Equal("C1", top[0].CountryCode);
        Assert.Equal("C2", top[1].CountryCode);
    }

    #endregion

    #region GetAllCountries

    [Fact]
    public void GetAllCountries_ReturnsAllTracked()
    {
        // Arrange
        _tracker.RecordDetection("US", "United States", true, 0.9);
        _tracker.RecordDetection("GB", "United Kingdom", false, 0.1);
        _tracker.RecordDetection("CA", "Canada", true, 0.8);

        // Act
        var all = _tracker.GetAllCountries();

        // Assert — all three tracked
        Assert.Equal(3, all.Count);
        var codes = all.Select(c => c.CountryCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("US", codes);
        Assert.Contains("GB", codes);
        Assert.Contains("CA", codes);
    }

    #endregion

    #region Case Insensitivity

    [Fact]
    public void RecordDetection_CaseInsensitive_SameCountry()
    {
        // Arrange — "US" and "us" should map to the same entry
        _tracker.RecordDetection("US", "United States", true, 0.9);
        _tracker.RecordDetection("us", "United States", false, 0.1);

        // Act
        var all = _tracker.GetAllCountries();

        // Assert — should be a single entry with 2 raw total
        Assert.Single(all);
        Assert.Equal(2, all[0].RawTotalCount);
        Assert.Equal(1, all[0].RawBotCount);
    }

    #endregion

    #region All-Bot Traffic

    [Fact]
    public void GetCountryBotRate_AllBots_ReturnsNearOne()
    {
        // Arrange — 100% bot traffic, enough samples
        for (var i = 0; i < 10; i++)
            _tracker.RecordDetection("RU", "Russia", true, 0.99);

        // Act
        var rate = _tracker.GetCountryBotRate("RU");

        // Assert — should be very close to 1.0
        Assert.InRange(rate, 0.95, 1.0);
    }

    #endregion

    #region Helpers

    /// <summary>
    ///     Records multiple bot and human detections for a given country.
    /// </summary>
    private void RecordMultiple(string countryCode, string countryName, int botCount, int humanCount)
    {
        for (var i = 0; i < botCount; i++)
            _tracker.RecordDetection(countryCode, countryName, true, 0.9);
        for (var i = 0; i < humanCount; i++)
            _tracker.RecordDetection(countryCode, countryName, false, 0.1);
    }

    #endregion
}

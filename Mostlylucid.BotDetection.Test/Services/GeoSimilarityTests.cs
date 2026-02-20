using Mostlylucid.BotDetection.Clustering;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class GeoSimilarityTests
{
    #region Haversine Distance Tests

    [Fact]
    public void GeoSimilarity_SameLocation_ReturnsOne()
    {
        // London, UK - exact same coordinates
        var a = CreateFeature(latitude: 51.5074, longitude: -0.1278);
        var b = CreateFeature(latitude: 51.5074, longitude: -0.1278);

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.Equal(1.0, sim);
    }

    [Fact]
    public void GeoSimilarity_SameCity_HighSimilarity()
    {
        // Two points in London (~15km apart)
        var a = CreateFeature(latitude: 51.5074, longitude: -0.1278); // Central London
        var b = CreateFeature(latitude: 51.4700, longitude: -0.0400); // Greenwich

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.True(sim >= 0.9, $"Same city should be >= 0.9, got {sim}");
    }

    [Fact]
    public void GeoSimilarity_SameMetroArea_HighSimilarity()
    {
        // London and Oxford (~85km)
        var a = CreateFeature(latitude: 51.5074, longitude: -0.1278); // London
        var b = CreateFeature(latitude: 51.7520, longitude: -1.2577); // Oxford

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.InRange(sim, 0.85, 1.0);
    }

    [Fact]
    public void GeoSimilarity_SameCountry_ModerateSimilarity()
    {
        // London and Edinburgh (~530km)
        var a = CreateFeature(latitude: 51.5074, longitude: -0.1278); // London
        var b = CreateFeature(latitude: 55.9533, longitude: -3.1883); // Edinburgh

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.InRange(sim, 0.5, 0.85);
    }

    [Fact]
    public void GeoSimilarity_SameContinent_LowSimilarity()
    {
        // London and Madrid (~1264km)
        var a = CreateFeature(latitude: 51.5074, longitude: -0.1278); // London
        var b = CreateFeature(latitude: 40.4168, longitude: -3.7038); // Madrid

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.InRange(sim, 0.3, 0.7);
    }

    [Fact]
    public void GeoSimilarity_DifferentContinents_VeryLowSimilarity()
    {
        // London and Tokyo (~9558km)
        var a = CreateFeature(latitude: 51.5074, longitude: -0.1278); // London
        var b = CreateFeature(latitude: 35.6762, longitude: 139.6503); // Tokyo

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.InRange(sim, 0.0, 0.3);
    }

    [Fact]
    public void GeoSimilarity_Antipodal_VeryLow()
    {
        // Near-antipodal points (London and New Zealand ~18,000km)
        var a = CreateFeature(latitude: 51.5, longitude: 0.0);
        var b = CreateFeature(latitude: -41.3, longitude: 174.8);

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.Equal(0.1, sim); // Very distant
    }

    #endregion

    #region Categorical Fallback Tests

    [Fact]
    public void GeoSimilarity_NoLatLon_SameCountry_HighSimilarity()
    {
        // Same country, no lat/lon → should be 1.0 (best available data)
        var a = CreateFeature(countryCode: "US");
        var b = CreateFeature(countryCode: "US");

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.Equal(1.0, sim);
    }

    [Fact]
    public void GeoSimilarity_NoLatLon_SameCountryAndRegion_HighSimilarity()
    {
        var a = CreateFeature(countryCode: "US", regionCode: "CA");
        var b = CreateFeature(countryCode: "US", regionCode: "CA");

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.Equal(1.0, sim);
    }

    [Fact]
    public void GeoSimilarity_NoLatLon_DifferentCountry_SameContinent()
    {
        var a = CreateFeature(countryCode: "US", continentCode: "NA");
        var b = CreateFeature(countryCode: "CA", continentCode: "NA");

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.Equal(0.4, sim); // Same continent
    }

    [Fact]
    public void GeoSimilarity_NoLatLon_DifferentContinent()
    {
        var a = CreateFeature(countryCode: "US", continentCode: "NA");
        var b = CreateFeature(countryCode: "JP", continentCode: "AS");

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.Equal(0.3, sim); // One has geo, other has geo, but mismatched
    }

    [Fact]
    public void GeoSimilarity_BothNull_Neutral()
    {
        var a = CreateFeature(); // No geo data
        var b = CreateFeature(); // No geo data

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.Equal(1.0, sim); // No penalty when no data
    }

    [Fact]
    public void GeoSimilarity_OneHasCountryOtherNull_Penalty()
    {
        var a = CreateFeature(countryCode: "US");
        var b = CreateFeature(); // No country

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.Equal(0.3, sim); // Slight penalty
    }

    [Fact]
    public void GeoSimilarity_CaseInsensitive()
    {
        var a = CreateFeature(countryCode: "us");
        var b = CreateFeature(countryCode: "US");

        var sim = BotClusterService.ComputeGeoSimilarity(a, b);
        Assert.Equal(1.0, sim);
    }

    #endregion

    #region ComputeSimilarity with Adaptive Weights

    [Fact]
    public void ComputeSimilarity_WithAdaptiveWeights_Respects()
    {
        var a = CreateFullFeature("s1", countryCode: "US", asn: "AS15169");
        var b = CreateFullFeature("s2", countryCode: "US", asn: "AS15169");

        var defaultSim = BotClusterService.ComputeSimilarity(a, b);

        // Custom weights: put all weight on geo
        var heavyGeoWeights = new Dictionary<string, double>();
        foreach (var key in AdaptiveSimilarityWeighter.GetDefaultWeights().Keys)
            heavyGeoWeights[key] = 0.01;
        heavyGeoWeights["geo"] = 0.83; // Sum to ~1.0

        var geoSim = BotClusterService.ComputeSimilarity(a, b, heavyGeoWeights);

        // Both have same country, no lat/lon → geoSim=1.0
        // With 83% weight on geo, similarity should be very high
        Assert.True(geoSim > 0.9, $"Expected geo-heavy sim > 0.9, got {geoSim}");
    }

    [Fact]
    public void ComputeSimilarity_WithDriftSignals_Included()
    {
        var a = CreateFullFeature("s1", selfDrift: 0.8, humanDrift: 0.9, loopScore: 0.7);
        var b = CreateFullFeature("s2", selfDrift: 0.8, humanDrift: 0.9, loopScore: 0.7);

        var sim = BotClusterService.ComputeSimilarity(a, b);

        // Identical drift signals → high similarity
        Assert.True(sim > 0.8, $"Expected sim > 0.8 for identical features, got {sim}");
    }

    [Fact]
    public void ComputeSimilarity_DifferentDriftSignals_LowerSimilarity()
    {
        var a = CreateFullFeature("s1", selfDrift: 0.9, loopScore: 0.8);
        var b = CreateFullFeature("s2", selfDrift: 0.1, loopScore: 0.0);

        var aSame = CreateFullFeature("s3", selfDrift: 0.9, loopScore: 0.8);

        var simDiff = BotClusterService.ComputeSimilarity(a, b);
        var simSame = BotClusterService.ComputeSimilarity(a, aSame);

        Assert.True(simSame > simDiff,
            $"Same drift ({simSame:F4}) should be > different drift ({simDiff:F4})");
    }

    #endregion

    #region Helpers

    private static BotClusterService.FeatureVector CreateFeature(
        double? latitude = null,
        double? longitude = null,
        string? countryCode = null,
        string? regionCode = null,
        string? continentCode = null)
    {
        return new BotClusterService.FeatureVector
        {
            Signature = "test",
            TimingRegularity = 0.5,
            RequestRate = 10.0,
            PathDiversity = 0.3,
            PathEntropy = 2.0,
            AvgBotProbability = 0.8,
            CountryCode = countryCode,
            IsDatacenter = false,
            Asn = null,
            FirstSeen = DateTime.UtcNow.AddMinutes(-5),
            LastSeen = DateTime.UtcNow,
            Latitude = latitude,
            Longitude = longitude,
            ContinentCode = continentCode,
            RegionCode = regionCode
        };
    }

    private static BotClusterService.FeatureVector CreateFullFeature(
        string signature,
        double timingRegularity = 0.5,
        double requestRate = 10.0,
        double pathDiversity = 0.3,
        double pathEntropy = 2.0,
        double avgBotProbability = 0.8,
        string? countryCode = "US",
        bool isDatacenter = true,
        string? asn = "AS15169",
        double selfDrift = 0.0,
        double humanDrift = 0.0,
        double loopScore = 0.0,
        double sequenceSurprise = 0.0,
        double transitionNovelty = 0.0,
        double entropyDelta = 0.0)
    {
        return new BotClusterService.FeatureVector
        {
            Signature = signature,
            TimingRegularity = timingRegularity,
            RequestRate = requestRate,
            PathDiversity = pathDiversity,
            PathEntropy = pathEntropy,
            AvgBotProbability = avgBotProbability,
            CountryCode = countryCode,
            IsDatacenter = isDatacenter,
            Asn = asn,
            FirstSeen = DateTime.UtcNow.AddMinutes(-5),
            LastSeen = DateTime.UtcNow,
            SelfDrift = selfDrift,
            HumanDrift = humanDrift,
            LoopScore = loopScore,
            SequenceSurprise = sequenceSurprise,
            TransitionNovelty = transitionNovelty,
            EntropyDelta = entropyDelta
        };
    }

    #endregion
}

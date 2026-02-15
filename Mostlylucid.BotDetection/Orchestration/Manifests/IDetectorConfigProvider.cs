using Microsoft.Extensions.Configuration;

namespace Mostlylucid.BotDetection.Orchestration.Manifests;

/// <summary>
/// Provides access to detector configuration from YAML manifests with appsettings.json overrides.
/// </summary>
public interface IDetectorConfigProvider
{
    /// <summary>
    /// Get the manifest for a specific detector by name.
    /// </summary>
    DetectorManifest? GetManifest(string detectorName);

    /// <summary>
    /// Get the defaults for a detector, with any appsettings overrides applied.
    /// </summary>
    DetectorDefaults GetDefaults(string detectorName);

    /// <summary>
    /// Get a specific parameter value from the detector's config.
    /// </summary>
    T GetParameter<T>(string detectorName, string parameterName, T defaultValue);

    /// <summary>
    /// Get all detector manifests.
    /// </summary>
    IReadOnlyDictionary<string, DetectorManifest> GetAllManifests();
}

/// <summary>
/// Provides detector configuration from YAML manifests with appsettings.json override support.
/// Configuration hierarchy (highest precedence first):
/// 1. appsettings.json BotDetection:Detectors:{Name}:*
/// 2. YAML manifest defaults
/// 3. Built-in code defaults
/// </summary>
public sealed class DetectorConfigProvider : IDetectorConfigProvider
{
    private readonly DetectorManifestLoader _manifestLoader;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, DetectorDefaults> _resolvedDefaults = new();
    private readonly object _lock = new();

    public DetectorConfigProvider(
        DetectorManifestLoader manifestLoader,
        IConfiguration configuration)
    {
        _manifestLoader = manifestLoader;
        _configuration = configuration;
    }

    public DetectorManifest? GetManifest(string detectorName)
    {
        return _manifestLoader.GetDetectorManifest(detectorName);
    }

    public DetectorDefaults GetDefaults(string detectorName)
    {
        lock (_lock)
        {
            if (_resolvedDefaults.TryGetValue(detectorName, out var cached))
                return cached;

            var defaults = ResolveDefaults(detectorName);
            _resolvedDefaults[detectorName] = defaults;
            return defaults;
        }
    }

    public T GetParameter<T>(string detectorName, string parameterName, T defaultValue)
    {
        // Try appsettings first
        // Use string lookup to avoid value-type defaults (GetValue<double> returns 0.0, not null)
        var configPath = $"BotDetection:Detectors:{detectorName}:Defaults:Parameters:{parameterName}";
        var rawValue = _configuration[configPath];
        if (rawValue is not null)
        {
            try { return (T)Convert.ChangeType(rawValue, typeof(T)); }
            catch { /* fall through */ }
        }

        // Then try manifest
        var manifest = GetManifest(detectorName);
        if (manifest?.Defaults.Parameters.TryGetValue(parameterName, out var value) == true)
        {
            try
            {
                return ConvertParameter<T>(value);
            }
            catch
            {
                // Fall through to default
            }
        }

        return defaultValue;
    }

    public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
    {
        return _manifestLoader.GetAllDetectorManifests();
    }

    private DetectorDefaults ResolveDefaults(string detectorName)
    {
        var manifest = GetManifest(detectorName);
        var yamlDefaults = manifest?.Defaults ?? new DetectorDefaults();

        // Get appsettings section for this detector
        var configSection = _configuration.GetSection($"BotDetection:Detectors:{detectorName}:Defaults");

        // Merge: appsettings overrides YAML
        return new DetectorDefaults
        {
            Weights = MergeWeights(yamlDefaults.Weights, configSection.GetSection("Weights")),
            Confidence = MergeConfidence(yamlDefaults.Confidence, configSection.GetSection("Confidence")),
            Timing = MergeTiming(yamlDefaults.Timing, configSection.GetSection("Timing")),
            Features = MergeFeatures(yamlDefaults.Features, configSection.GetSection("Features")),
            Parameters = MergeParameters(yamlDefaults.Parameters, configSection.GetSection("Parameters"))
        };
    }

    private static WeightDefaults MergeWeights(WeightDefaults yaml, IConfigurationSection config)
    {
        return new WeightDefaults
        {
            Base = config.GetValue<double?>("Base") ?? yaml.Base,
            BotSignal = config.GetValue<double?>("BotSignal") ?? yaml.BotSignal,
            HumanSignal = config.GetValue<double?>("HumanSignal") ?? yaml.HumanSignal,
            Verified = config.GetValue<double?>("Verified") ?? yaml.Verified,
            EarlyExit = config.GetValue<double?>("EarlyExit") ?? yaml.EarlyExit
        };
    }

    private static ConfidenceDefaults MergeConfidence(ConfidenceDefaults yaml, IConfigurationSection config)
    {
        return new ConfidenceDefaults
        {
            Neutral = config.GetValue<double?>("Neutral") ?? yaml.Neutral,
            BotDetected = config.GetValue<double?>("BotDetected") ?? yaml.BotDetected,
            HumanIndicated = config.GetValue<double?>("HumanIndicated") ?? yaml.HumanIndicated,
            StrongSignal = config.GetValue<double?>("StrongSignal") ?? yaml.StrongSignal,
            HighThreshold = config.GetValue<double?>("HighThreshold") ?? yaml.HighThreshold,
            LowThreshold = config.GetValue<double?>("LowThreshold") ?? yaml.LowThreshold,
            EscalationThreshold = config.GetValue<double?>("EscalationThreshold") ?? yaml.EscalationThreshold
        };
    }

    private static TimingDefaults MergeTiming(TimingDefaults yaml, IConfigurationSection config)
    {
        return new TimingDefaults
        {
            TimeoutMs = config.GetValue<int?>("TimeoutMs") ?? yaml.TimeoutMs,
            CacheRefreshSec = config.GetValue<int?>("CacheRefreshSec") ?? yaml.CacheRefreshSec,
            RateWindowSec = config.GetValue<int?>("RateWindowSec") ?? yaml.RateWindowSec,
            MaxRequestsPerWindow = config.GetValue<int?>("MaxRequestsPerWindow") ?? yaml.MaxRequestsPerWindow
        };
    }

    private static FeatureDefaults MergeFeatures(FeatureDefaults yaml, IConfigurationSection config)
    {
        return new FeatureDefaults
        {
            DetailedLogging = config.GetValue<bool?>("DetailedLogging") ?? yaml.DetailedLogging,
            EnableCache = config.GetValue<bool?>("EnableCache") ?? yaml.EnableCache,
            CanEarlyExit = config.GetValue<bool?>("CanEarlyExit") ?? yaml.CanEarlyExit,
            CanEscalate = config.GetValue<bool?>("CanEscalate") ?? yaml.CanEscalate
        };
    }

    private static Dictionary<string, object> MergeParameters(
        Dictionary<string, object> yaml,
        IConfigurationSection config)
    {
        var result = new Dictionary<string, object>(yaml);

        // Overlay config values
        foreach (var child in config.GetChildren())
        {
            var value = child.Value;
            if (value != null)
            {
                result[child.Key] = ParseConfigValue(value);
            }
        }

        return result;
    }

    private static object ParseConfigValue(string value)
    {
        // Try to parse as number
        if (double.TryParse(value, out var d))
            return d;
        if (int.TryParse(value, out var i))
            return i;
        if (bool.TryParse(value, out var b))
            return b;
        return value;
    }

    private static T ConvertParameter<T>(object value)
    {
        if (value is T typed)
            return typed;

        // Handle numeric conversions
        if (typeof(T) == typeof(int) && value is double dblVal)
            return (T)(object)(int)dblVal;
        if (typeof(T) == typeof(double) && value is int intVal)
            return (T)(object)(double)intVal;

        return (T)Convert.ChangeType(value, typeof(T));
    }
}

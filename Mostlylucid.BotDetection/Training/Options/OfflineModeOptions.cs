using System.ComponentModel.DataAnnotations;

namespace Mostlylucid.BotDetection.Training.Options;

/// <summary>
///     Configuration for offline pattern generation mode.
///     Observes live traffic and extracts training data.
/// </summary>
public class OfflineModeOptions
{
    /// <summary>Enable offline mode data collection</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Output directory for training data files</summary>
    public string OutputPath { get; set; } = "./training-data";

    /// <summary>Output file format</summary>
    public string FileFormat { get; set; } = "jsonl"; // jsonl or json

    /// <summary>File rotation policy</summary>
    public RotationPolicy Rotation { get; set; } = new();

    /// <summary>Observation window configuration</summary>
    public ObservationWindow Window { get; set; } = new();

    /// <summary>Features to include in training data</summary>
    public List<string> IncludeFeatures { get; set; } = new()
    {
        "ua_signature",
        "ip_signature",
        "behavior_signature",
        "fingerprint_hash",
        "request_timing",
        "path_patterns",
        "header_analysis"
    };

    /// <summary>Require manual labeling (don't auto-label)</summary>
    public bool RequireManualLabeling { get; set; } = false;

    /// <summary>Auto-labeling configuration</summary>
    public AutoLabelConfig AutoLabel { get; set; } = new();

    /// <summary>Skip paths that match these patterns</summary>
    public List<string> SkipPaths { get; set; } = new()
    {
        "/health",
        "/healthz",
        "/ping",
        "/metrics",
        "/_next/static/**",
        "/static/**",
        "/favicon.ico"
    };
}

public class RotationPolicy
{
    /// <summary>Maximum file size in bytes before rotation</summary>
    [Range(1048576, 1073741824)] // 1MB - 1GB
    public long MaxSizeBytes { get; set; } = 104857600; // 100MB

    /// <summary>Maximum file age in hours before rotation</summary>
    [Range(1, 168)] // 1 hour - 1 week
    public int MaxAgeHours { get; set; } = 24;
}

public class ObservationWindow
{
    /// <summary>Minimum requests to consider a session complete</summary>
    [Range(1, 1000)]
    public int MinRequests { get; set; } = 10;

    /// <summary>Maximum requests per session</summary>
    [Range(10, 10000)]
    public int MaxRequests { get; set; } = 100;

    /// <summary>Time window in minutes for session grouping</summary>
    [Range(1, 1440)] // 1 min - 24 hours
    public int TimeWindowMinutes { get; set; } = 30;

    /// <summary>Session timeout - close session if no activity</summary>
    [Range(1, 60)]
    public int SessionTimeoutMinutes { get; set; } = 10;
}

public class AutoLabelConfig
{
    /// <summary>High confidence threshold for bot label</summary>
    [Range(0.0, 1.0)]
    public double HighConfidenceThreshold { get; set; } = 0.95;

    /// <summary>Low confidence threshold for human label</summary>
    [Range(0.0, 1.0)]
    public double LowConfidenceThreshold { get; set; } = 0.05;

    /// <summary>Uncertain range - don't auto-label</summary>
    public double[] UncertainRange { get; set; } = { 0.4, 0.6 };
}
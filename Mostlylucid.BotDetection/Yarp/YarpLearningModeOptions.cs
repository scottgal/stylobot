using System.ComponentModel.DataAnnotations;

namespace Mostlylucid.BotDetection.Yarp;

/// <summary>
///     Configuration for YARP learning mode - collects training data without blocking.
///     Runs ALL detectors (except LLM for performance) to gather comprehensive signals.
/// </summary>
/// <remarks>
///     <para>
///         YARP Learning Mode is designed for gateway scenarios where you want to:
///         <list type="bullet">
///             <item>Collect training data from live traffic</item>
///             <item>See ALL detector signals and signatures</item>
///             <item>Generate bot signature files (JSONL)</item>
///             <item>Log comprehensive detection details to console</item>
///             <item>NOT block requests (shadow mode)</item>
///         </list>
///     </para>
///     <para>
///         This is a TRAINING/DEBUGGING mode - NOT for production blocking!
///     </para>
/// </remarks>
public class YarpLearningModeOptions
{
    /// <summary>
    ///     Enable YARP learning mode.
    ///     WARNING: This runs the full detection pipeline on EVERY request.
    ///     Only enable in non-production environments or with traffic sampling.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Output directory for bot signature files.
    ///     Files are written in JSONL format (one signature per line).
    /// </summary>
    public string OutputPath { get; set; } = "./yarp-learning-data";

    /// <summary>
    ///     Output file format: "jsonl" or "json"
    ///     JSONL is recommended for streaming and large datasets.
    /// </summary>
    public string FileFormat { get; set; } = "jsonl";

    /// <summary>
    ///     Log ALL signals and signatures to console.
    ///     Useful for real-time debugging during development.
    /// </summary>
    public bool LogToConsole { get; set; } = true;

    /// <summary>
    ///     Minimum confidence threshold to log/save signatures.
    ///     Set to 0.0 to capture all traffic (including likely humans).
    ///     Default: 0.0 (capture everything for training).
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinConfidenceToLog { get; set; } = 0.0;

    /// <summary>
    ///     Include LLM detector in learning mode.
    ///     WARNING: Very slow! Only enable for small-scale testing.
    ///     Default: false (skip LLM to reduce latency).
    /// </summary>
    public bool IncludeLlmDetector { get; set; } = false;

    /// <summary>
    ///     Include ONNX detector in learning mode.
    ///     Moderate performance impact but provides ML-based signals.
    ///     Default: true.
    /// </summary>
    public bool IncludeOnnxDetector { get; set; } = true;

    /// <summary>
    ///     Sample rate - only process N% of requests (1.0 = 100%, 0.1 = 10%).
    ///     Use sampling to reduce performance impact in high-traffic scenarios.
    ///     Default: 1.0 (process all requests).
    /// </summary>
    [Range(0.0, 1.0)]
    public double SamplingRate { get; set; } = 1.0;

    /// <summary>
    ///     File rotation policy for signature files.
    /// </summary>
    public YarpLearningRotationPolicy Rotation { get; set; } = new();

    /// <summary>
    ///     Include full HTTP context in signatures (headers, cookies, etc.).
    ///     WARNING: May contain PII! Only enable in secure environments.
    ///     Default: false (exclude sensitive data).
    /// </summary>
    public bool IncludeFullHttpContext { get; set; } = false;

    /// <summary>
    ///     Include request body in signatures (for POST/PUT requests).
    ///     WARNING: May contain PII! Only enable in secure environments.
    ///     Default: false.
    /// </summary>
    public bool IncludeRequestBody { get; set; } = false;

    /// <summary>
    ///     Paths to exclude from learning (e.g., health checks).
    ///     These paths won't be logged or saved to signature files.
    /// </summary>
    public List<string> ExcludePaths { get; set; } = new()
    {
        "/health",
        "/healthz",
        "/ping",
        "/metrics"
    };

    /// <summary>
    ///     Add timestamp to signature file names for better organization.
    ///     Example: signatures_2024-01-15.jsonl
    /// </summary>
    public bool UseTimestampedFiles { get; set; } = true;

    /// <summary>
    ///     Buffer size for signature file writes.
    ///     Higher values = better performance, but more data loss on crash.
    ///     Set to 1 to flush immediately (safest but slowest).
    ///     Default: 100.
    /// </summary>
    [Range(1, 10000)]
    public int BufferSize { get; set; } = 100;

    /// <summary>
    ///     Include raw detector outputs in signatures.
    ///     Shows exactly what each detector contributed.
    ///     Default: true.
    /// </summary>
    public bool IncludeDetectorOutputs { get; set; } = true;

    /// <summary>
    ///     Include blackboard signals in signatures.
    ///     Shows all intermediate signals during detection.
    ///     Default: true.
    /// </summary>
    public bool IncludeBlackboardSignals { get; set; } = true;
}

/// <summary>
///     File rotation policy for YARP learning mode signature files.
/// </summary>
public class YarpLearningRotationPolicy
{
    /// <summary>
    ///     Maximum file size in bytes before rotation.
    ///     Default: 100MB
    /// </summary>
    [Range(1048576, 1073741824)] // 1MB - 1GB
    public long MaxSizeBytes { get; set; } = 104857600; // 100MB

    /// <summary>
    ///     Maximum file age in hours before rotation.
    ///     Default: 24 hours (daily rotation)
    /// </summary>
    [Range(1, 168)] // 1 hour - 1 week
    public int MaxAgeHours { get; set; } = 24;

    /// <summary>
    ///     Maximum number of signature files to keep.
    ///     Oldest files are deleted when this limit is exceeded.
    ///     Set to 0 for unlimited.
    ///     Default: 30 (30 days of daily files)
    /// </summary>
    [Range(0, 365)]
    public int MaxFiles { get; set; } = 30;
}
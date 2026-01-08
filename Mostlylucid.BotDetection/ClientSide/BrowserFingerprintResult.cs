using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     Browser fingerprint data collected by the client-side script.
/// </summary>
public class BrowserFingerprintData
{
    // Basic signals
    [JsonPropertyName("tz")] public string? Timezone { get; set; }

    [JsonPropertyName("lang")] public string? Language { get; set; }

    [JsonPropertyName("langs")] public string? Languages { get; set; }

    [JsonPropertyName("platform")] public string? Platform { get; set; }

    [JsonPropertyName("cores")] public int HardwareConcurrency { get; set; }

    [JsonPropertyName("mem")] public double DeviceMemory { get; set; }

    [JsonPropertyName("touch")] public int HasTouch { get; set; }

    [JsonPropertyName("screen")] public string? ScreenResolution { get; set; }

    [JsonPropertyName("avail")] public string? AvailableResolution { get; set; }

    [JsonPropertyName("dpr")] public double DevicePixelRatio { get; set; }

    [JsonPropertyName("pdf")] public int HasPdfPlugin { get; set; }

    // Headless/automation detection
    [JsonPropertyName("webdriver")] public int WebDriver { get; set; }

    [JsonPropertyName("phantom")] public int Phantom { get; set; }

    [JsonPropertyName("nightmare")] public bool Nightmare { get; set; }

    [JsonPropertyName("selenium")] public bool Selenium { get; set; }

    [JsonPropertyName("cdc")] public int ChromeDevTools { get; set; }

    [JsonPropertyName("plugins")] public int PluginCount { get; set; }

    [JsonPropertyName("chrome")] public bool HasChromeObject { get; set; }

    [JsonPropertyName("permissions")] public string? NotificationPermission { get; set; }

    // Window consistency
    [JsonPropertyName("outerW")] public int OuterWidth { get; set; }

    [JsonPropertyName("outerH")] public int OuterHeight { get; set; }

    [JsonPropertyName("innerW")] public int InnerWidth { get; set; }

    [JsonPropertyName("innerH")] public int InnerHeight { get; set; }

    // Function integrity
    [JsonPropertyName("evalLen")] public int EvalLength { get; set; }

    [JsonPropertyName("bindNative")] public int BindIsNative { get; set; }

    // Optional WebGL
    [JsonPropertyName("glVendor")] public string? WebGLVendor { get; set; }

    [JsonPropertyName("glRenderer")] public string? WebGLRenderer { get; set; }

    // Optional Canvas
    [JsonPropertyName("canvasHash")] public string? CanvasHash { get; set; }

    // Client-calculated score
    [JsonPropertyName("score")] public int ClientScore { get; set; }

    // Timestamp
    [JsonPropertyName("ts")] public long Timestamp { get; set; }

    // Error (if collection failed)
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>
///     Processed browser fingerprint result with server-side analysis.
/// </summary>
public class BrowserFingerprintResult
{
    /// <summary>
    ///     Unique identifier for this fingerprint submission.
    /// </summary>
    public string RequestId { get; set; } = "";

    /// <summary>
    ///     Whether the browser appears to be automated/headless.
    /// </summary>
    public bool IsHeadless { get; set; }

    /// <summary>
    ///     Confidence score that this is a bot (0.0 to 1.0).
    /// </summary>
    public double HeadlessLikelihood { get; set; }

    /// <summary>
    ///     Browser integrity score (0-100, higher = more likely human).
    /// </summary>
    public int BrowserIntegrityScore { get; set; }

    /// <summary>
    ///     Fingerprint consistency score (0-100).
    /// </summary>
    public int FingerprintConsistencyScore { get; set; }

    /// <summary>
    ///     Detected automation framework, if any.
    /// </summary>
    public string? DetectedAutomation { get; set; }

    /// <summary>
    ///     Hash of the fingerprint for session correlation.
    /// </summary>
    public string FingerprintHash { get; set; } = "";

    /// <summary>
    ///     Detailed reasons for the scores.
    /// </summary>
    public List<string> Reasons { get; set; } = [];

    /// <summary>
    ///     When this fingerprint was processed.
    /// </summary>
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
///     Stores fingerprint results in memory for correlation with requests.
/// </summary>
public interface IBrowserFingerprintStore
{
    /// <summary>
    ///     Stores a fingerprint result keyed by IP hash.
    /// </summary>
    void Store(string ipHash, BrowserFingerprintResult result);

    /// <summary>
    ///     Retrieves the most recent fingerprint for an IP hash.
    /// </summary>
    BrowserFingerprintResult? Get(string ipHash);
}
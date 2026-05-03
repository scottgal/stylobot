using System.ComponentModel.DataAnnotations;

namespace Mostlylucid.BotDetection.Simulator;

/// <summary>
///     Configuration for the bot detection simulator.
///     Enables testing, training, and debugging of the detection system.
/// </summary>
public class BotSimulatorOptions
{
    /// <summary>
    ///     Enable bot simulator features.
    ///     WARNING: MUST be disabled in production! Only use in development/test environments.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Allow signature injection mode.
    ///     Feed specific signatures/signals to test detector interactions.
    /// </summary>
    public bool AllowSignatureInjection { get; set; } = true;

    /// <summary>
    ///     Allow learning mode with header overrides.
    ///     Override UA/IP/behavior via headers to train individual detectors.
    /// </summary>
    public bool AllowLearningMode { get; set; } = true;

    /// <summary>
    ///     Allow arbitrary policy execution.
    ///     Execute custom pipelines and policies for targeted testing.
    /// </summary>
    public bool AllowPolicyExecution { get; set; } = true;

    /// <summary>
    ///     Require API key for simulator access.
    ///     If set, requests must include X-Bot-Sim-ApiKey header.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    ///     Maximum pipeline execution time in milliseconds.
    ///     Prevents runaway custom pipelines.
    /// </summary>
    [Range(100, 60000)]
    public int MaxExecutionTime { get; set; } = 10000;

    /// <summary>
    ///     Include detailed execution trace in response.
    ///     Shows stage-by-stage detector results and timing.
    /// </summary>
    public bool IncludeExecutionTrace { get; set; } = true;

    /// <summary>
    ///     Enable learning/feedback in simulator modes.
    ///     Allows training pattern reputation and weight stores.
    /// </summary>
    public bool EnableLearning { get; set; } = false;

    /// <summary>
    ///     Allowed IP addresses for simulator access.
    ///     Empty list = allow from anywhere (NOT recommended).
    /// </summary>
    public List<string> AllowedIPs { get; set; } = new() { "127.0.0.1", "::1" };

    /// <summary>
    ///     Maximum JSON payload size in bytes.
    ///     Prevents abuse via large custom pipeline/policy definitions.
    /// </summary>
    [Range(1024, 1048576)] // 1KB - 1MB
    public int MaxPayloadSize { get; set; } = 102400; // 100KB

    /// <summary>
    ///     Add warning headers to all simulator responses.
    ///     Helps identify simulator-generated responses in logs.
    /// </summary>
    public bool AddWarningHeaders { get; set; } = true;

    /// <summary>
    ///     Log all simulator requests for auditing.
    /// </summary>
    public bool LogAllRequests { get; set; } = true;
}

/// <summary>
///     Simulator mode specified via X-Bot-Sim-Mode header.
/// </summary>
public enum SimulatorMode
{
    /// <summary>Not a simulator request</summary>
    None,

    /// <summary>Inject specific signatures and signals</summary>
    Signature,

    /// <summary>Override UA/IP/behavior via headers</summary>
    Learning,

    /// <summary>Execute custom pipeline/policy</summary>
    Policy
}
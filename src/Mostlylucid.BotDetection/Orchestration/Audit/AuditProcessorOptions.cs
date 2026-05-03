namespace Mostlylucid.BotDetection.Orchestration.Audit;

public sealed class AuditProcessorOptions
{
    public bool Enabled { get; set; }
    public AuditSignalRetentionOptions SignalRetention { get; set; } = new();
    public ErrorSignalAuditProcessorOptions Errors { get; set; } = new();
    public LoggerAuditSinkOptions Logger { get; set; } = new();
}

public sealed class AuditSignalRetentionOptions
{
    /// <summary>
    ///     Whether the audit context should retain signal values for processors.
    ///     When false, processors receive an empty signal dictionary unless they build
    ///     records directly from other evidence fields.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Keep all available signal keys after exclusions. When false, only keys matching
    ///     RetainedSignalPrefixes are retained.
    /// </summary>
    public bool RetainAllSignals { get; set; }

    /// <summary>
    ///     Maximum number of signals copied into the audit context. Prevents pathological
    ///     traces from bloating audit output.
    /// </summary>
    public int MaxSignalCount { get; set; } = 128;

    /// <summary>
    ///     Signal prefixes retained when RetainAllSignals is false.
    /// </summary>
    public List<string> RetainedSignalPrefixes { get; set; } =
    [
        "pipeline.",
        "detector.",
        "signal.",
        "ua.",
        "action.",
        "ephemeral.",
        "request.",
        "response.",
        "intent.",
        "threat.",
        "transport.",
        "session.",
        "cluster.",
        "reputation."
    ];

    /// <summary>
    ///     Signal prefixes never retained by the audit context.
    /// </summary>
    public List<string> ExcludedSignalPrefixes { get; set; } =
    [
        "request.body",
        "request.cookie",
        "request.authorization",
        "request.query.raw",
        "ip.address",
        "ua.raw"
    ];
}

public sealed class ErrorSignalAuditProcessorOptions
{
    public bool Enabled { get; set; }
    public string MinimumSeverity { get; set; } = "Warning";
    public List<string> SignalPrefixes { get; set; } =
    [
        "pipeline.error",
        "pipeline.exception",
        "detector.error",
        "detector.timeout",
        "signal.parse_failed",
        "ua.parse_failed",
        "ephemeral.read_failed",
        "action.policy_error",
        "llm.escalation_failed",
        "audit.processor"
    ];
}

public sealed class LoggerAuditSinkOptions
{
    public bool Enabled { get; set; } = true;
}

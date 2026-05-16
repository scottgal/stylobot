namespace Mostlylucid.BotDetection.Sidecar;

/// <summary>
///     Deployment-time configuration for the sidecar host, resolved from environment
///     variables. Detection behaviour itself is configured through the standard
///     <c>BotDetection</c> configuration section; this type only covers how the
///     process listens and the bounds it places on caller-supplied input.
/// </summary>
public sealed class SidecarOptions
{
    /// <summary>gRPC listen port (HTTP/2).</summary>
    public int Port { get; init; } = 5090;

    /// <summary>REST listen port (HTTP/1.1). Ignored when <see cref="GrpcOnly" /> is set.</summary>
    public int RestPort { get; init; } = 5091;

    /// <summary>When true, only the gRPC endpoint is exposed; no REST surface is mapped.</summary>
    public bool GrpcOnly { get; init; }

    /// <summary>
    ///     When true the listener binds all interfaces (0.0.0.0). Default is false: the
    ///     sidecar binds loopback only, matching the same-host / same-pod trust model
    ///     (a sidecar container shares the loopback interface with its app). Set
    ///     <c>STYLOBOT_BIND=any</c> only when callers reach it across a network.
    /// </summary>
    public bool BindAnyInterface { get; init; }

    /// <summary>
    ///     Escape hatch allowing the sidecar to start when bound to all interfaces with
    ///     no API keys configured. Off by default so that exposed-and-unauthenticated is
    ///     a deliberate, explicit choice rather than an accident.
    /// </summary>
    public bool AllowInsecure { get; init; }

    /// <summary>Maximum number of requests accepted in a single <c>DetectBatch</c> call.</summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>Maximum length (characters) of a <c>RenderWidget</c> Liquid template.</summary>
    public int MaxTemplateLength { get; init; } = 64 * 1024;

    /// <summary>
    ///     Maximum number of Liquid statements a single <c>RenderWidget</c> render may
    ///     execute. Bounds CPU and output size for caller-supplied templates.
    /// </summary>
    public int MaxRenderSteps { get; init; } = 10_000;

    public static SidecarOptions FromEnvironment()
    {
        var port = ParseInt("STYLOBOT_PORT", 5090);
        return new SidecarOptions
        {
            Port = port,
            RestPort = ParseInt("STYLOBOT_REST_PORT", port + 1),
            GrpcOnly = ParseBool("STYLOBOT_GRPC_ONLY"),
            BindAnyInterface = ParseBind("STYLOBOT_BIND"),
            AllowInsecure = ParseBool("STYLOBOT_ALLOW_INSECURE"),
            MaxBatchSize = ParseInt("STYLOBOT_MAX_BATCH", 100),
            MaxTemplateLength = ParseInt("STYLOBOT_MAX_TEMPLATE_LENGTH", 64 * 1024),
            MaxRenderSteps = ParseInt("STYLOBOT_MAX_RENDER_STEPS", 10_000),
        };
    }

    private static int ParseInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private static bool ParseBool(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

    // STYLOBOT_BIND: "loopback" (default, anything else) | "any" / "all" / "0.0.0.0"
    private static bool ParseBind(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return v is not null &&
               (v.Equals("any", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                v == "0.0.0.0");
    }
}

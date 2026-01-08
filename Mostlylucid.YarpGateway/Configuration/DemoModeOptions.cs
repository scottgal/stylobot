namespace Mostlylucid.YarpGateway.Configuration;

/// <summary>
/// Configuration for YARP Gateway demo mode.
/// Demo mode enables comprehensive bot detection with ALL headers passed to downstream services.
/// </summary>
public class DemoModeOptions
{
    public const string SectionName = "Gateway:DemoMode";

    /// <summary>
    /// Enable demo mode. When true, uses 'demo' policy and passes all detection headers downstream.
    /// Can be overridden with GATEWAY_DEMO_MODE environment variable.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Pass all bot detection headers to downstream cluster (not just basic headers).
    /// Includes: probabilities, contributions, reasons, processing time, etc.
    /// </summary>
    public bool PassAllHeaders { get; set; } = true;

    /// <summary>
    /// Use verbose logging in demo mode.
    /// </summary>
    public bool UseVerboseLogging { get; set; } = true;

    /// <summary>
    /// Description shown in logs when demo mode is active.
    /// </summary>
    public string Description { get; set; } = "Demo mode passes ALL bot detection headers to downstream cluster for UI display";
}

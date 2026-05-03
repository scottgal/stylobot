namespace Mostlylucid.Common.Telemetry;

/// <summary>
///     Options for configuring telemetry behavior
/// </summary>
public class TelemetryOptions
{
    /// <summary>
    ///     Gets or sets whether telemetry is enabled. Default is true.
    ///     When disabled, no activities will be created.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Gets or sets whether to record detailed attributes on activities.
    ///     Setting to false reduces telemetry data volume but provides less detail.
    /// </summary>
    public bool RecordDetailedAttributes { get; set; } = true;

    /// <summary>
    ///     Gets or sets whether to record exception details in activities.
    /// </summary>
    public bool RecordExceptions { get; set; } = true;
}
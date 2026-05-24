namespace Mostlylucid.BotDetection.UI.Configuration;

/// <summary>
///     Tuning knobs for the Behavioral Evolution panel on the signature-detail page.
///     Bound from <c>BotDetection:Dashboard:BehavioralEvolution</c>. Every magic number
///     the partial reads at render time lives here -- the partial emits the values into
///     <c>data-*</c> attributes which the inline script reads at boot.
/// </summary>
public sealed class BehavioralEvolutionOptions
{
    /// <summary>Most-recent sessions overlaid on the radar. Older sessions still appear in the right-column list but are not drawn.</summary>
    public int MaxOverlaySessions { get; set; } = 5;

    /// <summary>Half-life of ghost opacity in minutes. A session this many minutes old renders at half its peak intensity.</summary>
    public double HalfLifeMinutes { get; set; } = 240;

    public double MinGhostOpacity { get; set; } = 0.03;
    public double MaxGhostOpacity { get; set; } = 0.65;
    public double MinStrokeOpacity { get; set; } = 0.20;
    public double FocusFillOpacity { get; set; } = 0.20;
    public double FocusStrokeOpacity { get; set; } = 1.00;
    public double CurrentStrokeWidth { get; set; } = 2.5;
    public double GhostStrokeWidth { get; set; } = 1.0;

    /// <summary>Milliseconds between session focuses while Play is running.</summary>
    public int PlayIntervalMs { get; set; } = 1500;

    /// <summary>Number of concentric reference rings on the radar.</summary>
    public int RingCount { get; set; } = 4;

    /// <summary>Ghosts older than this shift from teal to slate-blue, signalling "different era".</summary>
    public double BlueShiftAfterMinutes { get; set; } = 720;

    public bool ShowQuadrantBackgrounds { get; set; } = true;
    public bool ShowAxisLegend { get; set; } = true;
    public bool ShowMetricsStrip { get; set; } = true;
}

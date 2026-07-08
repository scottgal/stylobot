using System.Reflection;

namespace Stylobot.Gateway.Health;

/// <summary>
/// Options for the active upstream health monitor.
/// Bound from <c>BotDetection:UpstreamHealth</c> in appsettings.json.
/// </summary>
public class UpstreamHealthMonitorOptions
{
    public const string SectionName = "BotDetection:UpstreamHealth";

    // Default candidate paths live in an embedded resource (data, not a C#
    // string list) and are read once. Loaded newline-delimited to stay AOT-safe
    // (no JSON serializer / reflection). Operators replace the whole list via
    // config; ApplyDefaults only fills in when config supplied none.
    private static readonly string[] DefaultCandidatePaths = LoadDefaultCandidatePaths();

    private static string[] LoadDefaultCandidatePaths()
    {
        var asm = typeof(UpstreamHealthMonitorOptions).Assembly;
        var name = Array.Find(
            asm.GetManifestResourceNames(),
            n => n.EndsWith("upstream-health-candidate-paths.txt", StringComparison.Ordinal));

        if (name is null)
            return [];

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
            return [];

        using var reader = new StreamReader(stream);
        var paths = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            paths.Add(trimmed);
        }

        return paths.ToArray();
    }

    /// <summary>
    /// Whether active upstream health probing is enabled.
    /// Default: false (opt-in).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Ordered candidate paths probed on each upstream to discover a health endpoint.
    /// Operator-overridable; first path that returns 2xx wins.
    /// When overridden via configuration, replaces the default list.
    /// </summary>
    public string[] CandidatePaths { get; set; } = [];

    /// <summary>
    /// How often (in seconds) each discovered upstream health endpoint is probed.
    /// Default: 60.
    /// </summary>
    public int ProbeIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// HTTP request timeout (in milliseconds) for each probe attempt.
    /// Default: 2000.
    /// </summary>
    public int ProbeTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// Apply default candidate paths if none were configured.
    /// Called by PostConfigure during options binding.
    /// </summary>
    public void ApplyDefaults()
    {
        if (CandidatePaths.Length == 0)
        {
            CandidatePaths = DefaultCandidatePaths;
        }
    }
}

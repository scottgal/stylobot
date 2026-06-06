namespace Mostlylucid.BotDetection.Observability.Signals;

public sealed class BlackboardSignalLogOptions
{
    /// <summary>Disable to keep the bridge from subscribing at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>If non-empty, only signals whose name starts with one of these prefixes are emitted.</summary>
    public IList<string> IncludePrefixes { get; set; } = new List<string>();

    /// <summary>Signals whose name starts with one of these prefixes are dropped. Applied after IncludePrefixes.</summary>
    public IList<string> ExcludePrefixes { get; set; } = new List<string> { "trace.", "debug." };
}

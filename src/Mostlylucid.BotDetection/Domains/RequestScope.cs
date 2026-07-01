namespace Mostlylucid.BotDetection.Domains;

/// <summary>
/// The (domain, host) pair every visit row carries. Domain is eTLD+1;
/// host is the full lowercased port-stripped request Host header.
/// </summary>
public readonly record struct RequestScope(string Domain, string Host)
{
    public static RequestScope Unknown { get; } = new("unknown", "unknown");
}
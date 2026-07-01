namespace Mostlylucid.BotDetection.Domains;

/// <summary>Configuration for <see cref="DomainNormalizer"/>.</summary>
public sealed class DomainNormalizerOptions
{
    public const string SectionName = "BotDetection:DomainNormalizer";

    /// <summary>Tag returned for RFC1918 / localhost / cluster-internal traffic.</summary>
    public string LocalTag { get; set; } = "local";

    /// <summary>Tag returned when the host is null / empty / unparseable.</summary>
    public string UnknownTag { get; set; } = "unknown";

    /// <summary>
    /// Bases treated as "hosting-provider" — the full label under the provider is the
    /// registrable name (myapp.azurewebsites.net, not azurewebsites.net).
    /// </summary>
    public List<string> HostingProviderExceptions { get; set; } = new()
    {
        "azurewebsites.net", "vercel.app", "netlify.app", "herokuapp.com"
    };

    /// <summary>
    /// Optional override path for the Public Suffix List. When null, the embedded resource is used.
    /// </summary>
    public string? PublicSuffixListPath { get; set; }
}
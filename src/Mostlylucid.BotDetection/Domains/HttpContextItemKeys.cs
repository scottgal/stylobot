namespace Mostlylucid.BotDetection.Domains;

/// <summary>Well-known HttpContext.Items keys used across the detection pipeline.</summary>
public static class HttpContextItemKeys
{
    public const string Domain = "BotDetection:Domain";
    public const string Host = "BotDetection:Host";
    public const string RequestScope = "BotDetection:RequestScope";
}
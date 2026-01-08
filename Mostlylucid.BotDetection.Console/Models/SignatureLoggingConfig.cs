namespace Mostlylucid.BotDetection.Console.Models;

/// <summary>
///     Configuration for signature logging
/// </summary>
public record SignatureLoggingConfig
{
    public bool Enabled { get; init; }
    public double MinConfidence { get; init; }
    public bool PrettyPrintJsonLd { get; init; }
    public required string SignatureHashKey { get; init; }
    public bool LogRawPii { get; init; } // DEFAULT: false (zero-PII)
}
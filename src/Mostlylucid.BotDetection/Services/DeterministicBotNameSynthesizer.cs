namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Async wrapper around <see cref="FingerprintNameComposer"/> for the
///     <see cref="IBotNameSynthesizer"/> contract. The composer is the single source of truth
///     for naming; this class just plugs it into the LLM-fallback path (see
///     <c>LlmDescriptionCoordinator.ProcessSignatureAsync</c>).
///
///     The matcher (<c>FingerprintMatchContributor</c>) calls
///     <see cref="FingerprintNameComposer.Compose"/> directly on fingerprint allocation, so
///     the per-request response header and dashboard surface never wait on this async path.
/// </summary>
public sealed class DeterministicBotNameSynthesizer : IBotNameSynthesizer
{
    public bool IsReady => true;

    public Task<string?> SynthesizeBotNameAsync(
        IReadOnlyDictionary<string, object?> signals,
        CancellationToken ct = default)
        => Task.FromResult<string?>(FingerprintNameComposer.Compose(signals));

    public Task<(string? Name, string? Description)> SynthesizeDetailedAsync(
        IReadOnlyDictionary<string, object?> signals,
        string? context = null,
        CancellationToken ct = default)
    {
        var name = FingerprintNameComposer.Compose(signals);
        var desc = GenerateDescription(signals);
        return Task.FromResult<(string?, string?)>((name, desc));
    }

    private static string? GenerateDescription(IReadOnlyDictionary<string, object?> signals)
    {
        var parts = new List<string>();

        var family = GetString(signals, "ua.family");
        if (!string.IsNullOrEmpty(family))
            parts.Add($"User-Agent family: {family}");

        var intent = GetString(signals, "intent.category");
        if (!string.IsNullOrEmpty(intent))
            parts.Add($"Intent: {intent}");

        var pageRate = GetDouble(signals, "waveform.page_rate");
        if (pageRate > 0)
            parts.Add($"Request rate: {pageRate:F1} pages/min");

        var assetRatio = GetDouble(signals, "waveform.asset_ratio");
        if (assetRatio < 0.01)
            parts.Add("No static assets loaded (headless behavior)");

        return parts.Count > 0 ? string.Join(". ", parts) + "." : null;
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> signals, string key)
        => signals.TryGetValue(key, out var v) && v is string s && !string.IsNullOrEmpty(s) ? s : null;

    private static double GetDouble(IReadOnlyDictionary<string, object?> signals, string key)
        => signals.TryGetValue(key, out var v) && v is double d ? d : 0;
}

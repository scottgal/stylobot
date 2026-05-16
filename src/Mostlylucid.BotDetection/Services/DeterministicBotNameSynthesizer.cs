using Mostlylucid.BotDetection.Models;

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
        => Task.FromResult<string?>(FingerprintNameComposer.Compose(StripNulls(signals)));

    public Task<(string? Name, string? Description)> SynthesizeDetailedAsync(
        IReadOnlyDictionary<string, object?> signals,
        string? context = null,
        CancellationToken ct = default)
    {
        var clean = StripNulls(signals);
        var name = FingerprintNameComposer.Compose(clean);
        var desc = GenerateDescription(clean);
        return Task.FromResult<(string?, string?)>((name, desc));
    }

    /// <summary>
    ///     Adapt the interface's nullable-value dictionary to the composer's non-nullable
    ///     shape. The interface predates the composer; the matcher (hot path) passes
    ///     <c>state.Signals</c> directly. The async LLM-fallback path that uses this
    ///     synthesizer pays one copy at the boundary instead.
    /// </summary>
    private static IReadOnlyDictionary<string, object> StripNulls(IReadOnlyDictionary<string, object?> signals)
        => signals.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);

    private static string? GenerateDescription(IReadOnlyDictionary<string, object> signals)
    {
        var parts = new List<string>();

        var family = FingerprintNameComposer.GetString(signals, SignalKeys.UserAgentFamily);
        if (!string.IsNullOrEmpty(family))
            parts.Add($"User-Agent family: {family}");

        var intent = FingerprintNameComposer.GetString(signals, SignalKeys.IntentCategory);
        if (!string.IsNullOrEmpty(intent))
            parts.Add($"Intent: {intent}");

        var pageRate = FingerprintNameComposer.GetDouble(signals, "waveform.page_rate");
        if (pageRate > 0)
            parts.Add($"Request rate: {pageRate:F1} pages/min");

        var assetRatio = FingerprintNameComposer.GetDouble(signals, "waveform.asset_ratio");
        if (assetRatio < 0.01)
            parts.Add("No static assets loaded (headless behavior)");

        return parts.Count > 0 ? string.Join(". ", parts) + "." : null;
    }
}

using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Drives the shared <c>_FingerprintShape.cshtml</c> partial used by both
///     the home-card view component and the signature-detail page. Encapsulates
///     the 7-bucket centroid radar so the two surfaces render identical
///     polygons from one piece of view code.
/// </summary>
public sealed record FingerprintShapeViewModel(
    FingerprintRadarShape? Shape,
    bool IsBot,
    int Size = 140,
    string? UserAgent = null);

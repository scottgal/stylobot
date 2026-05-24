using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for <c>_BehavioralEvolution.cshtml</c>. All numeric tuning
///     comes from <see cref="BehavioralEvolutionOptions"/> and is emitted
///     into <c>data-*</c> attributes on the root element, which the inline
///     render script reads at boot.
/// </summary>
public sealed class BehavioralEvolutionModel
{
    public required string SignatureId { get; init; }
    public required string BasePath { get; init; }
    public required string CspNonce { get; init; }
    public required BehavioralEvolutionOptions Options { get; init; }
}

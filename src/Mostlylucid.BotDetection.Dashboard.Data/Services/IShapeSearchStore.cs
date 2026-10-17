using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>HNSW vector similarity search + preset management. Implemented by commercial plugin.</summary>
public interface IShapeSearchStore
{
    Task<InvestigationResult> SearchByShapeAsync(ShapeSearchFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<InvestigationPreset>> GetPresetsAsync(CancellationToken ct = default);
}

namespace Mostlylucid.BotDetection.UI.Configuration;

/// <summary>
///     Debug/seeding option. When enabled, exposes /api/v1/bdf/harvest which streams
///     persisted BDFs (already captured by normal detection flow) as NDJSON for
///     offline archetype-YAML authoring via the `archetype-from-bdf` console command.
///     OFF by default; never turn on in production.
/// </summary>
public sealed class BdfHarvestOptions
{
    public bool Enabled { get; set; } = false;
    public int MaxBatchSize { get; set; } = 500;
}

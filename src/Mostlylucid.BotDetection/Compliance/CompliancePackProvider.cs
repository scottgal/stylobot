using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Compliance;

/// <summary>Provides the currently active compliance pack.</summary>
public interface ICompliancePackProvider
{
    CompliancePack ActivePack { get; }
    IReadOnlyList<CompliancePack> AvailablePacks { get; }
    void SetActivePack(string packId);
}

/// <summary>In-memory provider for FOSS -- loads packs at startup, active pack set via config.</summary>
public sealed class InMemoryCompliancePackProvider : ICompliancePackProvider
{
    private readonly IReadOnlyList<CompliancePack> _packs;
    private CompliancePack _active;
    private readonly ILogger<InMemoryCompliancePackProvider> _logger;

    public InMemoryCompliancePackProvider(
        string activePackId,
        ILogger<InMemoryCompliancePackProvider> logger,
        IReadOnlyList<CompliancePack>? additionalPacks = null)
    {
        _logger = logger;
        var embedded = CompliancePackLoader.LoadEmbeddedPacks(logger);
        _packs = additionalPacks is not null
            ? embedded.Concat(additionalPacks).DistinctBy(p => p.Id).ToList()
            : embedded;

        _active = _packs.FirstOrDefault(p => p.Id == activePackId)
                  ?? _packs.FirstOrDefault(p => p.Id == "balanced-default")
                  ?? _packs[0];

        _logger.LogInformation("Active compliance pack: {PackId}", _active.Id);
    }

    public CompliancePack ActivePack => _active;
    public IReadOnlyList<CompliancePack> AvailablePacks => _packs;

    public void SetActivePack(string packId)
    {
        var pack = _packs.FirstOrDefault(p => p.Id == packId);
        if (pack is null)
        {
            _logger.LogWarning("Compliance pack {PackId} not found, keeping {Current}", packId, _active.Id);
            return;
        }
        _logger.LogInformation("Switching compliance pack: {From} -> {To}", _active.Id, pack.Id);
        _active = pack;
    }
}

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Streams persisted BDF documents (one per signature) tagged with verdict /
///     risk-band / self-declared-bot metadata so an operator can run the
///     <c>archetype-from-bdf</c> console command against the NDJSON output and
///     emit YAML archetype seeds without re-running detection.
///     Exposed via /api/v1/bdf/harvest, off unless <see cref="Configuration.BdfHarvestOptions"/>
///     has Enabled set; pure read-side, never mutates the event store.
/// </summary>
public sealed class BdfHarvestService
{
    private readonly IDashboardEventStore _eventStore;
    private readonly BdfExportService _exportService;
    private readonly IBotListDatabase? _botList;
    private readonly ILogger<BdfHarvestService> _logger;

    public BdfHarvestService(
        IDashboardEventStore eventStore,
        BdfExportService exportService,
        ILogger<BdfHarvestService> logger,
        IBotListDatabase? botList = null)
    {
        _eventStore = eventStore;
        _exportService = exportService;
        _logger = logger;
        _botList = botList;
    }

    /// <summary>
    ///     Iterates the most-recent <paramref name="limit"/> signatures (newest first
    ///     by underlying store ordering), exports each to a <see cref="BdfExportDocument"/>,
    ///     and yields a <see cref="HarvestedBdf"/> tagged with verdict + risk-band +
    ///     self-declared-bot indicator. Signatures older than <paramref name="since"/>
    ///     are skipped. The <paramref name="labelBy"/> parameter is reserved for a
    ///     future nearest-archetype labelling pass (currently unused but accepted so
    ///     the endpoint surface is stable).
    /// </summary>
    public async IAsyncEnumerable<HarvestedBdf> HarvestAsync(
        DateTime? since,
        int limit,
        string? labelBy,
        [EnumeratorCancellation] CancellationToken ct)
    {
        _ = labelBy; // reserved: nearest-archetype labelling pass
        List<DashboardSignatureEvent> signatures;
        try
        {
            signatures = await _eventStore.GetSignaturesAsync(limit, 0, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BDF harvest: failed to read signatures");
            yield break;
        }

        foreach (var sigEvent in signatures)
        {
            if (ct.IsCancellationRequested) yield break;

            if (since is not null && sigEvent.Timestamp < since.Value)
                continue;

            BdfExportDocument? doc = null;
            try
            {
                doc = await _exportService.ExportAsync(sigEvent.PrimarySignature);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BDF harvest: export failed for {Signature}", sigEvent.PrimarySignature);
            }

            if (doc is null) continue;

            bool? selfDeclaredBot = null;
            if (_botList is not null && !string.IsNullOrWhiteSpace(sigEvent.BotName))
            {
                try
                {
                    selfDeclaredBot = await _botList.IsBot(sigEvent.BotName!, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "BDF harvest: bot-list lookup failed for {BotName}", sigEvent.BotName);
                }
            }

            var tags = new Dictionary<string, object?>
            {
                ["verdict"] = sigEvent.IsKnownBot ? "bot" : "human",
                ["riskBand"] = sigEvent.RiskBand,
                ["selfDeclaredBot"] = selfDeclaredBot,
                ["nearestArchetype"] = null,
            };

            yield return new HarvestedBdf(doc, tags);
        }
    }
}

/// <summary>
///     One harvested BDF + the tags that drive grouping in the console converter.
///     Serialized as one NDJSON line per signature.
/// </summary>
public sealed record HarvestedBdf(BdfExportDocument Bdf, Dictionary<string, object?> Tags);

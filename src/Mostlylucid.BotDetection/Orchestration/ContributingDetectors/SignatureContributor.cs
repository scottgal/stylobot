using System.Text.Json;
using Mostlylucid.BotDetection.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Unified signature + identity contributor. Computes the canonical PrimarySignature,
///     collects header hashes for progressive identity resolution, and writes everything
///     to the blackboard so downstream contributors use one identity key.
///
///     Priority 1 - runs before everything else in Wave 0.
///     No YAML config needed (no tunable parameters).
///     No trigger conditions (runs on every request).
///     Returns empty contributions (informational only - no bot/human signal).
/// </summary>
public class SignatureContributor : ContributingDetectorBase
{
    private readonly ILogger<SignatureContributor> _logger;
    private readonly MultiFactorSignatureService _signatureService;
    private readonly HeaderHashCollector _headerHashCollector;

    public SignatureContributor(
        ILogger<SignatureContributor> logger,
        MultiFactorSignatureService signatureService,
        HeaderHashCollector headerHashCollector)
    {
        _logger = logger;
        _signatureService = signatureService;
        _headerHashCollector = headerHashCollector;
    }

    public override string Name => "Signature";
    public override int Priority => 1;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributionSignals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var signatures = _signatureService.GenerateSignatures(state.HttpContext);

            // Write the canonical signature to the blackboard
            if (!string.IsNullOrEmpty(signatures.PrimarySignature))
            {
                state.WriteSignal(SignalKeys.PrimarySignature, signatures.PrimarySignature);
                contributionSignals[SignalKeys.PrimarySignature] = signatures.PrimarySignature;
            }

            // Write the full MultiFactorSignatures to the blackboard
            state.WriteSignal("signature.multifactor", signatures);
            contributionSignals["signature.multifactor"] = signatures;

            // Collect header hashes for progressive identity resolution.
            // These get persisted per session for retroactive stability analysis.
            var headerHashes = _headerHashCollector.CollectHashes(state.HttpContext.Request);
            if (headerHashes.Count > 0)
            {
                var hashesJson = JsonSerializer.Serialize(headerHashes);
                state.WriteSignal(SignalKeys.HeaderHashes, hashesJson);
                contributionSignals[SignalKeys.HeaderHashes] = hashesJson;
            }

            // Store in HttpContext.Items for backward compat with post-detection middleware
            state.HttpContext.Items[BotDetectionMiddleware.SignatureSetKey] = signatures;
            state.HttpContext.Items[BotDetectionMiddleware.PrimarySignatureKey] = signatures.PrimarySignature;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error computing unified signature");
        }

        if (contributionSignals.Count == 0)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        // Emit a neutral contribution carrying the signature signals so DetectionLedger.MergedSignals
        // (which only walks per-contribution Signals) exposes them to downstream consumers
        // (RequestPersistenceService, dashboard projections, ResponseHeader middleware).
        return Task.FromResult<IReadOnlyList<DetectionContribution>>(new[]
        {
            new DetectionContribution
            {
                DetectorName = Name,
                Category = "identity",
                ConfidenceDelta = 0.0,
                Weight = 0.0,
                Salience = 0.0,
                Reason = "signature computed",
                Priority = Priority,
                Signals = contributionSignals
            }
        });
    }
}
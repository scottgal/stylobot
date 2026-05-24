using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Grouping;

/// <summary>
///     Default <see cref="IBehaviouralGrouper"/>. Phase 1 covers the
///     bot-only gate (tier 0) plus the friendly bot-name tier (6) and
///     raw-signature fallback (7). Tiers 1-5 (Identity / Cluster / Vector
///     / Sequence / Subnet) are no-ops in this phase and will be wired
///     in subsequent phases as the dependencies are made injectable.
/// </summary>
/// <remarks>
///     The hierarchy is intentional: even partial coverage produces
///     correct group keys today -- a bot that should group by cluster
///     simply falls through to friendly-name or raw signature until
///     tier 2 lands. Display surfaces don't need to know which tier
///     produced the key; they just render the chip.
/// </remarks>
public sealed class BehaviouralGrouper : IBehaviouralGrouper
{
    private readonly IOptionsMonitor<GroupingOptions> _options;
    private readonly ILogger<BehaviouralGrouper> _logger;

    public BehaviouralGrouper(
        IOptionsMonitor<GroupingOptions> options,
        ILogger<BehaviouralGrouper> logger)
    {
        _options = options;
        _logger = logger;
    }

    public GroupKey Resolve(GroupingInput input)
    {
        var opts = _options.CurrentValue;

        if (!opts.Enabled)
            return new GroupKey(GroupKeySource.RawSignature, input.Signature, "(grouping disabled)");

        // ============================================================
        //  Tier 0 -- BOT-ONLY GATE (non-overridable, FIRST rule)
        //
        //  Humans never group. A human signature ALWAYS returns its own
        //  row regardless of behavioural similarity to anyone else.
        //  This pre-check is the foundational invariant of the grouper.
        // ============================================================
        if (!IsBotShaped(input, opts))
            return new GroupKey(GroupKeySource.Human, input.Signature, "(human)");

        // ============================================================
        //  Tier 1 -- Identity (metastable fingerprint)
        //  Phase 2 wires the FingerprintMatchContributor signal; for
        //  now, accept a pre-populated FingerprintId on the input if
        //  the caller already has it.
        // ============================================================
        if (opts.EnableIdentityTier && !string.IsNullOrEmpty(input.FingerprintId))
        {
            return new GroupKey(
                GroupKeySource.Identity,
                input.FingerprintId,
                $"Fingerprint {Short(input.FingerprintId)}");
        }

        // ============================================================
        //  Tier 2 -- Leiden cluster
        //  Phase 2 wires the IBotClusterReader. For now, accept a
        //  pre-populated ClusterId on the input.
        // ============================================================
        if (opts.EnableClusterTier && !string.IsNullOrEmpty(input.ClusterId))
        {
            return new GroupKey(
                GroupKeySource.Cluster,
                input.ClusterId,
                $"Cluster {Short(input.ClusterId)}");
        }

        // Tiers 3-5 (vector cosine / sequence centroid / subnet rotation)
        // wired in Phase 3-4 of the plan; placeholders only here so the
        // hierarchy reads correctly when those tiers come online.

        // ============================================================
        //  Tier 6 -- Friendly bot name (the current dashboard rule)
        // ============================================================
        if (opts.EnableFriendlyNameTier && IsFriendlyBotName(input))
        {
            // Custom name wins -- operator-pinned label is the strongest
            // friendly signal we have.
            var name = !string.IsNullOrEmpty(input.CustomBotName)
                ? input.CustomBotName!
                : input.BotName!;
            return new GroupKey(GroupKeySource.FriendlyBotName, name, name);
        }

        // ============================================================
        //  Tier 7 -- Raw signature (no behavioural collapse)
        // ============================================================
        return new GroupKey(GroupKeySource.RawSignature, input.Signature, "");
    }

    /// <summary>
    ///     The bot-only gate. Mirror of <c>IsBotShaped</c> from the plan,
    ///     intentionally conservative -- a suspected-bot row with
    ///     probability under threshold falls through to Human (no group).
    /// </summary>
    private static bool IsBotShaped(GroupingInput input, GroupingOptions opts)
    {
        if (input.IsBot is false) return false;
        if (input.BotProbability < opts.MinBotProbability) return false;

        // RiskBand on the input comes through as a string ("VeryHigh", "High",
        // "Elevated", ...) because callers use view-model types. Map back
        // and compare to the floor.
        var band = ParseRiskBand(input.RiskBand);
        if (band < opts.MinRiskBand) return false;

        return true;
    }

    private static RiskBand ParseRiskBand(string? value) =>
        value switch
        {
            "VeryHigh" => RiskBand.VeryHigh,
            "High" => RiskBand.High,
            "Medium" => RiskBand.Medium,
            "Elevated" => RiskBand.Elevated,
            "Low" => RiskBand.Low,
            "VeryLow" => RiskBand.VeryLow,
            _ => RiskBand.Unknown
        };

    /// <summary>
    ///     Mirror of the existing <c>WidgetRenderHelpers.IsGroupableIdentity</c>
    ///     rule, lifted here so the grouper owns the friendly-name policy.
    ///     A custom bot name is always trusted; otherwise the UA must
    ///     have produced a real BotName AND a BotType that's not a
    ///     human-sharing category like Tool / Unknown.
    /// </summary>
    private static bool IsFriendlyBotName(GroupingInput input)
    {
        if (!string.IsNullOrEmpty(input.CustomBotName)) return true;
        if (string.IsNullOrEmpty(input.BotName)) return false;
        if (string.IsNullOrEmpty(input.BotType)) return false;
        return !string.Equals(input.BotType, "Tool", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(input.BotType, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string id) =>
        id.Length <= 8 ? id : id[..8];
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Enforcement;

/// <summary>
///     Pins <see cref="BlockResponseGate.ShouldBlock"/>'s per-endpoint override.
///     A <see cref="BotPolicyAttribute"/>(BlockThreshold=…) on the matched
///     endpoint replaces the global <see cref="BotDetectionOptions.BotThreshold"/>
///     for that endpoint only, so an internal endpoint marked permissive
///     (BlockThreshold = 0.95) stays reachable by an edge-case visitor the
///     global 0.7 would refuse (CLAUDE.md: never skip detection; use a high
///     per-endpoint BlockThreshold instead of a bypass).
/// </summary>
public class BlockResponseGateTests
{
    private static BlockResponseGate Gate(BotDetectionOptions? options = null) =>
        new(Options.Create(options ?? new BotDetectionOptions()),
            NullLogger<BlockResponseGate>.Instance);

    private static AggregatedEvidence Evidence(double probability) => new()
    {
        BotProbability = probability,
        Confidence = 0.9,
        RiskBand = RiskBand.Elevated,
    };

    [Fact]
    public void NoOverride_BlocksAtGlobalThreshold()
    {
        // Global BotThreshold = 0.7; 0.8 clears it.
        var (block, action) = Gate().ShouldBlock(Evidence(0.8));

        Assert.True(block);
        Assert.Equal(BotBlockAction.StatusCode, action);
    }

    /// <summary>
    ///     BotType.Internal is documented as an ABSOLUTE guarantee — InternalTrustOptions
    ///     calls it "the LAN-traffic carve-out that routes a request to logonly instead of
    ///     throttling/blocking it (admin curl, dashboard self-poll, sidecar loopback, the
    ///     BDF runner)", and CLAUDE.md says Internal is "classified, listed, and filterable
    ///     but never throttled".
    ///
    ///     <para>
    ///         It was implemented in exactly ONE place: the
    ///         <c>BotTypeActionPolicies["Internal"] = "logonly"</c> mapping consulted by
    ///         PostDetectionActionGate. This gate — the one that actually returns 403 — had
    ///         no knowledge of Internal at all, so ANY path returning NoOverride from that
    ///         gate landed here and blocked on probability alone. An absolute guarantee with
    ///         one enforcement point and a fall-through around it is not a guarantee; this
    ///         pins it at the gate that does the blocking.
    ///     </para>
    ///
    ///     <para>
    ///         Safe to trust: Internal is derived from the real TCP peer
    ///         (<c>Connection.RemoteIpAddress</c> + InternalTrust config, see IpAtom), never
    ///         from a forwarded header, precisely so it cannot be spoofed into a bypass.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Internal_traffic_is_never_blocked_even_at_certainty()
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 1.0,          // maximally bot-like
            Confidence = 1.0,
            RiskBand = RiskBand.VeryHigh,
            PrimaryBotType = BotType.Internal,
        };

        var outcome = await Gate().HandleAsync(new DefaultHttpContext(), evidence);

        Assert.Equal(BlockResponseOutcome.Continue, outcome);
    }

    /// <summary>
    ///     Parity guard: the carve-out must be keyed on Internal specifically, not a
    ///     blanket softening of the gate. An identical non-Internal request at the same
    ///     probability must still block — otherwise the test above would pass on a gate
    ///     that had simply stopped blocking anything.
    /// </summary>
    [Fact]
    public async Task Non_internal_traffic_at_the_same_probability_still_blocks()
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 1.0,
            Confidence = 1.0,
            RiskBand = RiskBand.VeryHigh,
            PrimaryBotType = BotType.Scraper,
        };

        var outcome = await Gate().HandleAsync(new DefaultHttpContext(), evidence);

        Assert.Equal(BlockResponseOutcome.Blocked, outcome);
    }

    [Fact]
    public void PermissiveEndpoint_DoesNotBlockBelowItsThreshold()
    {
        // The core fix: an endpoint marked BlockThreshold = 0.95 must NOT
        // block a 0.8 visitor even though the global bar (0.7) is cleared.
        var (block, _) = Gate().ShouldBlock(Evidence(0.8), endpointBlockThreshold: 0.95);

        Assert.False(block);
    }

    [Fact]
    public void PermissiveEndpoint_StillBlocksAboveItsThreshold()
    {
        var (block, action) = Gate().ShouldBlock(Evidence(0.96), endpointBlockThreshold: 0.95);

        Assert.True(block);
        Assert.Equal(BotBlockAction.StatusCode, action);
    }

    [Fact]
    public void StrictEndpoint_BlocksBelowGlobalThreshold()
    {
        // A stricter override (0.5) blocks a 0.6 visitor the global 0.7 would allow.
        var (block, _) = Gate().ShouldBlock(Evidence(0.6), endpointBlockThreshold: 0.5);

        Assert.True(block);
    }

    [Fact]
    public void UnsetAttributeDefault_FallsBackToGlobal()
    {
        // The attribute default is -1 ("use policy default"); HandleAsync maps
        // that to null, so the global threshold decides.
        var attr = new BotPolicyAttribute("strict");
        Assert.Equal(-1, attr.BlockThreshold);

        double? mapped = attr is { BlockThreshold: >= 0 } ? attr.BlockThreshold : null;
        Assert.Null(mapped);

        var (block, _) = Gate().ShouldBlock(Evidence(0.8), endpointBlockThreshold: mapped);
        Assert.True(block); // 0.8 >= global 0.7
    }
}

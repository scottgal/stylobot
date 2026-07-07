using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Pass-through action policy. Marks the request as explicitly allowed by an
///     endpoint-policy rule and lets the pipeline continue to bot-detection and
///     downstream middleware unchanged. Detection still runs — this only signals
///     that the pre-detection endpoint gate has no objection.
/// </summary>
/// <remarks>
///     Typical use: the built-in default health-probe rule
///     (<c>Path:/health*, Source:internal, Action:allow</c>) uses this so that
///     internal health checks from loopback/RFC-1918 callers pass through
///     without any response modification while still being classified by the
///     detection pipeline.
///     Operators may reference <c>allow</c> in their own endpoint-policy rules
///     for the same purpose.
/// </remarks>
public sealed class AllowActionPolicy : IActionPolicy
{
    /// <summary>Shared singleton — the policy carries no per-instance state.</summary>
    public static readonly AllowActionPolicy Instance = new();

    /// <inheritdoc />
    public string Name => "allow";

    /// <inheritdoc />
    public ActionType ActionType => ActionType.LogOnly;

    /// <inheritdoc />
    public PolicyIntent Intent => PolicyIntent.Pass;

    /// <inheritdoc />
    public Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ActionResult.Allowed("endpoint-policy allow rule"));
}

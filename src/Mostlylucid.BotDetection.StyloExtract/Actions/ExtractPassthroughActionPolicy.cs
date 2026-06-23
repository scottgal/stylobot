using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.StyloExtract.Actions;

/// <summary>
/// Explicit no-op action policy. Returns <see cref="ActionResult.Allowed"/> immediately
/// without invoking the extractor. Lets an endpoint rule override an inherited rule with
/// "do nothing for this specific path".
/// </summary>
public sealed class ExtractPassthroughActionPolicy : IActionPolicy
{
    /// <inheritdoc />
    public string Name => "extract-passthrough";

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Custom;

    /// <inheritdoc />
    public PolicyIntent Intent => PolicyIntent.Pass;

    /// <inheritdoc />
    public Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ActionResult.Allowed("extract-passthrough: no-op"));
}

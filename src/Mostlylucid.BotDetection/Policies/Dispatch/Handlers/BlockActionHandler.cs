using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.Block"/> by writing the same 403 JSON
///     envelope the legacy <c>HandleBlockedRequest</c> uses, plus an
///     <c>X-Stylobot-Policy</c> header attributing the response to the
///     matched rule. Returns <see cref="PolicyDispatchResult.Handled"/> so
///     the middleware short-circuits the legacy enforcement path.
///
///     <para>
///         Body shape (field names + JSON keys) MUST match the legacy
///         <c>BlockedResponse</c> record (Error / Reason / RiskScore /
///         Policy). Downstream consumers (gateways, monitoring) parse this
///         envelope; changing the shape here is a breaking change.
///     </para>
/// </summary>
public sealed class BlockActionHandler : IPolicyActionHandler
{
    /// <summary>Header attributing a block response back to the policy stack and rule id.</summary>
    public const string PolicyHeader = "X-Stylobot-Policy";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public Type HandledAction => typeof(PolicyAction.Block);

    /// <inheritdoc />
    public async Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers[PolicyHeader] = $"rule-block ({rule.Id})";
        context.Response.ContentType = "application/json";

        // Mirror the legacy BlockedResponse record verbatim. RiskScore is 0
        // here because the policy stack does not need a numeric risk to fire
        // -- the rule's predicate already made the decision.
        var body = new PolicyBlockedResponse(
            Error: "Access denied",
            Reason: "Request blocked by policy rule",
            RiskScore: 0d,
            Policy: rule.Id.ToString());

        await context.Response
            .WriteAsJsonAsync(body, JsonOptions, ct)
            .ConfigureAwait(false);

        return PolicyDispatchResult.Handled;
    }

    /// <summary>
    ///     Wire shape of the 403 envelope written by <see cref="BlockActionHandler"/>.
    ///     Property names mirror the legacy <c>BlockedResponse</c> record so
    ///     downstream consumers parse both envelopes identically.
    /// </summary>
    internal sealed record PolicyBlockedResponse(
        string Error,
        string Reason,
        double RiskScore,
        string Policy);
}

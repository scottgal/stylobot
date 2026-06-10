using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.Challenge"/> by writing the same 403 +
///     <c>X-Bot-Challenge</c> envelope the legacy <c>HandleBlockedRequest</c>
///     uses for its <c>Challenge</c> branch, plus an <c>X-Stylobot-Policy</c>
///     header attributing the response to the matched rule. Returns
///     <see cref="PolicyDispatchResult.Handled"/> so the middleware
///     short-circuits the legacy enforcement path.
///
///     <para>
///         The body shape (Error / ChallengeType / RiskScore) mirrors the
///         legacy <c>ChallengeResponse</c> record verbatim. The challenge
///         kind comes from the <see cref="PolicyAction.Challenge.Kind"/>
///         field on the rule.
///     </para>
/// </summary>
public sealed class ChallengeActionHandler : IPolicyActionHandler
{
    /// <summary>Header indicating a challenge response. Same key the legacy path uses.</summary>
    public const string ChallengeHeader = "X-Bot-Challenge";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public Type HandledAction => typeof(PolicyAction.Challenge);

    /// <inheritdoc />
    public async Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct)
    {
        var challenge = action as PolicyAction.Challenge;
        var kind = string.IsNullOrWhiteSpace(challenge?.Kind) ? "challenge" : challenge!.Kind;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers[ChallengeHeader] = "required";
        context.Response.Headers[BlockActionHandler.PolicyHeader] = $"rule-challenge ({rule.Id})";
        context.Response.ContentType = "application/json";

        var body = new PolicyChallengeResponse(
            Error: "Challenge required",
            ChallengeType: kind,
            RiskScore: 0d);

        await context.Response
            .WriteAsJsonAsync(body, JsonOptions, ct)
            .ConfigureAwait(false);

        return PolicyDispatchResult.Handled;
    }

    /// <summary>
    ///     Wire shape of the 403 challenge envelope. Property names mirror
    ///     the legacy <c>ChallengeResponse</c> record so downstream consumers
    ///     parse both envelopes identically.
    /// </summary>
    internal sealed record PolicyChallengeResponse(
        string Error,
        string ChallengeType,
        double RiskScore);
}

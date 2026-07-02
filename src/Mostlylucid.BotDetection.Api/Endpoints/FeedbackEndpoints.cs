using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Visitor "flag as wrong" capture. The upstream site forwards the click here so
///     the gateway (the enforcement component) owns the store — the site never writes
///     it directly (feedback_upstream_owns_no_stylobot_state).
/// </summary>
public static class FeedbackEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/feedback")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Feedback")
            .WithApiBotPolicy();

        group.MapPost("", HandleFlag).WithName("PostDetectionFeedback");

        return endpoints;
    }

    private static async Task<Results<Ok<SingleResponse<bool>>, ProblemHttpResult>> HandleFlag(
        [FromBody] DetectionFeedbackRecord feedback,
        [FromServices] IDetectionFeedbackStore? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Detection feedback store");
        var ok = await store.RecordFlagAsync(feedback, ct);
        return ApiEndpointHelpers.Single(ok);
    }
}

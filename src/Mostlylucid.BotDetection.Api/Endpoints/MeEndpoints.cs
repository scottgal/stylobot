using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/me", HandleMe)
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithName("GetMe")
            .WithTags("Account")
            .WithMetadata(new Mostlylucid.BotDetection.Attributes.BotPolicyAttribute("default") { BlockThreshold = 0.95 });

        return endpoints;
    }

    private static Results<Ok<SingleResponse<ApiKeyContextDto>>, ProblemHttpResult> HandleMe(HttpContext httpContext)
    {
        var keyContext = httpContext.Items["BotDetection.ApiKeyContext"] as ApiKeyContext;
        if (keyContext is null)
        {
            return TypedResults.Problem(title: "No API key context", statusCode: 401,
                type: "https://stylo.bot/errors/no-api-key");
        }

        return TypedResults.Ok(new SingleResponse<ApiKeyContextDto>
        {
            Data = ApiKeyContextDto.From(keyContext),
            Meta = new ResponseMeta()
        });
    }
}

public sealed record ApiKeyContextDto(
    string KeyName,
    IReadOnlyList<string> DisabledDetectors,
    IReadOnlyDictionary<string, double> WeightOverrides,
    string? DetectionPolicyName,
    string? ActionPolicyName,
    IReadOnlyList<string> Tags,
    bool DisablesAllDetectors)
{
    public static ApiKeyContextDto From(ApiKeyContext c) => new(
        c.KeyName, c.DisabledDetectors, c.WeightOverrides,
        c.DetectionPolicyName, c.ActionPolicyName, c.Tags, c.DisablesAllDetectors);
}

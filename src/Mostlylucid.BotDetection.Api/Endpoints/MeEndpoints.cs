using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
            .WithOpenApi();

        return endpoints;
    }

    private static IResult HandleMe(HttpContext httpContext)
    {
        var keyContext = httpContext.Items["BotDetection.ApiKeyContext"] as ApiKeyContext;
        if (keyContext is null)
        {
            return Results.Problem(title: "No API key context", statusCode: 401,
                type: "https://stylobot.net/errors/no-api-key");
        }

        return Results.Ok(new SingleResponse<object>
        {
            Data = new
            {
                keyContext.KeyName, keyContext.DisabledDetectors, keyContext.WeightOverrides,
                keyContext.DetectionPolicyName, keyContext.ActionPolicyName, keyContext.Tags,
                keyContext.DisablesAllDetectors
            },
            Meta = new ResponseMeta()
        });
    }
}

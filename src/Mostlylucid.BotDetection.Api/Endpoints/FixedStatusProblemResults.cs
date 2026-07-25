using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Base for a <see cref="ProblemDetails"/> response whose HTTP status is fixed in the TYPE,
///     not just the runtime instance.
///     <para>
///         <see cref="ProblemHttpResult"/> (what <c>TypedResults.Problem(..., statusCode: N)</c>
///         returns) only carries its status on the constructed INSTANCE. ASP.NET Core's built-in
///         OpenAPI generator documents a <c>Results&lt;...&gt;</c> union by calling
///         <see cref="IEndpointMetadataProvider.PopulateMetadata"/> once per declared TYPE
///         argument, at endpoint-registration time - before any request ever runs and therefore
///         before any instance (and its runtime status code) exists. <c>ProblemHttpResult</c> can't
///         report a status it doesn't statically know, so every endpoint returning it documented
///         only its 200 response and silently dropped the real 503/400/401 - verified live via
///         <c>/openapi/v1.json</c> showing <c>responses: {"200"}</c> only for endpoints that
///         demonstrably also return 503.
///     </para>
///     <para>
///         Baking the status into the TYPE (mirroring how the framework's own <c>NotFound</c> /
///         <c>BadRequest</c> / <c>NoContent</c> results work - each is its own type with a
///         hardcoded status) lets the generator document it correctly. Runtime behaviour is
///         byte-identical to before: same status code, same ProblemDetails JSON body, same
///         content type - this class only ever delegates to a real <see cref="ProblemHttpResult"/>.
///     </para>
/// </summary>
internal abstract class FixedStatusProblemHttpResult : IResult
{
    private readonly ProblemHttpResult _problem;

    protected FixedStatusProblemHttpResult(ProblemHttpResult problem) => _problem = problem;

    public Task ExecuteAsync(HttpContext httpContext) => _problem.ExecuteAsync(httpContext);

    protected static void AddProblemMetadata(EndpointBuilder builder, int statusCode)
        => builder.Metadata.Add(new ProducesResponseTypeMetadata(statusCode, typeof(ProblemDetails), ["application/problem+json"]));
}

/// <summary>
///     503 Service Unavailable, ProblemDetails body. Returned when a backing store/feature isn't
///     registered - see <see cref="ApiEndpointHelpers.StoreUnavailable"/>.
/// </summary>
internal sealed class ServiceUnavailableHttpResult : FixedStatusProblemHttpResult, IEndpointMetadataProvider
{
    public const int StatusCode = StatusCodes.Status503ServiceUnavailable;

    private ServiceUnavailableHttpResult(ProblemHttpResult problem) : base(problem)
    {
    }

    public static ServiceUnavailableHttpResult FromTitle(string title)
        => new((ProblemHttpResult)TypedResults.Problem(title, statusCode: StatusCode));

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
        => AddProblemMetadata(builder, StatusCode);
}

/// <summary>
///     400 Bad Request, ProblemDetails body (title/detail/type). For validation failures that
///     aren't the per-field <c>BadRequest&lt;ApiError&gt;</c> shape used elsewhere in this project.
/// </summary>
internal sealed class BadRequestProblemHttpResult : FixedStatusProblemHttpResult, IEndpointMetadataProvider
{
    public const int StatusCode = StatusCodes.Status400BadRequest;

    private BadRequestProblemHttpResult(ProblemHttpResult problem) : base(problem)
    {
    }

    public static BadRequestProblemHttpResult From(string title, string? detail = null, string? type = null)
        => new((ProblemHttpResult)TypedResults.Problem(detail: detail, title: title, statusCode: StatusCode, type: type));

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
        => AddProblemMetadata(builder, StatusCode);
}

/// <summary>401 Unauthorized, ProblemDetails body.</summary>
internal sealed class UnauthorizedProblemHttpResult : FixedStatusProblemHttpResult, IEndpointMetadataProvider
{
    public const int StatusCode = StatusCodes.Status401Unauthorized;

    private UnauthorizedProblemHttpResult(ProblemHttpResult problem) : base(problem)
    {
    }

    public static UnauthorizedProblemHttpResult From(string title, string? type = null)
        => new((ProblemHttpResult)TypedResults.Problem(title: title, statusCode: StatusCode, type: type));

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
        => AddProblemMetadata(builder, StatusCode);
}

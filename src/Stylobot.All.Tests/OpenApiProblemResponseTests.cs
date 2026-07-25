using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stylobot.All.Tests;

/// <summary>
///     Regression coverage for a confirmed bug: ~20 handlers under
///     src/Mostlylucid.BotDetection.Api/Endpoints/ returned the framework's
///     <c>ProblemHttpResult</c> (via <c>ApiEndpointHelpers.StoreUnavailable</c> or a raw
///     <c>TypedResults.Problem(...)</c>) as one arm of their <c>Results&lt;...&gt;</c> union for
///     503/400/401 outcomes. <c>ProblemHttpResult</c> only carries its status code on the runtime
///     INSTANCE, but ASP.NET Core's built-in OpenAPI generator documents a
///     <c>Results&lt;...&gt;</c> union by calling <c>IEndpointMetadataProvider.PopulateMetadata</c>
///     on each declared TYPE argument at endpoint-registration time - before any request runs and
///     therefore before any instance (and its runtime status) exists. The generated
///     <c>/api/v1/openapi.json</c> silently showed <c>responses: {"200"}</c> only for every one of
///     these endpoints, even though they demonstrably also return 503/400/401.
///     <para>
///         Fixed by giving each status its own fixed-status result type
///         (<c>ServiceUnavailableHttpResult</c> / <c>BadRequestProblemHttpResult</c> /
///         <c>UnauthorizedProblemHttpResult</c> in
///         <c>Mostlylucid.BotDetection.Api.Endpoints.FixedStatusProblemResults</c>) that implements
///         <c>IEndpointMetadataProvider</c> with the status hardcoded in the type - mirroring how
///         the framework's own <c>NotFound</c> / <c>BadRequest</c> / <c>NoContent</c> results work.
///         Runtime behaviour (status code, ProblemDetails JSON body, content type) is unchanged;
///         only the declared TYPE changed, which is what the generator actually inspects.
///     </para>
/// </summary>
public class OpenApiProblemResponseTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OpenApiProblemResponseTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StyloBot:Api:EnableOpenApi"] = "true",
                });
            });
        });
    }

    private async Task<JsonElement> GetOpenApiDocumentAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static List<string> ResponseCodes(JsonElement doc, string path, string method)
    {
        var op = doc.GetProperty("paths").GetProperty(path).GetProperty(method);
        return op.GetProperty("responses").EnumerateObject().Select(p => p.Name).ToList();
    }

    // Before the fix, every one of these documented ONLY "200" - the handler's real
    // 503/400/401 outcome (verified live via /openapi/v1.json against the Demo host) was
    // silently dropped because ProblemHttpResult can't report a status it doesn't statically
    // know at metadata-population time.
    [Theory]
    [InlineData("/api/v1/clusters", "get", "503")]
    [InlineData("/api/v1/config/manifests", "get", "503")]
    [InlineData("/api/v1/sessions/recent", "get", "503")]
    [InlineData("/api/v1/routes", "get", "503")]
    [InlineData("/api/v1/routes/name", "put", "503")]
    [InlineData("/api/v1/routes/name", "delete", "503")]
    [InlineData("/api/v1/metrics/timeseries", "get", "503")]
    [InlineData("/api/v1/metrics/latest", "get", "503")]
    [InlineData("/api/v1/site-health/history", "get", "503")]
    [InlineData("/api/v1/me", "get", "401")]
    [InlineData("/api/v1/detect/batch", "post", "400")]
    public async Task OpenApiDocument_DocumentsNon200Response(string path, string method, string expectedCode)
    {
        var doc = await GetOpenApiDocumentAsync();
        var codes = ResponseCodes(doc, path, method);

        Assert.Contains(expectedCode, codes);
        Assert.True(codes.Count > 1,
            $"{method.ToUpperInvariant()} {path} only documents [{string.Join(",", codes)}] - " +
            "expected the 200 plus at least one error response.");
    }

    [Fact]
    public async Task ServiceUnavailableResponse_DocumentsProblemDetailsSchema()
    {
        var doc = await GetOpenApiDocumentAsync();
        var response503 = doc.GetProperty("paths").GetProperty("/api/v1/clusters").GetProperty("get")
            .GetProperty("responses").GetProperty("503");

        var schemaRef = response503
            .GetProperty("content")
            .GetProperty("application/problem+json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();

        Assert.Equal("#/components/schemas/ProblemDetails", schemaRef);
    }
}

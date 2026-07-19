using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Endpoints;

namespace Mostlylucid.BotDetection.Test.Endpoints;

public sealed class OperationalEndpointAuthorizationTests
{
    [Theory]
    [InlineData("/_sb/metrics/snapshot")]
    [InlineData("/admin/persistence-stats")]
    public void Operational_endpoints_require_the_api_key_policy(string route)
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        app.MapMetricsSnapshotEndpoints();
        app.MapPersistenceStatsEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == route);

        var authorization = endpoint.Metadata.GetMetadata<IAuthorizeData>();
        Assert.NotNull(authorization);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorization.Policy);
    }
}

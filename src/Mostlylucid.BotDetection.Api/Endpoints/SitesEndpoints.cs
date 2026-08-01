using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Read-only exposure of <see cref="SiteMapOptions"/> (<c>BotDetection:Sites</c>) --
///     the operator-declared "which domains does this deployment protect" list. This is
///     the single source of truth for domain identity; it is deliberately NOT derived
///     from live YARP route introspection or observed Host headers, both of which
///     reflect routing-infrastructure state rather than operator intent.
/// </summary>
public static class SitesEndpoints
{
    public static IEndpointRouteBuilder MapSitesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/sites")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Sites")
            .WithApiBotPolicy();

        group.MapGet("", HandleList)
            .WithName("ListSites");

        return endpoints;
    }

    private static Ok<SiteListResponse> HandleList(IOptions<SiteMapOptions> options)
    {
        var opts = options.Value;
        var domains = opts.Domains.Select(SiteDomainDto.FromRule).ToList();
        return TypedResults.Ok(new SiteListResponse(opts.DefaultProfile, domains));
    }
}

public sealed record SiteListResponse(string DefaultProfile, IReadOnlyList<SiteDomainDto> Domains);

public sealed record SiteDomainDto(string Host, string Profile, string? Domain, bool IsWildcard)
{
    public static SiteDomainDto FromRule(SiteMapRule rule) =>
        new(rule.Host, rule.Profile, rule.Domain, rule.Host.Contains('*', StringComparison.Ordinal));
}

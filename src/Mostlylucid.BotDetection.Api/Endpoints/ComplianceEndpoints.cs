using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Compliance;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Read-only compliance status served by the enforcement component (the gateway
///     runs the compliance stack). An upstream dashboard host reads this over REST
///     instead of owning any compliance store or connection
///     (feedback_upstream_owns_no_stylobot_state).
/// </summary>
public static class ComplianceEndpoints
{
    public static IEndpointRouteBuilder MapComplianceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/compliance")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Compliance")
            .WithApiBotPolicy();

        group.MapGet("/status", HandleStatus).WithName("GetComplianceStatus");

        return endpoints;
    }

    private static Ok<SingleResponse<ComplianceStatusDto>> HandleStatus(
        HttpContext http,
        [FromServices] ICompliancePackProvider? packs = null)
    {
        string? activeId = null;
        string? activeName = null;
        if (packs is not null)
        {
            var active = packs.ActivePack;
            activeId = active.Id;
            activeName = active.Name;
        }

        // Guardian count is a status-strip tile. Guardians are a commercial type, so
        // count them reflectively (same shape the dashboard middleware uses) and treat
        // it as best-effort — if the type can't be resolved (e.g. AOT trimming, or a
        // FOSS gateway with no compliance pack) the count is simply 0.
        var guardianCount = 0;
        try
        {
            var guardianType = System.Type.GetType(
                "Stylobot.Commercial.Compliance.Guardians.IComplianceGuardian, Stylobot.Commercial.Compliance");
            if (guardianType is not null)
                guardianCount = http.RequestServices.GetServices(guardianType).Count();
        }
        catch
        {
            // best-effort — leave count at 0.
        }

        return ApiEndpointHelpers.Single(new ComplianceStatusDto
        {
            ActivePackId = activeId,
            ActivePackName = activeName,
            GuardianCount = guardianCount
        });
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Data.Sources;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Read-only view over <see cref="IFetchSourceRegistry"/> — every external download this
///     process makes, its config, and its persisted last-success/last-failure state. This is the
///     ONLY way an upstream host (admin.stylo.bot) sees this data; it never owns a fetch-source
///     store of its own (feedback_upstream_owns_no_stylobot_state / feedback_website_never_owns_or_modifies_data).
///     <see cref="FetchSourceApiModel.HealthState"/> is computed server-side at read
///     (<see cref="FetchSourceStatus.GetHealthState"/>) so the consuming UI never has to
///     re-implement the never-attempted/healthy/stale/failing logic itself.
/// </summary>
public static class FetchSourceEndpoints
{
    public static IEndpointRouteBuilder MapFetchSourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/fetch-sources")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Fetch Sources")
            .WithApiBotPolicy();

        group.MapGet("", HandleList).WithName("GetFetchSources");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<FetchSourceApiModel>>, ServiceUnavailableHttpResult>> HandleList(
        [FromServices] IFetchSourceRegistry? registry,
        CancellationToken ct = default)
    {
        if (registry is null) return ApiEndpointHelpers.StoreUnavailable("Fetch source registry");

        var now = DateTimeOffset.UtcNow;
        var statuses = await registry.GetAllAsync(ct);
        var models = statuses.Select(s => FetchSourceApiModel.From(s, now)).ToArray();

        return ApiEndpointHelpers.Paginated(models, models.Length);
    }
}

/// <summary>Wire shape for one <see cref="FetchSourceStatus"/> — enums as strings for a stable JSON contract.</summary>
public sealed record FetchSourceApiModel(
    string Id,
    string DisplayName,
    string? Url,
    bool Enabled,
    string Purpose,
    string? Licence,
    string Cadence,
    string FailureMode,
    string? OnDiskLocation,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    bool HasLiveState,
    string HealthState,
    IReadOnlyList<string>? GroupedSourceIds)
{
    public static FetchSourceApiModel From(FetchSourceStatus status, DateTimeOffset now)
        => new(
            status.Id, status.DisplayName, status.Url, status.Enabled, status.Purpose, status.Licence,
            status.Cadence, status.FailureMode.ToString(), status.OnDiskLocation,
            status.LastSuccessUtc, status.LastFailureUtc, status.HasLiveState,
            status.GetHealthState(now).ToString(), status.GroupedSourceIds);
}

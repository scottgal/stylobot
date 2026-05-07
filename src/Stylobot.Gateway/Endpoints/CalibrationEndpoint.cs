using Stylobot.Gateway.Data;
using Stylobot.Gateway.Services;

namespace Stylobot.Gateway.Endpoints;

public record CalibrationResponse
{
    public long TotalAnalyzed { get; init; }
    public double CollectionPeriodHours { get; init; }
    public Dictionary<string, long> ScoreDistribution { get; init; } = new();
    public List<ThresholdSimRow> ThresholdSimulation { get; init; } = new();
    public double? RecommendedThreshold { get; init; }
    public string? RecommendationReason { get; init; }
    public int QueueDepth { get; init; }
    public long TotalDropped { get; init; }
}

public static class CalibrationEndpoint
{
    public static IEndpointRouteBuilder MapCalibrationEndpoints(
        this IEndpointRouteBuilder endpoints,
        string adminPath)
    {
        var group = endpoints.MapGroup(adminPath).WithTags("Calibration");

        group.MapGet("/calibration", GetCalibrationAsync)
            .WithName("GetCalibration")
            .WithSummary("Profile mode calibration data and threshold recommendation");

        group.MapPost("/calibration/reset", ResetCalibrationAsync)
            .WithName("ResetCalibration")
            .WithSummary("Clear all calibration data to start a fresh collection period");

        return endpoints;
    }

    public static async Task<IResult> GetCalibrationAsync(
        ProfileCalibrationStore store,
        ProfileAnalysisChannel channel,
        CancellationToken ct)
    {
        var dist = await store.GetScoreDistributionAsync(ct);
        var sim = await store.GetThresholdSimulationAsync(ct);
        var rec = ProfileCalibrationStore.GetRecommendedThresholdAsync(dist);

        return Results.Ok(new CalibrationResponse
        {
            TotalAnalyzed = dist.TotalAnalyzed,
            CollectionPeriodHours = dist.CollectionPeriodHours,
            ScoreDistribution = dist.Buckets,
            ThresholdSimulation = sim,
            RecommendedThreshold = rec?.Threshold,
            RecommendationReason = rec?.Reason,
            QueueDepth = channel.QueueDepth,
            TotalDropped = channel.TotalDropped,
        });
    }

    private static async Task<IResult> ResetCalibrationAsync(
        ProfileCalibrationStore store,
        CancellationToken ct)
    {
        await store.ResetAsync(ct);
        return Results.Ok(new { message = "Calibration data cleared." });
    }
}

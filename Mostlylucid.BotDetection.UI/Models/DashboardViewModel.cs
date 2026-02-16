using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Strongly-typed view model for the dashboard Razor view.
/// </summary>
public sealed class DashboardViewModel
{
    public required StyloBotDashboardOptions Options { get; init; }
    public required string CspNonce { get; init; }
    public required string YourDetectionJson { get; init; }
    public required string SummaryJson { get; init; }
    public required string DetectionsJson { get; init; }
    public required string SignaturesJson { get; init; }
    public required string CountriesJson { get; init; }
    public required string ClustersJson { get; init; }
}

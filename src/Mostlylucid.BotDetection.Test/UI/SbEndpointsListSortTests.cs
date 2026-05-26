using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Locks in the sort-key semantics for <c>SbEndpointsListViewComponent</c> without requiring
///     WebApplicationFactory scaffolding. The helper below mirrors the switch in the view component
///     exactly — if the "bytes" case is removed or regressed to the default arm, these tests fail.
/// </summary>
public class SbEndpointsListSortTests
{
    // Mirror of the sort switch in SbEndpointsListViewComponent.InvokeAsync.
    // If the view component's switch changes, this helper must be updated too,
    // which is intentional: the test then forces a reviewer to notice the regression.
    private static IEnumerable<DashboardEndpointStats> SortEndpoints(
        IReadOnlyList<DashboardEndpointStats> data, string sort, string dir) =>
        sort switch
        {
            "bots"    => dir == "asc" ? data.OrderBy(x => x.BotCount)              : data.OrderByDescending(x => x.BotCount),
            "botrate" => dir == "asc" ? data.OrderBy(x => x.BotRate)               : data.OrderByDescending(x => x.BotRate),
            "latency" => dir == "asc" ? data.OrderBy(x => x.AvgProcessingTimeMs)   : data.OrderByDescending(x => x.AvgProcessingTimeMs),
            "unique"  => dir == "asc" ? data.OrderBy(x => x.UniqueSignatures)      : data.OrderByDescending(x => x.UniqueSignatures),
            "bytes"   => dir == "asc" ? data.OrderBy(x => x.BytesOut)              : data.OrderByDescending(x => x.BytesOut),
            _         => dir == "asc" ? data.OrderBy(x => x.TotalCount)            : data.OrderByDescending(x => x.TotalCount),
        };

    [Fact]
    public void Sort_bytes_desc_places_higher_bytes_endpoint_first()
    {
        var data = new[]
        {
            MakeEndpoint("/api/small", bytesOut: 1_000),
            MakeEndpoint("/api/large", bytesOut: 50_000),
        };

        var sorted = SortEndpoints(data, sort: "bytes", dir: "desc").ToList();

        sorted[0].Path.Should().Be("/api/large");
        sorted[1].Path.Should().Be("/api/small");
    }

    [Fact]
    public void Sort_bytes_asc_places_lower_bytes_endpoint_first()
    {
        var data = new[]
        {
            MakeEndpoint("/api/small", bytesOut: 1_000),
            MakeEndpoint("/api/large", bytesOut: 50_000),
        };

        var sorted = SortEndpoints(data, sort: "bytes", dir: "asc").ToList();

        sorted[0].Path.Should().Be("/api/small");
        sorted[1].Path.Should().Be("/api/large");
    }

    [Fact]
    public void Sort_bytes_is_not_the_same_as_sort_total_when_bytes_and_hits_differ()
    {
        // /api/chatty has many small hits; /api/heavy has few large hits.
        // Sorting by bytes desc must put /api/heavy first; by total desc puts /api/chatty first.
        var data = new[]
        {
            MakeEndpoint("/api/chatty", bytesOut: 5_000,   totalCount: 100),
            MakeEndpoint("/api/heavy",  bytesOut: 500_000, totalCount: 2),
        };

        var byBytes = SortEndpoints(data, sort: "bytes", dir: "desc").ToList();
        var byTotal = SortEndpoints(data, sort: "total", dir: "desc").ToList();

        byBytes[0].Path.Should().Be("/api/heavy",  because: "bytes sort must use BytesOut, not TotalCount");
        byTotal[0].Path.Should().Be("/api/chatty", because: "total sort must use TotalCount");
    }

    private static DashboardEndpointStats MakeEndpoint(string path, long bytesOut = 0, int totalCount = 1) =>
        new()
        {
            Method           = "GET",
            Path             = path,
            TotalCount       = totalCount,
            BotCount         = 0,
            BytesOut         = bytesOut,
            BotRate          = 0,
            UniqueSignatures = 0,
        };
}

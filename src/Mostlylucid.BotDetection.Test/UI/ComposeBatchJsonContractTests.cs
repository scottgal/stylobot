using System.Text.Json;
using FluentAssertions;
using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression coverage for the bug where the FOSS gateway (<c>stylobot --enable-api</c>)
///     crashed on STARTUP with a <c>JsonTypeInfo metadata for type 'DashboardBatchRequest'
///     was not provided</c> error.
///
///     <para>
///     The <c>POST /api/v1/compose-batch</c> endpoint takes <c>[FromBody] DashboardBatchRequest</c>
///     and returns <c>SingleResponse&lt;DashboardDatasetBundle&gt;</c>. The minimal-API endpoint
///     factory resolves the body parameter's <c>JsonTypeInfo</c> against the configured resolver
///     <em>at startup</em>, while building the request-delegate. <see cref="StyloBotJsonContext"/>
///     is source-gen-only (no reflection fallback, because the gateway publishes Native AOT), so a
///     type absent from the context does not degrade to reflection: it throws and fails the whole
///     pipeline build. compose-batch (29183d77) shipped without registering its request/response
///     types, so <c>--enable-api</c> was dead on arrival on the AOT binary. It escaped every existing
///     smoke: <c>verify-aot.sh</c> runs the gateway WITHOUT <c>--enable-api</c>, and the Demo uses
///     reflection-based JSON.
///     </para>
///
///     <para>
///     <see cref="DashboardBatchRequest_ResolvesInContext"/> reproduces the exact startup call
///     (<c>GetTypeInfo(typeof(DashboardBatchRequest))</c>); the round-trip tests prove the request
///     parses and the response serializes through source-gen without reflection.
///     </para>
/// </summary>
public class ComposeBatchJsonContractTests
{
    private static DashboardBatchRequest BuildRequest() => new(
        StartTime: new DateTime(2026, 07, 15, 0, 0, 0, DateTimeKind.Utc),
        EndTime: new DateTime(2026, 07, 15, 1, 0, 0, DateTimeKind.Utc),
        Datasets: new[]
        {
            new DatasetRequest(DatasetKind.SummaryStats),
            new DatasetRequest(DatasetKind.TimeBuckets, TopN: 20, BucketMinutes: 30),
            new DatasetRequest(DatasetKind.DegradationHistory)
        },
        AudienceFilter: "bots",
        ProbMin: 0.7,
        Domains: new[] { "example.com" });

    /// <summary>
    ///     The exact resolution the minimal-API endpoint factory does at startup for the
    ///     <c>[FromBody] DashboardBatchRequest</c> parameter. Pre-fix this returned null (the type
    ///     was absent from the source-gen context) and the pipeline build threw, taking down
    ///     <c>--enable-api</c>.
    /// </summary>
    [Fact]
    public void DashboardBatchRequest_ResolvesInContext()
    {
        StyloBotJsonContext.Default.GetTypeInfo(typeof(DashboardBatchRequest))
            .Should().NotBeNull(
                "the compose-batch endpoint resolves this [FromBody] type at startup; a null here " +
                "is the --enable-api startup crash");
    }

    /// <summary>
    ///     The response wrapper the endpoint returns. Resolving it proves the whole
    ///     <see cref="DashboardDatasetBundle"/> graph (summary, time buckets, bot aggregate, geo,
    ///     endpoints, degradations) is source-gen-covered for the AOT gateway.
    /// </summary>
    [Fact]
    public void ComposeBatchResponse_ResolvesInContext()
    {
        StyloBotJsonContext.Default.GetTypeInfo(typeof(SingleResponse<DashboardDatasetBundle>))
            .Should().NotBeNull(
                "compose-batch returns SingleResponse<DashboardDatasetBundle>; a null here throws " +
                "on the first request through the AOT gateway");
    }

    [Fact]
    public void DashboardBatchRequest_Roundtrips_ThroughSourceGenContext()
    {
        var original = BuildRequest();

        var json = JsonSerializer.Serialize(original, typeof(DashboardBatchRequest), StyloBotJsonContext.Default);
        var roundTripped = (DashboardBatchRequest?)JsonSerializer.Deserialize(
            json, typeof(DashboardBatchRequest), StyloBotJsonContext.Default);

        roundTripped.Should().NotBeNull();
        roundTripped!.Datasets.Should().HaveCount(3);
        roundTripped.Datasets[1].Kind.Should().Be(DatasetKind.TimeBuckets);
        roundTripped.Datasets[1].BucketMinutes.Should().Be(30);
        roundTripped.AudienceFilter.Should().Be("bots");
        roundTripped.ProbMin.Should().Be(0.7);
        roundTripped.Domains.Should().ContainSingle().Which.Should().Be("example.com");
    }

    [Fact]
    public void ComposeBatchResponse_Roundtrips_ThroughSourceGenContext()
    {
        var bundle = new DashboardDatasetBundle(
            Summary: null,
            TimeBuckets: null,
            BotAggregate: null,
            Geo: null,
            Endpoints: null);
        var payload = new SingleResponse<DashboardDatasetBundle> { Data = bundle, Meta = new ResponseMeta() };

        var json = JsonSerializer.Serialize(
            payload, typeof(SingleResponse<DashboardDatasetBundle>), StyloBotJsonContext.Default);
        var roundTripped = (SingleResponse<DashboardDatasetBundle>?)JsonSerializer.Deserialize(
            json, typeof(SingleResponse<DashboardDatasetBundle>), StyloBotJsonContext.Default);

        roundTripped.Should().NotBeNull();
        roundTripped!.Data.Should().NotBeNull();
    }
}

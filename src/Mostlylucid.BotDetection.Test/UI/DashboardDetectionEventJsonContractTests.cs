using System.Text.Json;
using FluentAssertions;
using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression coverage for the staging bug where the website's signature-detail
///     page rendered "No detection signals recorded" and "No analysis available yet"
///     even though the gateway was producing detector contributions and important
///     signals on every request.
///
///     The gateway publishes Native AOT (see Dockerfile.gateway-aot,
///     <c>IsAotPublish=true</c>), so any reflection-based serialization path in
///     <see cref="StyloBotJsonContext"/>'s graph throws
///     <c>NotSupportedException</c> at runtime when the source generator did not
///     emit a <c>JsonTypeInfo</c> for a nested property type. The bug surfaced as
///     a silent drop of every <c>DashboardDetectionEvent</c> enrichment field
///     downstream of <c>ImportantSignals</c> and
///     <c>DetectorContributions</c> (the two open generic dictionaries on the
///     event record) in the <c>/api/v1/detections</c> response — the website's
///     <c>RemoteDashboardEventStore</c> deserialized a partial event with nulls
///     where the enrichment data should have been, and the detail page rendered
///     its empty-state placeholders.
///
///     These tests round-trip a fully-populated <see cref="DashboardDetectionEvent"/>
///     through the source-gen context end-to-end so a future SYSLIB1030 regression
///     fails locally instead of silently in production.
/// </summary>
public class DashboardDetectionEventJsonContractTests
{
    /// <summary>
    ///     Serialize + deserialize a populated <see cref="DashboardDetectionEvent"/>
    ///     through the source-gen context and assert every enrichment field
    ///     survives the round trip. This is the exact path
    ///     <see cref="Api.Endpoints.ReadEndpoints"/> takes when serializing
    ///     <c>/api/v1/detections</c> responses on the AOT gateway.
    /// </summary>
    [Fact]
    public void DashboardDetectionEvent_Roundtrip_PreservesAllEnrichmentFields()
    {
        var original = BuildPopulatedEvent();

        var json = JsonSerializer.Serialize(original, StyloBotJsonContext.Default.DashboardDetectionEvent);
        var roundTripped = JsonSerializer.Deserialize(json, StyloBotJsonContext.Default.DashboardDetectionEvent);

        roundTripped.Should().NotBeNull();
        roundTripped!.RequestId.Should().Be(original.RequestId);

        roundTripped.TopReasons.Should().BeEquivalentTo(original.TopReasons,
            "TopReasons drives the Detection Signals panel on the signature-detail page");
        roundTripped.Narrative.Should().Be(original.Narrative,
            "Narrative renders as the italic line under Analysis");
        roundTripped.Description.Should().Be(original.Description,
            "Description renders as the lead line under Analysis");

        roundTripped.DetectorContributions.Should().NotBeNull(
            "DetectorContributions drives the Detector Contributions table");
        roundTripped.DetectorContributions!.Should().ContainKey("UserAgentContributor");
        roundTripped.DetectorContributions["UserAgentContributor"].Reason.Should().Be("ua family mismatch");

        roundTripped.ImportantSignals.Should().NotBeNull(
            "ImportantSignals drives the Signal Intelligence categories");
        roundTripped.ImportantSignals!.Should().ContainKeys("ua.family", "tls.version", "ja3.score", "headless.indicator");
    }

    /// <summary>
    ///     PaginatedResponse&lt;DashboardDetectionEvent&gt; is the actual top-level
    ///     shape <c>HandleDetections</c> returns. Roundtripping the wrapper proves
    ///     the envelope + payload pair are both AOT-safe via source-gen.
    /// </summary>
    [Fact]
    public void PaginatedResponse_OfDetection_Roundtrip_PreservesEnrichmentFields()
    {
        var payload = new PaginatedResponse<DashboardDetectionEvent>
        {
            Data = new List<DashboardDetectionEvent> { BuildPopulatedEvent() },
            Pagination = new PaginationInfo { Limit = 50, Offset = 0, Total = 1 },
            Meta = new ResponseMeta()
        };

        var json = JsonSerializer.Serialize(payload,
            StyloBotJsonContext.Default.PaginatedResponseDashboardDetectionEvent);
        var roundTripped = JsonSerializer.Deserialize(json,
            StyloBotJsonContext.Default.PaginatedResponseDashboardDetectionEvent);

        roundTripped.Should().NotBeNull();
        roundTripped!.Data.Should().HaveCount(1);

        var detection = roundTripped.Data[0];
        detection.TopReasons.Should().NotBeEmpty();
        detection.Narrative.Should().NotBeNullOrEmpty();
        detection.Description.Should().NotBeNullOrEmpty();
        detection.DetectorContributions.Should().NotBeNull().And.NotBeEmpty();
        detection.ImportantSignals.Should().NotBeNull().And.NotBeEmpty();
    }

    /// <summary>
    ///     Specifically guard the mixed-value-type case that originally tripped the
    ///     source-gen path: a string, a double, an int (via long), and a bool inside
    ///     the same Dictionary&lt;string, object&gt;. Each value-type kind goes
    ///     through STJ's polymorphic dispatch for <c>object</c> slots, and the
    ///     pre-fix context emitted SYSLIB1030 warnings + threw
    ///     <c>NotSupportedException</c> at runtime for every non-string value.
    /// </summary>
    [Fact]
    public void ImportantSignals_MixedPrimitiveTypes_Roundtrip()
    {
        var detection = new DashboardDetectionEvent
        {
            RequestId = "req-1",
            Timestamp = DateTime.UtcNow,
            IsBot = true,
            BotProbability = 1.0,
            Confidence = 0.95,
            RiskBand = "VeryHigh",
            Method = "GET",
            Path = "/",
            ImportantSignals = new Dictionary<string, object>
            {
                ["ua.family"] = "curl",
                ["ja3.score"] = 0.83,
                ["headless.indicator"] = true,
                ["session.hits"] = 42L
            }
        };

        var json = JsonSerializer.Serialize(detection,
            StyloBotJsonContext.Default.DashboardDetectionEvent);

        // Spot-check the JSON literally contains the right values — guards against
        // any future change that quietly omits the property.
        json.Should().Contain("\"ua.family\":\"curl\"");
        json.Should().Contain("\"ja3.score\":0.83");
        json.Should().Contain("\"headless.indicator\":true");

        var roundTripped = JsonSerializer.Deserialize(json,
            StyloBotJsonContext.Default.DashboardDetectionEvent);

        roundTripped!.ImportantSignals.Should().NotBeNull();
        roundTripped.ImportantSignals!.Should().ContainKeys(
            "ua.family", "ja3.score", "headless.indicator", "session.hits");
    }

    private static DashboardDetectionEvent BuildPopulatedEvent() => new()
    {
        RequestId = "test-request-id",
        Timestamp = new DateTime(2026, 06, 14, 20, 51, 52, DateTimeKind.Utc),
        IsBot = true,
        BotProbability = 1.0,
        Confidence = 0.95,
        RiskBand = "VeryHigh",
        BotType = "Scraper",
        BotName = "curl/8",
        Action = "Block",
        Method = "GET",
        Path = "/dashboard/signatures",
        StatusCode = 200,
        ProcessingTimeMs = 1.8,
        UserAgentRaw = "curl/8.5.0",
        PrimarySignature = "LbLGywIy5JHweex7_Jd3Zg",
        CountryCode = "GB",
        Narrative = "Scraper from datacenter — caught by UA + TLS fingerprint mismatch.",
        Description = "Headless curl client with cleartext-only TLS handshake.",
        TopReasons = new List<string>
        {
            "UA family mismatch (curl)",
            "TLS JA3 anomaly",
            "Missing Accept-Language",
        },
        DetectorContributions = new Dictionary<string, DashboardDetectorContribution>
        {
            ["UserAgentContributor"] = new DashboardDetectorContribution
            {
                ConfidenceDelta = 0.4,
                Contribution = 0.32,
                Reason = "ua family mismatch",
                ExecutionTimeMs = 0.12,
                Priority = 10
            },
            ["TlsFingerprintContributor"] = new DashboardDetectorContribution
            {
                ConfidenceDelta = 0.35,
                Contribution = 0.28,
                Reason = "ja3 anomaly",
                ExecutionTimeMs = 0.08,
                Priority = 12
            }
        },
        ImportantSignals = new Dictionary<string, object>
        {
            ["ua.family"] = "curl",
            ["tls.version"] = "TLS 1.2",
            ["ja3.score"] = 0.83,
            ["headless.indicator"] = true
        },
        ThreatScore = 0.78,
        ThreatBand = "High",
        RiskJustification = "Datacenter origin + UA anomaly + TLS anomaly."
    };
}
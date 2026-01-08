using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using System.Collections.Generic;

namespace Mostlylucid.BotDetection.Demo.Tests;

public static class TestHelpers
{
    public static Mock<ILogger<T>> CreateMockLogger<T>()
    {
        return new Mock<ILogger<T>>();
    }
    public static AggregatedEvidence CreateTestEvidence(double botProbability)
    {
        // Create the ledger with a request ID
        var ledger = new DetectionLedger($"test-{Guid.NewGuid():N}");

        // Add a test contribution
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "TestDetector",
            Category = "Test",
            ConfidenceDelta = botProbability > 0.5 ? 0.4 : -0.4,
            Weight = 1.0,
            Reason = "Test reason",
            Priority = 10
        });

        return new AggregatedEvidence
        {
            BotProbability = botProbability,
            Confidence = 0.9,
            RiskBand = botProbability >= 0.8 ? RiskBand.High :
                       botProbability >= 0.5 ? RiskBand.Medium : RiskBand.Low,
            Ledger = ledger,
            Signals = new Dictionary<string, object>(),
            CategoryBreakdown = new Dictionary<string, CategoryScore>
            {
                ["Test"] = new CategoryScore
                {
                    Category = "Test",
                    Score = botProbability,
                    TotalWeight = 1.0,
                    ContributionCount = 1,
                    Reasons = new List<string> { "Test reason" }
                }
            },
            ContributingDetectors = new HashSet<string> { "TestDetector" }
        };
    }

    public static Services.RequestMetadata CreateTestRequestMetadata(
        string path = "/test",
        string userAgent = "TestAgent/1.0",
        string remoteIp = "127.0.0.1")
    {
        return new Services.RequestMetadata
        {
            Path = path,
            Method = "GET",
            UserAgent = userAgent,
            RemoteIp = remoteIp,
            Protocol = "HTTP/1.1",
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = userAgent,
                ["Accept"] = "application/json"
            }
        };
    }
}

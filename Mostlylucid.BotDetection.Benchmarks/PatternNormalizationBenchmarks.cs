using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Benchmarks for pattern normalization (hot path — called per request in Wave 0)
///     and reputation updater (background path — called per learning event).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class PatternNormalizationBenchmarks
{
    private const string ChromeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string ChromeMac =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string Googlebot =
        "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    private const string CurlBot = "curl/7.88.1";

    private const string PythonScraper =
        "python-requests/2.31.0 (compatible; scraper; headless)";

    private const string IPv4 = "192.168.1.100";
    private const string IPv6 = "2001:0db8:85a3:0000:0000:8a2e:0370:7334";

    private PatternReputationUpdater _updater = null!;
    private PatternReputation _existingPattern = null!;

    [GlobalSetup]
    public void Setup()
    {
        _updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance,
            Options.Create(new BotDetectionOptions()));

        // Pre-create a pattern with some history for update benchmarks
        _existingPattern = new PatternReputation
        {
            PatternId = "ua:test1234567890",
            PatternType = "UserAgent",
            Pattern = "test",
            BotScore = 0.7,
            Support = 50,
            State = ReputationState.Suspect,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-6),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-30)
        };
    }

    // ===== Normalization =====

    [Benchmark(Description = "NormalizeUserAgent (Chrome/Win)")]
    public string NormalizeUA_ChromeWindows() => PatternNormalization.NormalizeUserAgent(ChromeWindows);

    [Benchmark(Description = "NormalizeUserAgent (Chrome/Mac)")]
    public string NormalizeUA_ChromeMac() => PatternNormalization.NormalizeUserAgent(ChromeMac);

    [Benchmark(Description = "NormalizeUserAgent (Googlebot)")]
    public string NormalizeUA_Googlebot() => PatternNormalization.NormalizeUserAgent(Googlebot);

    [Benchmark(Description = "NormalizeUserAgent (curl)")]
    public string NormalizeUA_Curl() => PatternNormalization.NormalizeUserAgent(CurlBot);

    [Benchmark(Description = "NormalizeUserAgent (Python scraper)")]
    public string NormalizeUA_PythonScraper() => PatternNormalization.NormalizeUserAgent(PythonScraper);

    // ===== Full Pattern ID Creation (normalize + hash) =====

    [Benchmark(Description = "CreateUaPatternId (Chrome/Win)")]
    public string CreateUaPatternId_Chrome() => PatternNormalization.CreateUaPatternId(ChromeWindows);

    [Benchmark(Description = "CreateIpPatternId (IPv4)")]
    public string CreateIpPatternId_IPv4() => PatternNormalization.CreateIpPatternId(IPv4);

    [Benchmark(Description = "CreateIpPatternId (IPv6)")]
    public string CreateIpPatternId_IPv6() => PatternNormalization.CreateIpPatternId(IPv6);

    // ===== Reputation Updater =====

    [Benchmark(Description = "ApplyEvidence (existing pattern)")]
    public PatternReputation ApplyEvidence_Existing()
        => _updater.ApplyEvidence(_existingPattern, "ua:test", "UserAgent", "test", 1.0);

    [Benchmark(Description = "ApplyEvidence (new pattern)")]
    public PatternReputation ApplyEvidence_New()
        => _updater.ApplyEvidence(null, "ua:new123", "UserAgent", "new", 1.0);

    [Benchmark(Description = "ApplyTimeDecay (30min stale)")]
    public PatternReputation ApplyTimeDecay()
        => _updater.ApplyTimeDecay(_existingPattern);
}

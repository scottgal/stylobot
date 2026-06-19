using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Benchmarks for <see cref="WellKnownBotIndex.TryMatch"/> — the three-tier
///     L1 SIMD pre-filter / L2 literal / L3 regex scan.
///
///     Run: dotnet run -c Release --project src/Mostlylucid.BotDetection.Benchmarks -- --filter '*WellKnownBotIndex*'
///
///     Scenarios:
///       ColdMiss  — unique human Chrome UA each call (no bot keyword → L1 exits, 0 alloc).
///       ColdHit   — unique bot UA each call (L1 passes → L2 literal match → result).
///       WarmCache — same UA repeated (BoundedCache hit, fastest path).
/// </summary>
[Config(typeof(WellKnownBotIndexConfig))]
[MemoryDiagnoser]
public class WellKnownBotIndexBenchmarks
{
    private sealed class WellKnownBotIndexConfig : ManualConfig
    {
        public WellKnownBotIndexConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(3)
                .WithIterationCount(15));
            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }

    // 637-entry index that mirrors the real arcjet catalog shape:
    // 521 pure literals + 116 complex patterns (regex metacharacters).
    private WellKnownBotIndex _index = null!;

    private static readonly string HumanUA =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

    private static readonly string BotUA =
        "TurnitinBot (https://turnitin.com/robot/crawlerinfo.html)";

    // Unique suffix counter — appended so each iteration is a cache miss.
    private int _coldCounter;

    [GlobalSetup]
    public void Setup()
    {
        _index = new WellKnownBotIndex();
        _index.Replace(BuildEntries());
    }

    // Clears scan cache before each COLD iteration so every call is a true miss.
    [IterationSetup(Targets = [nameof(ColdMiss), nameof(ColdHit)])]
    public void ResetCache() => _index.Replace(BuildEntries());

    // ── L1 pre-filter exit: human Chrome UA, no bot keyword → returns null immediately ──

    [Benchmark]
    public WellKnownBotMatch? ColdMiss()
        => _index.TryMatch(HumanUA + "#" + (_coldCounter++));

    // ── L1 passes → L2 literal string.Contains → TurnitinBot match ──

    [Benchmark]
    public WellKnownBotMatch? ColdHit()
        => _index.TryMatch(BotUA + "#" + (_coldCounter++));

    // ── BoundedCache hit — unchanged by this PR ──

    [Benchmark]
    public WellKnownBotMatch? WarmCache()
        => _index.TryMatch(HumanUA);

    // ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<WellKnownBotEntry> BuildEntries()
    {
        var entries = new List<WellKnownBotEntry>(637);

        // 521 pure literals (realistic bot names from the arcjet catalog)
        string[] names = [
            "Googlebot-Mobile","Googlebot-Image","Googlebot-News","Googlebot-Video",
            "GPTBot","bingbot","YandexBot","Baiduspider","AhrefsBot","SemrushBot",
            "msnbot","DuckDuckBot","Slurp","Sogou","Exabot","ia_archiver",
            "facebookexternalhit","Twitterbot","LinkedInBot","Slackbot","TelegramBot",
            "Discordbot","PinterestBot","WhatsApp","SkypeUriPreview","vkShare",
            "curl","Wget","python-requests","python-urllib","Go-http-client",
            "HeadlessChrome","PhantomJS","Puppeteer","Playwright","Selenium",
            "Scrapy","HTTrack","libwww-perl","OkHttp","okhttp",
            "TurnitinBot","DotBot","MJ12bot","Screaming Frog SEO Spider",
            "ClaudeBot","anthropic-ai","CCBot","cohere-ai","PerplexityBot",
            "DataForSeoBot","MojeekBot","SeekportBot","PetalBot","Cocolyzebot",
            "CriteoBot","Google-InspectionTool","AdsBot-Google","Applebot","Amazonbot",
            "DuckAssistBot","ImagesiftBot","archive.org_bot","UptimeRobot","PingdomBot",
            "Nagios","site24x7","Zabbix","Dynatrace","NewRelic","Datadog",
            "StyloBot","StatusCake","WebCopier","DistilBotDetector",
        ];
        for (int i = 0; i < 521; i++)
        {
            var name = names[i % names.Length] + (i / names.Length > 0 ? (i / names.Length).ToString() : "");
            entries.Add(new WellKnownBotEntry {
                Id = "lit-" + i, Categories = ["tool"],
                Pattern = new WellKnownBotPattern { Accepted = [name], Forbidden = [] }
            });
        }

        // 116 complex patterns (metacharacters, realistic arcjet regex style)
        string[] complex = [
            "Googlebot\\/", "AdsBot-Google([^-]|$)", "[wW]get", "grub\\.org",
            "yandex\\.com\\/bots", "Mail\\.RU_Bot", "europarchive\\.org",
            "NerdByNature\\.Bot", "(sistrix|SISTRIX) [cC]rawler", "Exabot(\\/|\\+)",
            "BingPreview\\/1\\.0b", "MicrosoftPreview\\/[0-9]", "facebookexternalhit\\/[0-9]",
        ];
        for (int i = 0; i < 116; i++)
            entries.Add(new WellKnownBotEntry {
                Id = "re-" + i, Categories = ["tool"],
                Pattern = new WellKnownBotPattern {
                    Accepted = [complex[i % complex.Length]],
                    Forbidden = []
                }
            });

        return entries;
    }
}

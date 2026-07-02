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

    // The real arcjet catalog, loaded from the bundled baseline embedded in the
    // core library (635 entries: 521 pure literals + 116 regex patterns). Same
    // data the runtime seeds the index from — no hand-maintained list here.
    private WellKnownBotIndex _index = null!;
    private IReadOnlyList<WellKnownBotEntry> _entries = null!;

    private static readonly string HumanUA =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

    private static readonly string BotUA =
        "TurnitinBot (https://turnitin.com/robot/crawlerinfo.html)";

    // Passes L1 (keyword 'grub' from grub\.org) but matches NO literal and no
    // regex, so the scan traverses all 521 literals + all 116 regexes. Only
    // grub\.org's required core ('grub') is present, so the required-core
    // pre-check runs 1 regex and skips 115; without it all 116 regexes run.
    // This is the path the pre-check optimizes; the Cold* scenarios above never
    // reach the regex loop (L1 exit / early literal match).
    private static readonly string RegexScanUA =
        "Mozilla/5.0 (compatible; grubfoo/2.0; +http://example.com/info)";

    // Unique suffix counter — appended so each iteration is a cache miss.
    private int _coldCounter;

    [GlobalSetup]
    public void Setup()
    {
        _entries = WellKnownBotBaseline.Load();
        _index = new WellKnownBotIndex();
        _index.Replace(_entries);
    }

    // Clears scan cache before each COLD iteration so every call is a true miss.
    [IterationSetup(Targets = [nameof(ColdMiss), nameof(ColdHit), nameof(RegexScan)])]
    public void ResetCache() => _index.Replace(_entries);

    // ── L1 pre-filter exit: human Chrome UA, no bot keyword → returns null immediately ──

    [Benchmark]
    public WellKnownBotMatch? ColdMiss()
        => _index.TryMatch(HumanUA + "#" + (_coldCounter++));

    // ── L1 passes → L2 literal string.Contains → TurnitinBot match ──

    [Benchmark]
    public WellKnownBotMatch? ColdHit()
        => _index.TryMatch(BotUA + "#" + (_coldCounter++));

    // ── L1 passes, no literal/regex match → full 116-regex traversal ──
    //    (the required-core pre-check's target path)

    [Benchmark]
    public WellKnownBotMatch? RegexScan()
        => _index.TryMatch(RegexScanUA + "#" + (_coldCounter++));

    // ── BoundedCache hit — unchanged by this PR ──

    [Benchmark]
    public WellKnownBotMatch? WarmCache()
        => _index.TryMatch(HumanUA);

    // ────────────────────────────────────────────────────────────────
    // Index data comes from WellKnownBotBaseline.Load() (the real catalog embedded
    // in the core library) — no hand-maintained name/pattern list lives here.
}

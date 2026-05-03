# Bot Detection Benchmarks

YAML-driven benchmark harness for tuning detector memory and CPU, with automated regression checking.

## Quick Start

```bash
# Run all detector benchmarks
dotnet run -c Release -- --filter '*DetectorBenchmarkRunner*'

# Run full pipeline benchmarks
dotnet run -c Release -- --filter '*PipelineBenchmarkRunner*'

# Run a specific detector
dotnet run -c Release -- --filter '*Header*'

# List all available scenarios
dotnet run -c Release -- --list-scenarios

# Regression check (CI mode — exits non-zero if thresholds exceeded)
dotnet run -c Release -- --regression
```

## How It Works

Each benchmark scenario is a **YAML file** in `Scenarios/`. A generic BenchmarkDotNet runner class loads all scenarios at runtime, constructs `BlackboardState` from the YAML, resolves the named detector from DI, and benchmarks it. No C# code needed to add a new scenario.

```
Scenarios/
├── header-human-chrome.benchmark.yaml
├── header-bot-curl.benchmark.yaml
├── useragent-human.benchmark.yaml
├── heuristic-human.benchmark.yaml
├── pipeline-human-browsing.benchmark.yaml
└── ...
```

## YAML Scenario Format

```yaml
name: Header_HumanChrome                    # BenchmarkDotNet display name
description: Standard Chrome browser        # Human-readable description
detector: Header                            # IContributingDetector.Name (or "_pipeline")

request:
  method: GET
  path: /products/123
  protocol: https
  ip: 203.0.113.42
  headers:
    user-agent: "Mozilla/5.0 Chrome/120.0"
    accept: "text/html"
    accept-language: "en-US,en;q=0.9"

signals:                                    # Pre-populated blackboard signals
  request.ip.is_datacenter: false           # Simulates earlier detector output

thresholds:                                 # Optional — for regression mode
  max_mean_ns: 10000
  max_allocated_bytes: 2048
  max_p95_ns: 20000

tags: [fast-path, header, human]            # For filtering
```

### Key fields

- **`detector`** — matches `IContributingDetector.Name` exactly. Use `_pipeline` to benchmark the full `BlackboardOrchestrator.DetectAsync()`.
- **`signals`** — pre-populate blackboard signals to simulate wave dependencies (e.g., a Wave 2 detector can assume Wave 0 signals are present).
- **`thresholds`** — optional regression limits. When present, `--regression` mode compares actual results against these limits and fails if exceeded.
- **`tags`** — for filtering scenarios by category.

## Adding a New Scenario

1. Create a new `*.benchmark.yaml` file in `Scenarios/`
2. Set `detector` to the detector's `Name` property
3. Define the `request` with appropriate headers and IP
4. Optionally add `signals` to simulate prior detector output
5. Optionally add `thresholds` for regression checking
6. Run — it's automatically picked up

That's it. No C# changes needed.

## Architecture

```
Harness/
├── BenchmarkScenario.cs          # YAML model + ToBlackboardState() builder
├── BenchmarkScenarioLoader.cs    # Globs and deserializes *.benchmark.yaml
├── DetectorBenchmarkRunner.cs    # BenchmarkDotNet class for individual detectors
├── PipelineBenchmarkRunner.cs    # BenchmarkDotNet class for full orchestrator
└── RegressionChecker.cs          # Post-run threshold validation
```

- **`DetectorBenchmarkRunner`** uses `[ParamsSource]` to iterate all non-pipeline scenarios. BenchmarkDotNet creates one benchmark row per YAML scenario automatically.
- **`PipelineBenchmarkRunner`** does the same for `detector: _pipeline` scenarios.
- Both use `[MemoryDiagnoser]` and `[ThreadingDiagnoser]` for allocation and threading metrics.

## Tuning Workflow

1. Run benchmarks to identify hot spots:
   ```bash
   dotnet run -c Release -- --filter '*DetectorBenchmarkRunner*'
   ```

2. Read the results table — sort by `Allocated` column to find the biggest allocators.

3. Profile a specific detector for detail:
   ```bash
   dotnet run -c Release -- --filter '*Intent*'
   ```

4. Make code changes to the detector.

5. Re-run the same benchmark and compare.

6. If happy, update `thresholds` in the YAML to lock in the new baseline.

## Regression Checking (CI)

```bash
dotnet run -c Release -- --regression
```

This runs all benchmarks, then compares results against YAML `thresholds`. If any scenario exceeds its thresholds, the process exits with code 1 and prints the violations.

Threshold fields:
- `max_mean_ns` — mean execution time in nanoseconds
- `max_allocated_bytes` — managed heap allocation per operation
- `max_p95_ns` — 95th percentile execution time

## Current Results

Measured on Apple M-series, .NET 10.0, Release mode:

| Detector | Scenario | Mean | Allocated |
|----------|----------|------|-----------|
| Intent | Navigation | 2,341 ns | 5,448 B |
| Heuristic | Bot | 4,255 ns | 2,504 B |
| Heuristic | Human | 3,424 ns | 2,488 B |
| Behavioral | Normal | 1,446 ns | 2,112 B |
| Haxxor | SQL Injection | 1,306 ns | 1,608 B |
| Header | Bot (curl) | 507 ns | 1,520 B |
| Header | Human (Chrome) | 496 ns | 1,448 B |
| CacheBehavior | Normal | 1,335 ns | 1,400 B |
| Ip | Datacenter | 537 ns | 1,152 B |
| MultiLayerCorrelation | Full signals | 330 ns | 1,088 B |
| UserAgent | Googlebot | 829 ns | 1,072 B |
| AiScraper | GPTBot | 572 ns | 1,016 B |
| FastPathReputation | Cached signature | 308 ns | 928 B |
| UserAgent | Chrome | 2,058 ns | 928 B |
| Ip | Residential | 557 ns | 840 B |
| TransportProtocol | Document | 145 ns | 504 B |
| TlsFingerprint | Chrome/Bot | ~100-260 ns | 424 B |
| Inconsistency | TLS/UA mismatch | 135 ns | 376 B |
| CookieBehavior | With cookies | 33 ns | 184 B |
| Http2Fingerprint | Chrome | 120 ns | 152 B |
| HeaderCorrelation | Full headers | 50 ns | 104 B |
| Haxxor | Clean request | 204 ns | **0 B** |

### What to look for

- **>2KB allocated** — candidate for tuning (pre-size collections, avoid ToLowerInvariant, use spans)
- **>5µs mean** — check for unnecessary async, LINQ closures, or string allocations
- **0 B allocated** — ideal for fast-path detectors (Haxxor clean path achieves this)
- **High StdDev** — may indicate lock contention or cache misses

## Legacy Benchmarks

The original hand-coded benchmark classes (`IndividualDetectorBenchmarks.cs`, `DetectionPipelineBenchmarks.cs`, `SessionVectorBenchmarks.cs`, etc.) are still present and functional. They can be run via BenchmarkDotNet's interactive menu:

```bash
dotnet run -c Release
# Then select from the menu
```

The YAML-driven harness is the preferred approach for new scenarios.

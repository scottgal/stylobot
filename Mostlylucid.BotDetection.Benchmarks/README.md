# Bot Detection Benchmarks

Performance benchmarks for the Mostlylucid bot detection request processing pipeline using BenchmarkDotNet.

## Overview

This project benchmarks the bot detection pipeline with **predictable, deterministic scenarios** (no AI/LLM randomness)
to identify performance hotspots.

## What's Benchmarked

### Full Pipeline Scenarios

These benchmarks test the complete bot detection pipeline end-to-end:

- **Human request**: Typical website visitor with browser headers
- **Obvious bot**: Simple bot with curl user-agent
- **Search engine bot**: Googlebot with proper headers
- **Datacenter bot**: Browser UA from AWS datacenter IP

Each scenario runs through:

- BlackboardOrchestrator wave-based execution
- All enabled detectors (UserAgent, IP, Header, Behavioral, etc.)
- Evidence aggregation
- Policy evaluation

## Running Benchmarks

### Quick Start

```bash
cd Mostlylucid.BotDetection.Benchmarks
dotnet run -c Release
```

### Run Specific Benchmark

```bash
# Run specific scenario
dotnet run -c Release --filter "*DetectHuman*"

# Run with memory profiler
dotnet run -c Release --memory
```

### Advanced Options

```bash
# Longer run for more accurate results
dotnet run -c Release --warmupCount 5 --iterationCount 20

# Export results to different formats
dotnet run -c Release --exporters json,html,csv

# Compare results
dotnet run -c Release --join
```

## Understanding Results

BenchmarkDotNet will output:

- **Mean**: Average execution time
- **Error**: Standard error of mean
- **StdDev**: Standard deviation
- **Gen0/Gen1/Gen2**: Garbage collection counts
- **Allocated**: Memory allocated per operation

### What to Look For

**Performance Hotspots:**

- Operations taking >10ms in the fast path
- High GC allocations (Gen0 > 0.01 per op)
- High memory allocations (>10KB per request)
- High standard deviation (unpredictable performance)

**Expected Baselines:**

- Full pipeline (no AI): <50ms
- Memory allocation: <20KB per request

## Configuration

Benchmarks use these settings for predictable results:

```csharp
BotDetection.Enabled = true
AiDetection.OllamaEnabled = false     // No LLM calls
AiDetection.AnthropicEnabled = false  // No AI API calls
```

## Interpreting Results

If you see performance issues:

1. **High latency (>50ms)**: Check which wave is slow using detailed logging
2. **High memory (>20KB)**: Look for allocations in hot paths
3. **High GC pressure**: Consider object pooling for frequently allocated objects
4. **Variable performance**: May indicate external dependencies timing out

## Profiling

For deeper analysis, use:

```bash
# Memory profiler
dotnet run -c Release --memory

# Disassembly viewer
dotnet run -c Release --disasm

# Threading diagnostics
dotnet run -c Release --threading
```

## Comparing Before/After

```bash
# Baseline
dotnet run -c Release --job baseline

# After optimization
dotnet run -c Release --job optimized

# Compare results
dotnet run -c Release --join
```

## Output Files

Results are saved to `BenchmarkDotNet.Artifacts/results/`:

- `*.html` - Visual report
- `*.csv` - Raw data
- `*.md` - Markdown summary
- `*.json` - Structured data

## CI Integration

For CI pipelines:

```bash
# Run benchmarks and fail if regression detected
dotnet run -c Release --filter "*" --memory --allCategories --stopOnFirstError
```

## Tips

1. **Close other applications** - Minimize noise
2. **Run in Release mode** - Always use `-c Release`
3. **Multiple runs** - Run 3+ times for consistency
4. **Check GC** - Watch for excessive Gen2 collections
5. **Profile selectively** - Start with full pipeline, drill down to hotspots

## Example Output

```
| Method              | Mean       | Error    | StdDev   | Gen0   | Allocated |
|-------------------- |-----------:|---------:|---------:|-------:|----------:|
| DetectHuman         | 42.31 ms   | 0.84 ms  | 1.19 ms  | 0.0100 | 15.2 KB   |
| DetectBot           | 38.72 ms   | 0.76 ms  | 1.08 ms  | 0.0100 | 14.8 KB   |
| UserAgentDetector   |  8.45 ms   | 0.17 ms  | 0.24 ms  | -      |  2.1 KB   |
| EvidenceAggregation |  0.42 ms   | 0.01 ms  | 0.01 ms  | -      |  0.8 KB   |
```

## Next Steps

After identifying hotspots:

1. **Profile with dotTrace/perfview** for detailed call stacks
2. **Optimize hot paths** (regex, allocations, async overhead)
3. **Add caching** for expensive operations
4. **Parallelize** independent detectors
5. **Re-benchmark** to verify improvements

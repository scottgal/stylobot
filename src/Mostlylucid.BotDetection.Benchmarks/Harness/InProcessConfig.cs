using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

/// <summary>
///     InProcessEmitToolchain skips BenchmarkDotNet's per-job auto-generated
///     boilerplate csproj build step. Two related reasons it's the default
///     here: (1) the repo has multiple Benchmarks csproj copies under
///     git worktrees, and the BDN ProjectBuilder can pick the wrong one and
///     fail with "BenchmarkDotNet has failed to build the auto-generated
///     boilerplate code"; (2) for local dev iteration on the perf code, an
///     in-process job is meaningfully faster to start. Accuracy is sufficient
///     for the microbenchmarks in this project; the production CI run still
///     uses the external-process default job from a clean checkout.
///
///     JsonExporter.Full writes *-report-full.json to the artifacts directory,
///     which is required by RegressionChecker.Check to evaluate thresholds.
/// </summary>
public sealed class InProcessConfig : ManualConfig
{
    public InProcessConfig()
    {
        AddJob(Job.Default
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithStrategy(RunStrategy.Throughput)
            .WithWarmupCount(3)
            .WithIterationCount(10));
        AddExporter(JsonExporter.Full);
        AddDiagnoser(MemoryDiagnoser.Default);
        AddDiagnoser(ThreadingDiagnoser.Default);
    }
}

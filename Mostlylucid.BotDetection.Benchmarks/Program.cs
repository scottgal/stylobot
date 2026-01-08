using BenchmarkDotNet.Running;

namespace Mostlylucid.BotDetection.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        // Run individual detector benchmarks if --detectors flag is provided
        if (args.Contains("--detectors"))
            BenchmarkRunner.Run<IndividualDetectorBenchmarks>();
        else
            BenchmarkRunner.Run<DetectionPipelineBenchmarks>();
    }
}
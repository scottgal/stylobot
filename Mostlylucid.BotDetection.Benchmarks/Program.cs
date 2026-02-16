using BenchmarkDotNet.Running;

namespace Mostlylucid.BotDetection.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7171/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.100-rc.1.25451.107
  [Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```

| Method                        | Mean | Error |
|-------------------------------|-----:|------:|
| &#39;UserAgent Detector&#39;  |   NA |    NA |
| &#39;IP Detector&#39;         |   NA |    NA |
| &#39;Header Detector&#39;     |   NA |    NA |
| &#39;Behavioral Detector&#39; |   NA |    NA |
| &#39;Heuristic Detector&#39;  |   NA |    NA |

Benchmarks with issues:
IndividualDetectorBenchmarks.'UserAgent Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
IndividualDetectorBenchmarks.'IP Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
IndividualDetectorBenchmarks.'Header Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
IndividualDetectorBenchmarks.'Behavioral Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
IndividualDetectorBenchmarks.'Heuristic Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)

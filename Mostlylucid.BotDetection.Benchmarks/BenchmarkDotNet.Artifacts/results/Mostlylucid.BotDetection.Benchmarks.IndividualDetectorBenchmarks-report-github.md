```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```
| Method                        | Mean | Error |
|------------------------------ |-----:|------:|
| &#39;UserAgent Detector&#39;          |   NA |    NA |
| &#39;IP Detector&#39;                 |   NA |    NA |
| &#39;Header Detector&#39;             |   NA |    NA |
| &#39;Behavioral Detector&#39;         |   NA |    NA |
| &#39;Heuristic Detector&#39;          |   NA |    NA |
| &#39;HTTP/3 Fingerprint Detector&#39; |   NA |    NA |
| &#39;AI Scraper Detector&#39;         |   NA |    NA |

Benchmarks with issues:
  IndividualDetectorBenchmarks.'UserAgent Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
  IndividualDetectorBenchmarks.'IP Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
  IndividualDetectorBenchmarks.'Header Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
  IndividualDetectorBenchmarks.'Behavioral Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
  IndividualDetectorBenchmarks.'Heuristic Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
  IndividualDetectorBenchmarks.'HTTP/3 Fingerprint Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)
  IndividualDetectorBenchmarks.'AI Scraper Detector': Job-ORZUYQ(IterationCount=10, RunStrategy=Throughput, WarmupCount=3)

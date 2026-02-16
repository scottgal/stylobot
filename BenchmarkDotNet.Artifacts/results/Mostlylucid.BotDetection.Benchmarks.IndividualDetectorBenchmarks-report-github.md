```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```
| Method                        | Mean       | Error    | StdDev   | Gen0   | Completed Work Items | Lock Contentions | Gen1   | Gen2   | Allocated |
|------------------------------ |-----------:|---------:|---------:|-------:|---------------------:|-----------------:|-------:|-------:|----------:|
| &#39;UserAgent Detector&#39;          |   283.3 ns |  5.86 ns |  3.88 ns | 0.0148 |               0.0000 |                - |      - |      - |    1152 B |
| &#39;IP Detector&#39;                 |   422.8 ns |  2.72 ns |  1.62 ns | 0.0124 |               0.0000 |                - | 0.0005 | 0.0005 |         - |
| &#39;Header Detector&#39;             |   204.8 ns |  4.41 ns |  2.92 ns | 0.0119 |               0.0000 |                - |      - |      - |     920 B |
| &#39;Behavioral Detector&#39;         | 1,193.6 ns | 43.00 ns | 28.44 ns | 0.0343 |               0.0000 |                - |      - |      - |    2688 B |
| &#39;Heuristic Detector&#39;          | 1,200.7 ns | 19.54 ns | 11.63 ns | 0.0324 |               0.0000 |                - |      - |      - |    2512 B |
| &#39;HTTP/3 Fingerprint Detector&#39; |   405.5 ns |  8.42 ns |  5.57 ns | 0.0229 |               0.0000 |                - |      - |      - |    1776 B |
| &#39;AI Scraper Detector&#39;         |   347.2 ns |  9.08 ns |  5.40 ns | 0.0143 |               0.0000 |                - |      - |      - |    1112 B |

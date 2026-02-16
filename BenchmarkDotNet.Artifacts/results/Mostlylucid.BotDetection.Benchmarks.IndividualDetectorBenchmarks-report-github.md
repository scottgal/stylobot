```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```
| Method                        | Mean       | Error     | StdDev   | Gen0   | Completed Work Items | Lock Contentions | Gen1   | Gen2   | Allocated |
|------------------------------ |-----------:|----------:|---------:|-------:|---------------------:|-----------------:|-------:|-------:|----------:|
| &#39;UserAgent Detector&#39;          |   482.2 ns |  81.01 ns | 53.58 ns | 0.0200 |               0.0000 |                - |      - |      - |    1568 B |
| &#39;IP Detector&#39;                 |   700.4 ns | 117.68 ns | 77.84 ns | 0.0181 |               0.0000 |                - |      - |      - |    1400 B |
| &#39;Header Detector&#39;             |   519.0 ns |  25.30 ns | 16.74 ns | 0.0191 |               0.0000 |                - |      - |      - |    1472 B |
| &#39;Behavioral Detector&#39;         | 1,465.5 ns |  92.34 ns | 54.95 ns | 0.0381 |               0.0000 |                - |      - |      - |    2944 B |
| &#39;Heuristic Detector&#39;          | 1,605.1 ns | 115.29 ns | 76.26 ns | 0.0362 |               0.0000 |                - |      - |      - |    2848 B |
| &#39;HTTP/3 Fingerprint Detector&#39; |   898.7 ns |  70.38 ns | 41.88 ns | 0.0401 |               0.0000 |                - | 0.0010 | 0.0010 |         - |
| &#39;AI Scraper Detector&#39;         |   706.7 ns |  57.96 ns | 38.34 ns | 0.0210 |               0.0000 |                - |      - |      - |    1664 B |

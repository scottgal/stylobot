```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-VVEFLF : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=5  RunStrategy=Throughput  WarmupCount=2  

```
| Method                            | Mean        | Error      | StdDev    | Completed Work Items | Lock Contentions | Gen0   | Gen1   | Gen2   | Allocated |
|---------------------------------- |------------:|-----------:|----------:|---------------------:|-----------------:|-------:|-------:|-------:|----------:|
| &#39;UserAgent Detector&#39;              |   507.41 ns |  59.018 ns |  9.133 ns |               0.0000 |                - | 0.0210 |      - |      - |    1648 B |
| &#39;IP Detector&#39;                     |   484.85 ns |  80.149 ns | 12.403 ns |               0.0000 |                - | 0.0114 |      - |      - |     912 B |
| &#39;Header Detector&#39;                 |   282.24 ns |  71.534 ns | 18.577 ns |               0.0000 |                - | 0.0138 |      - |      - |    1072 B |
| &#39;Behavioral Detector&#39;             | 1,276.25 ns | 161.738 ns | 42.003 ns |               0.0000 |                - | 0.0343 |      - |      - |    2736 B |
| &#39;Heuristic Detector&#39;              | 1,457.87 ns | 166.301 ns | 43.188 ns |               0.0000 |                - | 0.0744 | 0.0019 | 0.0019 |         - |
| &#39;HTTP/3 Fingerprint Detector&#39;     |   422.33 ns |  38.333 ns |  5.932 ns |               0.0000 |                - | 0.0229 |      - |      - |    1776 B |
| &#39;AI Scraper Detector&#39;             |   369.83 ns |  53.705 ns |  8.311 ns |               0.0000 |                - | 0.0153 | 0.0005 | 0.0005 |         - |
| &#39;Haxxor: Clean Path (zero alloc)&#39; |   403.59 ns |  10.912 ns |  2.834 ns |               0.0000 |                - | 0.0005 |      - |      - |      72 B |
| &#39;Haxxor: SQLi Attack&#39;             | 1,478.95 ns | 210.887 ns | 54.767 ns |               0.0000 |                - | 0.0210 |      - |      - |    1744 B |
| &#39;Haxxor: Path Probe (/wp-admin)&#39;  |   660.22 ns | 102.390 ns | 26.590 ns |               0.0000 |                - | 0.0172 |      - |      - |    1344 B |
| &#39;ATO: Clean Path (zero alloc)&#39;    |    43.30 ns |   4.041 ns |  1.049 ns |               0.0000 |                - | 0.0009 |      - |      - |      73 B |
| &#39;ATO: Login POST&#39;                 |    46.98 ns |   2.268 ns |  0.589 ns |               0.0000 |                - | 0.0009 |      - |      - |      73 B |

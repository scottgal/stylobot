```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-VVEFLF : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=5  RunStrategy=Throughput  WarmupCount=2  

```
| Method                            | Mean        | Error      | StdDev    | Completed Work Items | Lock Contentions | Gen0   | Allocated |
|---------------------------------- |------------:|-----------:|----------:|---------------------:|-----------------:|-------:|----------:|
| &#39;UserAgent Detector&#39;              |   558.08 ns | 348.255 ns | 90.441 ns |               0.0000 |                - | 0.0215 |    1648 B |
| &#39;IP Detector&#39;                     |   467.56 ns |  29.634 ns |  7.696 ns |               0.0000 |                - | 0.0119 |     912 B |
| &#39;Header Detector&#39;                 |   212.56 ns |  30.296 ns |  7.868 ns |               0.0000 |                - | 0.0119 |     920 B |
| &#39;Behavioral Detector&#39;             | 1,403.82 ns | 113.470 ns | 17.560 ns |               0.0000 |                - | 0.0343 |    2688 B |
| &#39;Heuristic Detector&#39;              | 1,577.36 ns | 231.116 ns | 60.020 ns |               0.0000 |                - | 0.0324 |    2512 B |
| &#39;HTTP/3 Fingerprint Detector&#39;     |   541.48 ns |  56.192 ns |  8.696 ns |               0.0000 |                - | 0.0229 |    1776 B |
| &#39;AI Scraper Detector&#39;             |   476.11 ns |  88.923 ns | 13.761 ns |               0.0000 |                - | 0.0143 |    1112 B |
| &#39;Haxxor: Clean Path (zero alloc)&#39; |   488.19 ns | 267.053 ns | 69.353 ns |               0.0000 |                - | 0.0010 |      72 B |
| &#39;Haxxor: SQLi Attack&#39;             | 1,472.12 ns |  71.402 ns | 18.543 ns |               0.0000 |                - | 0.0229 |    1880 B |
| &#39;Haxxor: Path Probe (/wp-admin)&#39;  |   673.89 ns |  77.213 ns | 20.052 ns |               0.0000 |                - | 0.0172 |    1344 B |
| &#39;ATO: Clean Path (zero alloc)&#39;    |    42.05 ns |   0.923 ns |  0.143 ns |               0.0000 |                - | 0.0009 |      73 B |
| &#39;ATO: Login POST&#39;                 |    43.64 ns |   2.564 ns |  0.666 ns |               0.0000 |                - | 0.0009 |      73 B |

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```
| Method                                | Mean      | Error     | StdDev    | Gen0   | Allocated |
|-------------------------------------- |----------:|----------:|----------:|-------:|----------:|
| &#39;NormalizeUserAgent (Chrome/Win)&#39;     |  94.80 ns |  6.878 ns |  4.093 ns | 0.0031 |     240 B |
| &#39;NormalizeUserAgent (Chrome/Mac)&#39;     | 100.21 ns | 17.221 ns | 11.390 ns | 0.0031 |     240 B |
| &#39;NormalizeUserAgent (Googlebot)&#39;      | 104.23 ns |  3.065 ns |  2.028 ns | 0.0029 |     224 B |
| &#39;NormalizeUserAgent (curl)&#39;           |  92.30 ns |  8.316 ns |  4.949 ns | 0.0027 |     216 B |
| &#39;NormalizeUserAgent (Python scraper)&#39; | 107.36 ns |  1.595 ns |  1.055 ns | 0.0033 |     264 B |
| &#39;CreateUaPatternId (Chrome/Win)&#39;      | 113.19 ns |  3.066 ns |  2.028 ns | 0.0045 |     360 B |
| &#39;CreateIpPatternId (IPv4)&#39;            |  25.46 ns |  6.216 ns |  4.112 ns | 0.0015 |     112 B |
| &#39;CreateIpPatternId (IPv6)&#39;            | 113.98 ns |  3.949 ns |  2.350 ns | 0.0062 |     480 B |
| &#39;ApplyEvidence (existing pattern)&#39;    |  61.08 ns |  1.015 ns |  0.604 ns | 0.0015 |     120 B |
| &#39;ApplyEvidence (new pattern)&#39;         |  96.30 ns |  1.742 ns |  1.037 ns | 0.0015 |     120 B |
| &#39;ApplyTimeDecay (30min stale)&#39;        |  25.73 ns |  0.194 ns |  0.101 ns |      - |         - |

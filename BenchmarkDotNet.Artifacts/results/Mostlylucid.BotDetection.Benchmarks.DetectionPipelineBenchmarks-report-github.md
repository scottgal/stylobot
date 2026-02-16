```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```
| Method                                    | Mean          | Error      | StdDev     | Completed Work Items | Lock Contentions | Gen0   | Allocated |
|------------------------------------------ |--------------:|-----------:|-----------:|---------------------:|-----------------:|-------:|----------:|
| &#39;Human request (typical website visitor)&#39; | 100,757.89 μs | 464.521 μs | 307.252 μs |               6.6000 |                - |      - | 205.18 KB |
| &#39;Obvious bot (curl user-agent)&#39;           | 101,096.44 μs | 462.539 μs | 305.941 μs |               6.6000 |                - |      - | 163.92 KB |
| &#39;Search engine bot (Googlebot)&#39;           |      51.82 μs |   2.987 μs |   1.976 μs |               0.0110 |           0.0001 | 0.7324 |  54.33 KB |
| &#39;Datacenter IP with browser UA&#39;           | 100,843.65 μs | 667.794 μs | 441.705 μs |               6.4000 |                - |      - | 169.69 KB |

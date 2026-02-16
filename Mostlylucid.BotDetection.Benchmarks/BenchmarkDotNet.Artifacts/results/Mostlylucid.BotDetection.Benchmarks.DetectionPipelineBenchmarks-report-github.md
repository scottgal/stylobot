```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```
| Method                                    | Mean          | Error      | StdDev     | Completed Work Items | Lock Contentions | Gen0   | Gen1   | Allocated |
|------------------------------------------ |--------------:|-----------:|-----------:|---------------------:|-----------------:|-------:|-------:|----------:|
| &#39;Human request (typical website visitor)&#39; | 100,934.87 μs | 303.960 μs | 158.977 μs |               6.2000 |                - |      - |      - |  208.1 KB |
| &#39;Obvious bot (curl user-agent)&#39;           | 100,786.51 μs | 398.586 μs | 263.640 μs |               6.2000 |                - |      - |      - | 178.96 KB |
| &#39;Search engine bot (Googlebot)&#39;           |      69.44 μs |   1.531 μs |   0.801 μs |               1.0134 |                - | 0.4883 | 0.2441 |  86.85 KB |
| &#39;Datacenter IP with browser UA&#39;           | 100,959.51 μs | 451.032 μs | 298.330 μs |               6.2000 |                - |      - |      - | 171.29 KB |

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```
| Method                                    | Mean     | Error     | StdDev   | Completed Work Items | Lock Contentions | Gen0   | Gen1   | Allocated |
|------------------------------------------ |---------:|----------:|---------:|---------------------:|-----------------:|-------:|-------:|----------:|
| &#39;Human request (typical website visitor)&#39; | 237.6 μs | 107.54 μs | 71.13 μs |               3.4404 |           0.0020 | 0.9766 |      - | 170.46 KB |
| &#39;Obvious bot (curl user-agent)&#39;           | 253.9 μs |  65.89 μs | 43.58 μs |               3.4111 |                - | 0.9766 |      - | 177.02 KB |
| &#39;Search engine bot (Googlebot)&#39;           | 224.3 μs |  53.94 μs | 32.10 μs |               3.3330 |           0.0010 | 0.9766 |      - | 178.78 KB |
| &#39;Datacenter IP with browser UA&#39;           | 183.7 μs |  26.54 μs | 17.55 μs |               3.2705 |           0.0010 | 0.9766 | 0.4883 | 169.83 KB |

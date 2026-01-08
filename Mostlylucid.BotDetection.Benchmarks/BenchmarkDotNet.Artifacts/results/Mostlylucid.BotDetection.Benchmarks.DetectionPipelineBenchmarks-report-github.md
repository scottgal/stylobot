```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7171/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.100-rc.1.25451.107
  [Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```

| Method                                            |     Mean |    Error |   StdDev | Completed Work Items | Lock Contentions |   Gen0 |   Gen1 |   Gen2 | Allocated |
|---------------------------------------------------|---------:|---------:|---------:|---------------------:|-----------------:|-------:|-------:|-------:|----------:|
| &#39;Human request (typical website visitor)&#39; | 19.09 μs | 1.246 μs | 0.824 μs |               0.9790 |           0.0001 | 0.6104 | 0.1221 |      - |  43.57 KB |
| &#39;Obvious bot (curl user-agent)&#39;           | 18.75 μs | 1.275 μs | 0.843 μs |               0.9789 |                - | 0.7324 | 0.2441 | 0.0610 |  43.29 KB |
| &#39;Search engine bot (Googlebot)&#39;           | 18.00 μs | 1.193 μs | 0.710 μs |               0.9771 |                - | 0.4883 | 0.1221 |      - |  43.29 KB |
| &#39;Datacenter IP with browser UA&#39;           | 18.61 μs | 1.380 μs | 0.913 μs |               0.9726 |                - | 0.7935 | 0.3052 | 0.1221 |  42.76 KB |

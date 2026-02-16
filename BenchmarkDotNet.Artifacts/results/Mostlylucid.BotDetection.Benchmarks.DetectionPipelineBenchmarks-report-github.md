```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```
| Method                                    | Mean      | Error     | StdDev    | Completed Work Items | Lock Contentions | Gen0   | Gen1   | Allocated |
|------------------------------------------ |----------:|----------:|----------:|---------------------:|-----------------:|-------:|-------:|----------:|
| &#39;Human request (typical website visitor)&#39; | 221.07 μs | 21.700 μs | 14.353 μs |               3.3662 |                - | 0.9766 | 0.4883 | 173.85 KB |
| &#39;Obvious bot (curl user-agent)&#39;           | 190.54 μs |  5.241 μs |  3.119 μs |               3.2852 |                - | 0.9766 | 0.4883 | 168.47 KB |
| &#39;Search engine bot (Googlebot)&#39;           |  66.12 μs |  0.618 μs |  0.409 μs |               0.0138 |                - | 0.6104 |      - |  54.19 KB |
| &#39;Datacenter IP with browser UA&#39;           | 191.93 μs |  5.956 μs |  3.115 μs |               3.2603 |           0.0005 | 0.9766 | 0.4883 |  171.9 KB |

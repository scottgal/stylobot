```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7840/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-ORZUYQ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  RunStrategy=Throughput  WarmupCount=3  

```
| Method                                          | Mean          | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|------------------------------------------------ |--------------:|------------:|------------:|-------:|-------:|----------:|
| &#39;RecordTransition (per-request hot path)&#39;       |  4,134.952 ns | 161.9217 ns |  84.6882 ns | 0.1221 | 0.0229 |   10168 B |
| &#39;PathNormalizer.Normalize (per-request)&#39;        |    177.220 ns |   3.4714 ns |   2.0658 ns | 0.0012 |      - |     104 B |
| &#39;PathNormalizer.Normalize (8 diverse paths)&#39;    |  1,379.504 ns |  13.9516 ns |   8.3024 ns | 0.0076 |      - |     648 B |
| PathNormalizer.Classify                         |     11.086 ns |   0.0823 ns |   0.0490 ns |      - |      - |         - |
| &#39;ComputeSimilarity (default weights)&#39;           |    345.643 ns |   6.5562 ns |   3.4290 ns | 0.0272 |      - |    2080 B |
| &#39;ComputeSimilarity (adaptive weights)&#39;          |     69.759 ns |   0.4269 ns |   0.2540 ns |      - |      - |         - |
| &#39;ComputeGeoSimilarity (Haversine)&#39;              |     23.024 ns |   0.4719 ns |   0.3121 ns |      - |      - |         - |
| &#39;ComputeGeoSimilarity (categorical)&#39;            |      2.117 ns |   0.0324 ns |   0.0214 ns |      - |      - |         - |
| &#39;AdaptiveWeighter.ComputeWeights (50 features)&#39; |  7,094.257 ns | 171.4397 ns |  89.6663 ns | 0.2823 |      - |   21616 B |
| &#39;ComputeSimilarity 50x50 matrix&#39;                | 94,028.937 ns | 669.0602 ns | 398.1470 ns |      - |      - |         - |
| &#39;JensenShannonDivergence (5-key distributions)&#39; |    367.691 ns |  33.4525 ns |  22.1268 ns | 0.0100 |      - |     800 B |
| TransitionMatrix.RecordTransition               |     60.530 ns |   0.5466 ns |   0.3616 ns |      - |      - |         - |
| TransitionMatrix.GetTransitionProbability       |     30.900 ns |   0.2371 ns |   0.1568 ns |      - |      - |         - |
| TransitionMatrix.GetDistribution                |     77.427 ns |   1.8047 ns |   0.9439 ns | 0.0056 |      - |     432 B |
| TransitionMatrix.GetPathEntropy                 |    263.762 ns |  16.8060 ns |  10.0010 ns | 0.0129 |      - |    1016 B |
| DecayingCounter.Decayed                         |     57.435 ns |   0.5102 ns |   0.3374 ns |      - |      - |         - |

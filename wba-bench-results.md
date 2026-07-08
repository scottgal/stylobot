# Web Bot Auth Verifier Benchmarks

**Platform:** BenchmarkDotNet v0.15.8, macOS Tahoe 26.5, Apple M5 arm64 (10 cores)
**Runtime:** .NET 10.0.5, Arm64 RyuJIT armv8.0-a
**Job:** IterationCount=5, WarmupCount=3
**Committed to:** `claude/wba-bench` branch

---

## WebBotAuthVerifierBenchmarks

Full RFC 9421 pipeline (parse Signature-Input/Signature + resolve key + reconstruct sig base + crypto verify + freshness check), signed bearer token verifier, and raw crypto baseline.

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.5 (25F71) [Darwin 25.5.0]
Apple M5, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-NTRUNJ : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

IterationCount=5  WarmupCount=3
```

| Method                  | Mean         | Error         | StdDev       | Gen0   | Allocated |
|------------------------ |-------------:|--------------:|-------------:|-------:|----------:|
| Rfc9421_Valid_Ed25519   | 21,534.48 ns |  2,855.813 ns |   741.645 ns | 0.0305 |    4816 B |
| Rfc9421_Valid_EcdsaP256 | 85,287.25 ns | 14,505.677 ns | 3,767.078 ns |      - |    6744 B |
| Rfc9421_Invalid_Sig     | 22,354.37 ns |  1,021.897 ns |   265.383 ns | 0.0305 |    4600 B |
| Rfc9421_Expired         | 22,314.33 ns |    943.540 ns |   146.014 ns | 0.0305 |    5520 B |
| Rfc9421_Unknown_Key     |    357.16 ns |     48.769 ns |    12.665 ns | 0.0272 |    2784 B |
| Rfc9421_Malformed       |     33.65 ns |      1.465 ns |     0.380 ns | 0.0013 |     128 B |
| SignedToken_Valid        | 21,302.22 ns |  1,755.052 ns |   455.781 ns | 0.0305 |    3280 B |
| SignedToken_Tampered    | 21,094.28 ns |  3,625.970 ns |   941.653 ns | 0.0305 |    3240 B |
| Crypto_Baseline_Ed25519 | 21,102.33 ns |  2,874.589 ns |   746.522 ns |      - |      56 B |

---

## WebBotAuthRegistryBenchmarks

`PublicKeyRegistry.TryResolve` across key-set sizes. Hit = last key (worst-case linear scan). Miss = keyid absent (full scan of both arrays). Zero allocations at all sizes confirmed.

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.5 (25F71) [Darwin 25.5.0]
Apple M5, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-NTRUNJ : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

IterationCount=5  WarmupCount=3
```

| Method          | RegistrySize | Mean        | Error      | StdDev    | Allocated |
|---------------- |------------- |------------:|-----------:|----------:|----------:|
| TryResolve_Hit  | 1            |   1.5344 ns |  0.7973 ns | 0.2071 ns |         - |
| TryResolve_Miss | 1            |   0.8604 ns |  0.2029 ns | 0.0527 ns |         - |
| TryResolve_Hit  | 50           |  55.6118 ns |  3.2158 ns | 0.4976 ns |         - |
| TryResolve_Miss | 50           |  21.5633 ns |  3.9674 ns | 0.6140 ns |         - |
| TryResolve_Hit  | 500          | 615.1647 ns | 45.4652 ns | 7.0358 ns |         - |
| TryResolve_Miss | 500          | 255.3612 ns | 38.7463 ns | 5.9960 ns |         - |

---

## Analysis

**Ed25519 vs ECDSA cost:** Ed25519 (~21.5 us) is ~4x faster than ECDSA-P256 (~85 us). Both use per-verify key import: NSec Ed25519 key import is cheap; the `ECDsa.Create()` + `ImportSubjectPublicKeyInfo()` path in ECDSA dominates. If ECDSA becomes a primary scheme in deployment, key-import caching would give a 4x improvement.

**Non-crypto overhead delta:** `Crypto_Baseline_Ed25519` = 21,102 ns; `Rfc9421_Valid_Ed25519` = 21,534 ns. Delta = ~432 ns (~2%). Parse + split + `TryResolve` + `StringBuilder` sig-base reconstruct + `Encoding.UTF8.GetBytes` adds only ~430 ns on top of the crypto cost. Negligible.

**TryResolve alloc + scan cost:** Zero allocations at all sizes (volatile array read, no GC pressure). Scan is O(n) linear: ~1.1 ns per key-string comparison on hit, ~0.5 ns per comparison on miss (mismatch short-circuits after prefix). At the expected real-world registry size of 50-200 keys, `TryResolve` costs 55-220 ns and is not a bottleneck.

**Short-circuit quality:** Unknown-key exits after the registry scan without touching crypto (~357 ns vs ~21 us for the full path). Malformed exits after the first newline-split check (~34 ns). Both produce no crypto work.

**Allocation profile:** Valid Ed25519 paths allocate ~4.8 KB per verify, driven by the `Dictionary<string,string>` header copy inside `TryBuildSignatureBase` and the `ToClaims` result dict. If per-token alloc matters at very high RPS, the header dictionary could be pooled. For typical WBA traffic (authenticated agents, not 100k+ RPS raw throughput), the current profile is acceptable.
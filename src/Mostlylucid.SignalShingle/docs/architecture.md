# Architecture

Signal Shingle has three intentionally separate responsibilities:

```text
authoritative data → materializer → projection cache → request / HTML fragment
                              └──── dirty beacon ───→ browser fetch
```

The materializer is outside the request path. It obtains `AcquireRefreshCandidates`, applies its own page-count, concurrency, and wall-clock budgets, then calls `CompleteRefresh` or `FailRefresh`. This package does not prescribe a scheduler because the protected resource and batching strategy belong to the host.

## Identity and freshness

The key is the normalized identity of a projection: widget/surface, filters, display parameters, and data generation where appropriate. Cadence is demand metadata, never key material. The effective interval is the smallest interval requested by a live lease or pin.

`MarkDirty` only affects resident keys. This protects a bounded cache from an unbounded event stream. A completion acknowledges only the dirty version it acquired; a newer invalidation remains due.

## Lifecycle

1. `Read(key, demand)` records or renews consumer demand and returns `Warm` or `Warming`.
2. `AcquireRefreshCandidates` atomically reserves due entries.
3. The host composes a value and calls `CompleteRefresh(candidate, value, generation)`.
4. A successful completion can be followed by `ISignalShingleNotifier.NotifyAsync`.
5. Consumers receive `Dirty` and request the fragment endpoint; they never receive dashboard data through SignalR.

When a lease expires, it no longer affects cadence. When no live lease or pin remains, the entry is not scheduled and can be reclaimed by LFU eviction.

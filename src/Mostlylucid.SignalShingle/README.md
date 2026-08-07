# Mostlylucid.SignalShingle

`Mostlylucid.SignalShingle` is a bounded, demand-driven projection cache for shared live surfaces. Requests only read the last successful value (or receive `Warming`); a scheduled materializer is the only component that performs expensive work.

It fits operational dashboards, shared widgets, status pages, and read-heavy server-rendered applications where coherent, honest snapshots matter more than computing on every request.

Read the full design rationale: [Signal Shingle architecture](https://mostlylucid.net/blog/signal-shingle-architecture).

## Why it exists

Traditional `GetOrCreate` caching lets a cold request execute expensive work. Under concurrent traffic that becomes a fan-out storm. Signal Shingle makes the boundary explicit:

1. The request records short-lived demand and reads a snapshot.
2. A bounded scheduler acquires due work, batches/composes it, then completes a lease.
3. SignalR sends a tiny dirty beacon; the browser fetches only the updated HTML fragment.

The cache is not a data authority. It contains derived projections only.

```csharp
services.AddSignalShingleCache<DashboardEnvelope, DashboardModel>(o =>
{
    o.Capacity = 256;
    o.MaximumStaleness = TimeSpan.FromMinutes(20);
});

// Request path: no factory and therefore no accidental database work.
var read = cache.Read(envelope,
    SignalShingleDemand.Create("dashboard:traffic", TimeSpan.FromMinutes(1)));
return read.IsWarm ? Render(read.Value!) : RenderWarming();

// Scheduled, bounded materializer: batch/compose these candidates as appropriate.
foreach (var candidate in cache.AcquireRefreshCandidates(maxCount: 16))
{
    try { cache.CompleteRefresh(candidate, await ComposeAsync(candidate.Key, ct), generation); }
    catch { cache.FailRefresh(candidate); throw; }
}
```

The caller owns key normalization and the refresh executor. The core package owns bounded LFU-style retention, demand leases, pinned coverage, stale fallback, generation ordering, and exclusive refresh leases.

## ASP.NET Core quick start

```csharp
builder.Services.AddSignalShingleUi(o => o.Capacity = 128);
app.MapSignalShingleUi();
```

Add the package script plus Alpine and the SignalR browser client, then use a cache island in any Razor view:

```html
@addTagHelper *, Mostlylucid.SignalShingle

<signal-shingle key="traffic" consumer="dashboard:traffic" refresh-seconds="30">
  <p>Warming dashboard…</p>
</signal-shingle>
<script src="/_content/Mostlylucid.SignalShingle/signal-shingle.js"></script>
```

The scheduled materializer calls `CompleteRefresh`; `ISignalShingleNotifier.NotifyAsync` emits the small SignalR dirty beacon after a successful completion. External source changes can call `MarkDirtyAndNotifyAsync`.

The demo project is runnable with `dotnet run --project src/Mostlylucid.SignalShingle.Demo`.

## Guarantees

- **No request-time compute.** There is no factory API on `Read`.
- **One refresh per key.** Acquiring candidates creates a short refresh lease; overlapping scheduler waves cannot both compose a key.
- **No lost invalidations.** A dirty event received during a refresh increments a dirty version, so the older completion cannot acknowledge it.
- **Honest age.** Values older than `MaximumStaleness` return `Warming`, even if they remain resident for diagnostics.
- **Bounded local state.** Pins protect known defaults; active demand is LFU-ranked and expires through renewable leases.

## Integration notes

- Normalize every result-bearing parameter into the key. Do not include refresh cadence in the key.
- Use a stable consumer name per widget/view, not a random request id, so requests renew one demand lease.
- Always call `FailRefresh` when composition fails; this releases the work for a later wave.
- Treat SignalR as an acceleration hint. Reconnects and normal cadence must still recover missed beacons.
- The HTML cache is local to a replica. Do not store secrets in fragment HTML or log its contents.

See [architecture notes](docs/architecture.md), [ASP.NET Core integration](docs/aspnetcore.md), and [NuGet publishing](docs/publishing.md).

## Contract

- A read never invokes a factory or starts background work.
- Refresh work is exclusively leased; completion only acknowledges the invalidations it observed.
- Cadence is the minimum interval across active leases and an optional pin; it is not key material.
- Values past `MaximumStaleness` become `Warming`, rather than being presented as current.
- Pins cover known defaults; demand leases retain views that real consumers continue to read.

# ASP.NET Core integration

`AddSignalShingleUi` registers a local `ISignalShingleCache<string,string>`, SignalR, the fragment endpoint, and the notifier. `MapSignalShingleUi` maps the endpoint and hub.

```csharp
builder.Services.AddSignalShingleUi(cache =>
{
    cache.Capacity = 128;
    cache.MaximumStaleness = TimeSpan.FromMinutes(10);
});

app.MapStaticAssets(); // .NET 9/10 static-web-assets endpoint
app.MapSignalShingleUi();
```

For Razor, add `@addTagHelper *, Mostlylucid.SignalShingle`, load Alpine, the SignalR browser client, and `/_content/Mostlylucid.SignalShingle/signal-shingle.js`. The `signal-shingle` element supplies fallback HTML while warming and subscribes the island to dirty beacons once warm.

The fragment endpoint returns `202 Accepted` while warming. It returns the cached HTML only; it never triggers composition.

Hosts needing authentication or tenant isolation should authorize the mapped hub/endpoint and use tenant-scoped, normalized keys. Do not let untrusted clients join a key that could reveal content they are not entitled to request.

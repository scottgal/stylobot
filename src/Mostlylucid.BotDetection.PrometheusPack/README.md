# Mostlylucid.BotDetection.PrometheusPack

**Optional meter-ingest + metrics surface for the StyloBot dashboard.** An **optional add-on** to `Mostlylucid.BotDetection.UI`: it ships the meter streams (`IMeterStream` — `LocalMeterStream` via a `MeterListener`, `RemoteMeterStream` via Prometheus text scrape) plus the dashboard's meter-health tile, contributed through the UI's `IPackHealthSummaryProvider` seam. The dashboard works without it — installing this pack just adds the metrics surface.

[![NuGet](https://img.shields.io/nuget/v/Mostlylucid.BotDetection.PrometheusPack.svg)](https://www.nuget.org/packages/Mostlylucid.BotDetection.PrometheusPack)
[![GitHub](https://img.shields.io/badge/GitHub-scottgal%2Fstylobot-blue)](https://github.com/scottgal/stylobot)

---

## Install

```bash
dotnet add package Mostlylucid.BotDetection.PrometheusPack
```

## Quick start

```csharp
builder.Services.AddPrometheusPack(opt => opt.Mode = PrometheusPackMode.Local);
```

Two ingest modes (pick ONE — they must not be combined):

- **`Local`** — a `MeterListener` subscribes to process-local `System.Diagnostics.Metrics` meters. The in-gateway mode.
- **`Remote`** — the stream scrapes a gateway's Prometheus `/metrics` endpoint over HTTP. The viewer-host mode; requires `RemoteMeterStreamOptions.BaseUrl`:

```csharp
builder.Services.AddPrometheusPack(opt =>
{
    opt.Mode = PrometheusPackMode.Remote;
    opt.Remote = r => r.BaseUrl = "http://gateway:8080";
});
```

## What it provides

- `IMeterStream` — the read surface (`ListAsync` / `GetAsync`) implemented by `LocalMeterStream` (MeterListener) and `RemoteMeterStream` (text scrape)
- The shared LFU `MeterSummaryAtom` storage and the meter-signals blackboard bridge (`MeterSnapshotSignalContributor`, `MeterSignalCatalogSource`)
- The dashboard **meter-health tile** (via `AddPrometheusPack`, which registers `MeterStreamHealthSummaryProvider` through the UI's `IPackHealthSummaryProvider` seam) and its tick-driven freshness contributor

The pack is a **read-only data plane** — no write surfaces.

## Full documentation

- [stylo.bot](https://stylo.bot) · [GitHub](https://github.com/scottgal/stylobot)

## License

AGPL-3.0-only. See [LICENSE](https://github.com/scottgal/stylobot/blob/main/LICENSE).

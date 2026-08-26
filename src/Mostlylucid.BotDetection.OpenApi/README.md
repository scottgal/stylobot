# Mostlylucid.BotDetection.OpenApi

**OpenAPI document loading for the StyloBot stack.** Wraps `Microsoft.OpenApi.Readers` with caching and `$ref` resolution, exposing the loaded document through `IOpenApiCatalog` / `IOpenApiDocumentLoader`. It powers the dashboard's Routes tab (catalog cross-reference) and is reusable for API Holodeck auto-honeypot generation and spec-audit tooling.

[![NuGet](https://img.shields.io/nuget/v/Mostlylucid.BotDetection.OpenApi.svg)](https://www.nuget.org/packages/Mostlylucid.BotDetection.OpenApi)
[![GitHub](https://img.shields.io/badge/GitHub-scottgal%2Fstylobot-blue)](https://github.com/scottgal/stylobot)

---

## Install

```bash
dotnet add package Mostlylucid.BotDetection.OpenApi
```

This is a dependency of `Mostlylucid.BotDetection.UI` (the dashboard Routes tab) and is pulled in automatically when you install the dashboard. You can reference it directly for your own OpenAPI tooling.

## What it provides

- `IOpenApiCatalog` — a cached catalog of loaded OpenAPI documents
- `IOpenApiDocumentLoader` — loads + caches a document from a URL/stream with `$ref` resolution
- `OpenApiSeedOptions` / `OpenApiStartupSeederService` — seed documents at startup
- `LoadedOpenApiDocument` — the loaded document model

## Usage

```csharp
builder.Services.AddSingleton<IOpenApiCatalog, OpenApiCatalog>();
builder.Services.AddSingleton<IOpenApiDocumentLoader, OpenApiDocumentLoader>();
```

## Full documentation

- [stylo.bot](https://stylo.bot) · [GitHub](https://github.com/scottgal/stylobot)

## License

AGPL-3.0-only. See [LICENSE](https://github.com/scottgal/stylobot/blob/main/LICENSE).

# Migration Guide: v6 to v7 (Package Rename)

## What is changing

On **June 1, 2026**, all Stylobot NuGet packages are being renamed. The package IDs, namespaces, and extension method names all change. The detection pipeline, configuration schema, SQLite schema, and signal keys are unchanged.

## Package ID changes

| Current (v6) | New (v7) |
|---|---|
| `mostlylucid.botdetection` | `stylobot` |
| `mostlylucid.geodetection` | `stylobot.geodetection` |
| `Mostlylucid.BotDetection.UI` | `stylobot.ui` |
| `Mostlylucid.BotDetection.Api` | `stylobot.api` |
| `Mostlylucid.BotDetection.Llm.Holodeck` | `stylobot.llm.holodeck` |

The old packages will be marked as deprecated on NuGet.org at the time of the v7.0 release. They will continue to work but will not receive further updates.

## Namespace changes

All `Mostlylucid.BotDetection.*` namespaces become `Stylobot.*`.

```csharp
// Before (v6)
using Mostlylucid.BotDetection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies;

// After (v7)
using Stylobot;
using Stylobot.Extensions;
using Stylobot.Models;
using Stylobot.Policies;
```

## Migration steps

### 1. Update package references

```xml
<!-- Before -->
<PackageReference Include="mostlylucid.botdetection" Version="6.*" />

<!-- After -->
<PackageReference Include="stylobot" Version="7.*" />
```

Repeat for each package you reference from the table above.

### 2. Update using directives

Run a solution-wide find and replace:

| Find | Replace |
|---|---|
| `using Mostlylucid.BotDetection` | `using Stylobot` |
| `using Mostlylucid.GeoDetection` | `using Stylobot.GeoDetection` |

Most editors support regex find-and-replace across the solution. In Visual Studio: Edit > Find and Replace > Find in Files, with regex enabled.

### 3. Build

The compiler will flag any remaining references. All public API shapes, method signatures, and configuration keys are identical between v6 and v7.

## What is NOT changing

- Configuration schema (`appsettings.json` keys under `BotDetection:`)
- SQLite schema (existing databases work without migration)
- Signal keys (e.g. `signature.primary`, `request.ip.is_datacenter`)
- YAML manifest format for detectors and policies
- The `stylobot` binary (already named correctly)
- Detector behaviour and weights

## Timeline

| Date | Event |
|---|---|
| Now | Deprecation notice added to v6 package descriptions |
| June 1, 2025 | `stylobot` v7.0.0 released; old packages deprecated on NuGet.org |
| No end date | Old packages remain installable; no further updates |

## Questions

Open an issue at https://github.com/scottgal/stylobot/issues
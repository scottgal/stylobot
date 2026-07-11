# Migration Guide: v6 to v7 (License Change)

## What is changing

In **7.0.0**, StyloBot FOSS changes its license: **`Unlicense` (public domain) becomes `GNU AGPL-3.0-only`**. The `LICENSE` file at the repo root is now the canonical AGPLv3 text, and every published NuGet package advertises `PackageLicenseExpression = AGPL-3.0-only`.

The detection pipeline, public API, configuration schema, SQLite schema, signal keys, package IDs, and namespaces are **unchanged**. This is a licensing change, not a code change.

## Practical impact

- **Internal use is unaffected.** If you build on StyloBot and do not distribute the binary or run it as a public-facing service, nothing changes for you.
- **Public-facing services must offer source.** If you incorporate StyloBot's source into a service your users reach over a network, AGPL's network clause (the "A") requires you to offer those users the corresponding source. Static linking, dynamic linking, and the SDK helpers all count as incorporation.
- **`Mostlylucid.GeoDetection.Contributor` stays MIT.** That package is dual-licensed and is not affected.

If either of the first two points affects you, review the `LICENSE` file and your distribution model before upgrading. There is no code, config, or database migration required.

## What is NOT changing

- **Package IDs** stay `Mostlylucid.BotDetection`, `Mostlylucid.BotDetection.ApiHolodeck`, `Mostlylucid.Common`, `Mostlylucid.GeoDetection`, etc. (There is no package rename in v7.)
- **Namespaces** stay `Mostlylucid.BotDetection.*`.
- **Configuration schema** (`appsettings.json` keys under `BotDetection:`).
- **SQLite schema** (existing databases work without migration).
- **Signal keys** (e.g. `signature.primary`, `request.ip.is_datacenter`) and the **YAML manifest format**.
- **Detector behaviour and weights.** FOSS detection is unchanged: every SQLite store stays the default. The v7 identity-layer interfaces only add a swap point for the commercial layer; FOSS behaviour is identical.

## Migration steps

Bump the package version and rebuild. No source changes are required.

```xml
<!-- Before -->
<PackageReference Include="Mostlylucid.BotDetection" Version="6.*" />

<!-- After -->
<PackageReference Include="Mostlylucid.BotDetection" Version="7.*" />
```

## Upgrading further

To move from 7.x to 8.x, see [`upgrade-7-to-8.md`](upgrade-7-to-8.md).

## Questions

Open an issue at https://github.com/scottgal/stylobot/issues.

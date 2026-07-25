# Changelog

All notable changes to the Mostlylucid.BotDetection.Llm.LlamaSharp package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> The **root [`CHANGELOG.md`](../../CHANGELOG.md)** is the authoritative source across the whole solution. Entries below cover the package-visible surface.

## [8.5.0] - 2026-07-25

Full notes in the root [`CHANGELOG.md`](../../CHANGELOG.md#850---2026-07-25).

### Fixed

- **Missing `Microsoft.Extensions.Options.ConfigurationExtensions` package reference.**
  `LlamaSharpServiceExtensions.OptionsBuilder<T>.BindConfiguration()` lives in that package, which
  wasn't referenced (only the base `Microsoft.Extensions.Options` was) — broke a cold restore
  (`CS1061`) of the Gateway SKU build path; a warm local NuGet cache had been masking it.

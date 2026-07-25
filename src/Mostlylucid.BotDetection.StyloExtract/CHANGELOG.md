# Changelog

All notable changes to the Mostlylucid.BotDetection.StyloExtract package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> The **root [`CHANGELOG.md`](../../CHANGELOG.md)** is the authoritative source across the whole solution. Entries below cover the package-visible surface.

## [8.5.0] - 2026-07-25

Full notes in the root [`CHANGELOG.md`](../../CHANGELOG.md#850---2026-07-25).

### Fixed

- **`StyloExtractActionOptions` moved off `IOptionsMonitor<T>` onto `IOptionsFactory<T>`.** The 3
  extract-action policies use named options (`.Get(name)` against 4 `StyloExtract:Actions:*` sections
  from one type); `IOptions<T>` has no named lookup, and these policies are singletons so
  `IOptionsSnapshot<T>` can't inject. Each policy now resolves its named section once at construction
  via `IOptionsFactory<T>.Create(name)` and stores the frozen value, satisfying the FOSS
  no-runtime-options-reload rule without losing named-config support.

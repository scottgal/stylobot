# Changelog

All notable changes to the Mostlylucid.BotDetection.Api package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> The **root [`CHANGELOG.md`](../../CHANGELOG.md)** is the authoritative source across the whole solution. Entries below cover the package-visible surface.

## [8.5.0] - 2026-07-25

Full notes in the root [`CHANGELOG.md`](../../CHANGELOG.md#850---2026-07-25).

### Fixed

- **OpenAPI spec was hiding real error responses.** Endpoints returning
  `Results<..., ProblemHttpResult>` documented only `200` in `/api/v1/openapi.json` across 17 endpoint
  files, dropping every 400/401/503 they could actually return. Fixed at the type level
  (`Endpoints/FixedStatusProblemResults.cs`) — no runtime behavior change, but API consumers who
  codegen a client from the spec were missing real error-handling cases.

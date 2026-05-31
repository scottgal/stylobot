# Contributing to StyloBot

Thanks for your interest in contributing to StyloBot! This document covers the basics.

## Getting Started

1. Fork and clone the repo
2. Install [.NET SDK 10.0](https://dotnet.microsoft.com/download)
3. Build: `dotnet build mostlylucid.stylobot.sln`
4. Run tests: `dotnet test`
5. Run the demo: `dotnet run --project src/Mostlylucid.BotDetection.Demo` -- visit `http://localhost:5080/` for the demo, `http://localhost:5080/dashboard/` for the operator dashboard.

## Development Guidelines

- **No hard-coded site-specific exceptions.** StyloBot is a detection product - the fix is always to make detection *correct*, not to add workarounds or allowlists.
- **All detection improvements must be generic** - based on protocol specs (W3C, RFCs), not site-specific paths or domains.
- **No magic numbers in detectors** - all confidence, weight, and threshold values come from YAML manifest `defaults.parameters` via `GetParam<T>()`.
- **Zero PII** - raw IP addresses and user agents must never be persisted. Use HMAC-SHA256 signatures.

## Adding a Detector

Every new detector touches exactly 5 files. See `CLAUDE.md` for the full checklist, or use `Http3FingerprintContributor` as a reference implementation:

1. C# class in `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/`
2. YAML manifest in `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/`
3. Signal keys in `src/Mostlylucid.BotDetection/Models/DetectionContext.cs`
4. DI registration in `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`
5. Narrative builder entries in `src/Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`

## Adding an Action Policy

Action policies define HOW to respond (block / throttle / rate-limit / challenge / pass) and are separate from detection policies (which define WHAT to detect). The grammar landed in 6.8 -- see [`src/Mostlylucid.BotDetection/docs/policy-defaults.md`](src/Mostlylucid.BotDetection/docs/policy-defaults.md) for the per-`BotType` defaults and [`docs/action-policies.md`](src/Mostlylucid.BotDetection/docs/action-policies.md) for the full grammar.

To add a new action policy class:

1. Implement `IActionPolicy` in `src/Mostlylucid.BotDetection/Actions/`. Declare both `ActionType` (the closest existing enum value) and `Intent` (the new `PolicyIntent` -- `Block` / `RateLimit` / `Throttle` / `Challenge` / `Pass`). Reference: `RateLimitActionPolicy.cs`.
2. Add an options class in the same folder if your policy is configurable -- include static presets so registrations stay one-line. Reference: `RateLimitActionOptions.cs`.
3. Register one or more named instances in `ActionPolicyRegistry.RegisterBuiltInPolicies()` (`src/Mostlylucid.BotDetection/Actions/ActionPolicyRegistry.cs`).
4. If your policy holds runtime state that's worth surfacing on the dashboard policy tab, extend `RegistryPolicyStateProvider.ToState()` to populate `EffectiveParams` for your policy class.
5. Tests in `src/Mostlylucid.BotDetection.Test/Actions/` -- pin the intent, the per-method behaviour, and (for policies that compose with others) the fallback path.

## Pull Requests

- Keep PRs focused - one feature or fix per PR
- Include tests for new detection logic
- Run `dotnet test` before submitting
- Update detector YAML manifests if you change default weights or thresholds
- Update `CHANGELOG.md` under the `[Unreleased]` section (add one if none exists)
- For changes that touch action policies or the per-`BotType` default mapping, also update [`src/Mostlylucid.BotDetection/docs/policy-defaults.md`](src/Mostlylucid.BotDetection/docs/policy-defaults.md) -- it's the canonical "what stylobot does out of the box" reference

## Running Tests

```bash
# All tests
dotnet test

# Specific project
dotnet test src/Mostlylucid.BotDetection.Test/
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/

# Single test class
dotnet test --filter "FullyQualifiedName~UserAgentDetectorTests"

# BDF replay campaign (against a running demo at localhost:5080)
bash test-suites/run-tests.sh
```

## Reporting Issues

Open an issue on [GitHub](https://github.com/scottgal/stylobot/issues). Include:

- What you expected vs what happened
- Steps to reproduce
- Relevant log output or detection signals
- .NET version and OS

## License

By contributing, you agree that your contributions will be released under the [GNU AGPLv3](LICENSE).

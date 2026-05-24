# Scanner-path catalog consolidation (partial -- steps 1, 2, dashboard label rewrite done)

> Originally captured as agent memory after the 6.7.5 honeypot landing. Materialised here so the duplication audit isn't trapped in agent memory and a future contributor can pick up the refactor without re-discovering all twelve files.

## Status (2026-05-24)

- **Done in 6052f0f**: step 1 (`HoneypotCategory` enum + every catalog entry tagged) and step 2 (`ClassifyDetailed` returns `(Tier, Category, Pattern)`; `CategoryForPattern`, `GetPathsByCategory`, `GetAllPaths` added). 52 new tests pin the mapping.
- **Done in 39c7fa0**: the dashboard slice of step 5 -- `SqliteDashboardEventStore.IntentForPath`'s 45-line string-matcher is replaced by a category-keyed `LabelForCategory` table; `HoneypotHitRow` surfaces `Category`; the Honeypot tab renders a colour-grouped category chip. 16 new tests pin the labels.
- **Remaining**: the Haxxor YAML category lists (step 3), the `ResponseCoordinator` default (step 4), `EndpointRiskClassifier` (step 5 cont.), inline references in `SignatureToBdfMapper` / `HeuristicFeatureExtractor` / `ThreatIntelContributor` (step 6), and the `AdditionalCatalogFiles` extension surface (step 7).
- The operator-facing reference for the now-landed catalog lives at [`src/Mostlylucid.BotDetection/docs/honeypot-catalog.md`](../../src/Mostlylucid.BotDetection/docs/honeypot-catalog.md).

## The problem

Twelve files carry overlapping scanner-path knowledge. `/.env`, `/wp-login.php`, `/wp-admin`, `/.git/config`, `/phpmyadmin`, `/backup.sql` each appear in 4–7 places. Adding a new path today means picking one or two files at random and letting the rest drift. Catalog drift is invisible until production.

## Where the duplication lives

| File | Role | Notes |
|---|---|---|
| `src/Mostlylucid.BotDetection/Honeypot/HoneypotPathDefinitions.cs` | Canonical catalog (Tier 1 / 2 / suspicious extensions, glob-aware) | Should be the single source of truth |
| `src/Mostlylucid.BotDetection/Honeypot/HoneypotDetectionOptions.cs` | `ExemptPaths`, `AdditionalPaths` (operator overrides) | OK -- operator-facing |
| `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/haxxor.detector.yaml` | Six categorised path lists: `path_probes` / `webshell_patterns` / `backup_patterns` / `admin_patterns` / `debug_patterns` / `config_patterns` | **Heaviest duplicate**; the consolidation target |
| `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/behavioral.detector.yaml`, `accounttakeover.detector.yaml` | Inline path mentions | Replace with catalog references |
| `src/Mostlylucid.BotDetection/Orchestration/ResponseCoordinator.cs` | 19-pattern `HoneypotPaths` default list | Derive from catalog |
| `src/Mostlylucid.BotDetection/SimulationPacks/Packs/wordpress.yaml` | Per-CVE response templates | **Different concern -- leave alone.** Catalog says "scanner-shaped"; sim packs say "if you must serve a fake, here's what it looks like" |
| `src/Mostlylucid.BotDetection/ThreatIntel/EndpointRiskClassifier.cs` | Path-category switch | Replace with `Classify(...)` call |
| `src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs` | CVE-severity-by-path heuristic (`StartsWith` chain) | Replace with `Classify(...)` call |
| `src/Mostlylucid.BotDetection/Behavioral/SignatureToBdfMapper.cs`, `Detectors/HeuristicFeatureExtractor.cs`, `Orchestration/ContributingDetectors/ThreatIntelContributor.cs` | Inline references | Constants from the catalog |

## Planned consolidation

1. Add `HoneypotCategory` enum (`Credentials` / `Config` / `VersionControl` / `Database` / `Webshell` / `Admin` / `Debug` / `Backup`) and tag every entry in `HoneypotPathDefinitions`.
2. Extend `Classify` to return `(Tier, Category, Pattern)`; add `GetPathsByCategory(...)` + `GetAllPaths()`.
3. Delete the six YAML path lists from `haxxor.detector.yaml`; `HaxxorContributor` reads from the catalog by category. YAML keeps weights/thresholds only.
4. `ResponseCoordinator.HoneypotPaths` default → derive from `GetAllPaths()`. Operator override preserved.
5. `EndpointRiskClassifier`, `SqliteDashboardEventStore` CVE inference → call `Classify(...)`.
6. Inline references in `SignatureToBdfMapper` / `HeuristicFeatureExtractor` / `ThreatIntelContributor` → constants from the catalog.
7. New FOSS extension surface: `HoneypotDetectionOptions.AdditionalCatalogFiles: List<string>` pointing at YAML files (paths + category + description + optional tier override), same pattern as simulation packs. Commercial dynamic-honeypot writes into the same in-memory store via a different writer.

## How to apply

Ship as **two commits**:

1. Catalog-with-categories first -- no behavioural change, just data structure + tests asserting every Tier 1 / 2 entry has a category.
2. Consumer rewrites, file by file.

**Don't bundle simulation packs into the refactor** -- they're a separate concern. Net code reduction once landed; one place to add a path.

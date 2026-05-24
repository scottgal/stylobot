# Honeypot Path Catalog

The honeypot subsystem watches a curated catalog of scanner-shaped paths and,
when a request hits one, raises the bot probability ceiling and routes the
response through a stealth action policy. This doc covers the catalog
structure, the category taxonomy, and how the dashboard surfaces them.

## Tier model

The catalog distinguishes two tiers plus a generic suspicious-extension
fallback. The exhaustive list lives in
`Mostlylucid.BotDetection/Honeypot/HoneypotPathDefinitions.cs` -- a
single `_catalog` array of `(Tier, Category, Paths)` tuples that every
helper derives from.

| Tier | Meaning | Exempt-able by operators |
|------|---------|--------------------------|
| **Tier 1** (`Always`) | Zero-FP credential / key / dump probes (e.g. `/.aws/credentials`, `/etc/passwd`, `/id_rsa`). On a real site these paths never exist. | No. Operator overrides are ignored for Tier 1 paths so a misconfigured exempt list can't silently re-enable credential theft. |
| **Tier 2** (`Probable`) | Vendor-specific paths that are scanner-shaped on most stacks but legitimate on some (e.g. `/wp-login.php`, `/swagger.json`, `/grafana`). | Yes. `BotDetection:Honeypot:ExemptPaths` (+ trailing-`*` globs) suppresses Tier 2 elevation per path. |
| `SuspiciousExtensions` | Extension-level catch-all (`.sql`, `.bak`, `.pem`, `.sqlite`, `.ini`, `.log`, ...). Treated as Tier 2; carries a category derived from the extension. | Yes. |

## Categories

Every catalog entry (Tier 1, Tier 2, suspicious-extension fallback) carries
a `HoneypotCategory` from `Mostlylucid.BotDetection.Honeypot.HoneypotCategory`:

| Category | Examples | Operator-facing label |
|----------|----------|-----------------------|
| `Credentials` | `/.aws/credentials`, `/.ssh/id_rsa`, `/.kube/config`, `/id_rsa`, `/credentials.json`, `*.pem`, `*.key` | credentials theft |
| `Config` | `/.env*`, `/wp-config.php.bak`, `/web.config`, `/appsettings.json`, `*.ini` | config file leak |
| `VersionControl` | `/.git/config`, `/.git/HEAD`, `/.svn/entries`, `/.hg/*` | version-control exposure |
| `Database` | `/phpmyadmin`, `/adminer.php`, `*.sql`, `*.sqlite` | database admin probe |
| `Webshell` | `/c99.php`, `/r57.php`, `/backdoor.php`, `/alfa.php` | webshell upload |
| `Admin` | `/wp-admin`, `/wp-login.php`, `/grafana`, `/actuator` | admin-panel probe |
| `Debug` | `/elmah.axd`, `/phpinfo.php`, `/trace.axd` | debug-endpoint probe |
| `Backup` | `/backup.sql`, `/site.tar.gz`, `*.bak`, `*.log` | database/backup dump |
| `Metadata` | `/latest/meta-data`, `/computeMetadata/v1` | metadata SSRF probe |
| `PathTraversal` | `/etc/passwd`, `/proc/self/environ`, `/windows/win.ini` | path-traversal probe |
| `BuildArtifact` | `/composer.lock`, `/.idea`, `/.DS_Store` | build-artifact probe |
| `ApiDoc` | `/swagger.json`, `/v3/api-docs` | api-doc enumeration |
| `Cgi` | `/cgi-bin/*` | CGI probe |
| `Cms` | `/sites/default/files` | CMS probe |
| `None` | sentinel for non-catalog paths | (falls back to tier-derived label) |

The category is the single source of truth for "what is the scanner going
for?". The dashboard label and the colour chip are derived from it; there
is no parallel string-matching layer.

## Public API

Five entry points on `HoneypotPathDefinitions`:

```csharp
// 1. Detailed classification -- returns tier + category + matched pattern.
ClassificationResult result = HoneypotPathDefinitions.ClassifyDetailed("/.aws/credentials");
// result.Tier     == HoneypotTier.Always
// result.Category == HoneypotCategory.Credentials
// result.Pattern  == "/.aws/credentials"

// 2. Bare tier (back-compat for older call sites).
var tier = HoneypotPathDefinitions.Classify("/.env", out string? matched);

// 3. Category lookup for a known pattern.
var category = HoneypotPathDefinitions.CategoryForPattern("/.aws/credentials");

// 4. Paths by category (e.g. all Webshell entries).
FrozenSet<string> webshells = HoneypotPathDefinitions.GetPathsByCategory(HoneypotCategory.Webshell);

// 5. All known paths across every tier (deduplicated).
IReadOnlyCollection<string> all = HoneypotPathDefinitions.GetAllPaths();
```

`ClassifyDetailed` is the preferred entry point for new code -- one
catalog lookup yields everything the dashboard, the rate limiter, the
holodeck, and the threat report need.

## Dashboard surface

The Honeypot tab under `/_stylobot` (rendered by
`Views/StyloBot/Dashboard/_InvestigateHoneypot.cshtml`) renders one row
per distinct honeypot path hit in the current window. Each row carries
a colour-grouped category chip alongside the tier badge:

| Chip colour | Category set | Risk grouping |
|-------------|--------------|---------------|
| **Red** | `Credentials` | Highest -- key / token theft |
| **Orange** | `Config`, `VersionControl` | Source / secret leak |
| **Yellow** | `Database`, `Admin`, `Debug`, `Backup` | Lateral movement / admin probe |
| **Purple** | `Webshell`, `PathTraversal`, `Metadata` | RCE / SSRF |
| **Grey** | everything else | Catalog hit, lower priority |

The row also carries a `data-category` attribute so future CSS / JS
filters can hide categories the operator isn't interested in without a
server round-trip.

## Adding a new path

1. Add the literal path to the appropriate tuple in
   `HoneypotPathDefinitions._catalog`. Pick the tier (Tier 1 only if you
   are *certain* the path never serves real content on any reasonable
   stack) and the category.
2. If the category is brand new, add it to the `HoneypotCategory` enum
   *and* the `LabelForCategory` table in
   `Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs`.
   The `EveryEnumValue_HasANonEmptyLabel` test will fail if you forget.
3. Run `dotnet test --filter "FullyQualifiedName~Honeypot"` -- the
   `EveryCatalogEntry_HasANonNoneCategory` test guards against a missing
   category and the per-category theory pins the expected mapping.

## See also

- `docs/deferred/scanner-path-catalog-consolidation.md` -- remaining
  consumers (Haxxor YAML, ResponseCoordinator, EndpointRiskClassifier,
  inline references in Behavioral / Heuristic / ThreatIntel) that
  should switch from inline string lists to catalog lookups.
- `src/Mostlylucid.BotDetection/docs/endpoint-pinning.md` -- the
  per-endpoint operator override surface that wraps the catalog.

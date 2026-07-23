# Per-host site profiles (deferred)

> Originally captured as agent memory during the path-catalog consolidation discussion that produced `feat/site-profiles`. Materialised here so the design isn't trapped in agent memory.

## What it is

YAML-driven `host → stack` mappings so the honeypot subsystem knows that `/wp-login.php` is real on a WordPress host but a scanner target everywhere else.

## Scope

**Only the honeypot exempt list.** Profile-driven `framework_paths` skip **Tier 2** honeypot elevation; they do NOT gate detection. Haxxor, behavioural, auth-takeover, rate-limit all still run normally on those paths. The mechanical change is `IHoneypotExemptStore.IsExempt(path, ctx)` consulting the resolved profile -- nothing else in the orchestrator is touched.

## Orthogonal to simulation packs

- `SimulationPacks/Packs/*.yaml` = fake-response templates per CVE.
- `SiteProfiles/*.yaml` = per-host honeypot-config modulation.

Different files, different read sites. Profiles MAY reference suggested packs by id as a cross-sell -- "you're running WordPress, the `wordpress` pack would serve plausible fakes" -- but there's no functional dependency.

## Files when picked up

- `Mostlylucid.BotDetection/SiteProfiles/SiteProfile.cs` -- POCO + VYaml binding.
- `Mostlylucid.BotDetection/SiteProfiles/ISiteProfileResolver.cs` + implementation with frozen exact-host map + small wildcard list (`*.staging.example.com`, `api.*.example.com`). First-match wins.
- `Mostlylucid.BotDetection/SiteProfiles/Profiles/*.yaml` -- ~12 embedded YAMLs (wordpress, drupal, magento, aspnet, aspnet-mvc, django, rails, spring-boot, laravel, nextjs, nuxt, ghost, strapi).
- `appsettings.json` schema under `BotDetection:Sites:{Default,Domains[]}` or a separate `sites.yaml`.
- `IHoneypotExemptStore.IsExempt` extended with optional `HttpContext` so it can look up the per-host profile.
- Dashboard chip showing resolved profile + suggested-pack pills (FOSS read-only, commercial editable).

## YAML shapes

Site profile:

```yaml
id: wordpress
name: WordPress
honeypot:
  framework_paths:           # SKIP Tier 2 honeypot elevation only; detection still runs
    - /wp-login.php
    - /wp-admin*
    - /xmlrpc.php
    - /wp-cron.php
  additional_tier1:          # promoted to Tier 1 on this host
    - /wp-config.php.bak
    - /wp-config.php.save
    - /wp-content/debug.log
  elevated_tier2:            # not in global catalog; flag on this stack
    - /wp-content/plugins/elementor*
expected_bots:
  - googlebot
  - bingbot
  - jetpack
suggested_packs:             # cross-sell surface in the dashboard
  - id: wordpress
    description: Realistic 404/login fakes for WP probes
    tier: foss
  - id: wordpress-cve-bundle
    description: Per-CVE response templates, refreshed monthly
    tier: commercial
```

`sites.yaml` (host mapping):

```yaml
default_profile: generic
sites:
  - host: stylo.bot
    profile: aspnet
  - host: blog.example.com
    profile: wordpress
  - host: "*.staging.example.com"
    profile: aspnet
```

## FOSS vs commercial split

- **FOSS:** ~12 embedded profile YAMLs, operator-edited `sites.yaml`, applied via `/admin/restart` or a redeploy (no runtime options-reload in FOSS), dashboard chip read-only.
- **Commercial:** live profile editing in the dashboard, per-tenant overrides, optional passive auto-detection that *suggests* a profile when none is mapped (uses `PathLifecycleStore` 2xx fingerprints + cookie/header fingerprints), pack-install flow.

## Why operator-declared beats auto-detection

No learning loop, no confidence scoring, no sliding window -- operator declares, we honour. Auto-detect is the optional commercial second tier.

## Effort

When tackled, ship as one branch, ~1.5 days, mostly YAML content. `SimulationPacks/Packs/` currently only has `wordpress.yaml`; an `aspnet.yaml` pack would be a natural companion to ship alongside.

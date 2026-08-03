# Dashboard Authentication

StyloBot's dashboard authorization has two independent, optional, config-driven layers. Both gate **access/viewing** only — neither is the commercial write moat (which stays separately gated on top).

| Layer | Gates | Configured by |
|-------|-------|---------------|
| **Interactive login** | The human-facing `/{BasePath}/*` HTML dashboard | `StyloBot:Dashboard:Auth` (this doc) |
| **Gateway API key** | The machine `/api/v1/*` read endpoints | `BotDetection:ApiKeys` (see [api-keys.md](api-keys.md)) |

This doc covers the interactive login. For the API-key access layer, see [api-keys.md → Gateway API access authorization](api-keys.md#gateway-api-access-authorization-apiv1).

## What it is

A styled login page + cookie authentication for the FOSS dashboard, verifying a **single username + password hash held in config/env** — no user database, no identity provider. Opt-in: the dashboard's existing behaviour is unchanged until you configure it.

```json
{
  "StyloBot": {
    "Dashboard": {
      "Auth": {
        "Mode": "Login",
        "Username": "admin",
        "PasswordHash": "AQAAAAIAAYag...(from the hash CLI)..."
      }
    }
  }
}
```

- **`Mode`** — `None` (default, inert) or `Login`.
- **`Username`** — the single login username.
- **`PasswordHash`** — a PBKDF2 hash produced by the hash CLI (below). **Never a plaintext password.**
- **`CookieName`** (default `sb.dashboard.auth`) and **`SlidingExpirationMinutes`** (default `480`) are tunable.

All keys bind from environment variables via the standard double-underscore convention, e.g. `StyloBot__Dashboard__Auth__PasswordHash`.

## Generating the password hash

Never write a plaintext password into config. Generate the hash with the CLI:

```bash
stylobot dashboard hash-password
# Password: ******
# Confirm : ******
# Set StyloBot:Dashboard:Auth (Mode=Login, Username=...) and paste this as PasswordHash:  (stderr)
AQAAAAIAAYagAAAAE...                                                                       (stdout)
```

The hash is printed to **stdout** (prompts/guidance go to stderr), so you can pipe it: `stylobot dashboard hash-password > hash.txt`. For scripted/CI use, pass `--password <pw>` to skip the prompt.

## Behaviour

- **Unconfigured (`Mode: None`):** inert. The dashboard's existing authorization (`RequireAuthentication` / `AllowUnauthenticatedAccess` / dev-vs-prod default) is untouched. A **startup warning** nudges you to configure a login (or explicitly allow open access).
- **`Mode: Login` with a credential:** unauthenticated HTML requests redirect to `/{BasePath}/login`; a correct login POST issues a sliding auth cookie; `/{BasePath}/logout` clears it. Dashboard data/partial requests (`/{BasePath}/api/*`, `/{BasePath}/partials/*`) get `401` instead of an HTML redirect.
- **`Mode: Login` but `Username`/`PasswordHash` incomplete:** login is **not** enforced (can't verify an incomplete credential) and a startup warning says so. There is no baked-in default credential.

The login page is styled to the dashboard chrome (Tailwind + daisyUI, light/dark aware) and protects the login POST with a double-submit CSRF token.

## Scope — what it does and does not gate

**Gates:** the human-facing `/{BasePath}/*` HTML routes only.

**Does NOT gate:**
- The machine `/api/v1/*` read endpoints — those are API-key gated (`BotDetection:ApiKeys`); the marketing site's `RemoteDashboardEventStore` and the gateway depend on them.
- The detection pipeline.
- The commercial edit/write moat (separately gated, on top of this).

## Commercial OIDC layers on — no fork

FOSS registers a cookie authentication scheme satisfying the well-known authorization policy **`stylobot-dashboard-view`**, which the dashboard middleware enforces. Commercial OIDC (Keycloak/SSO) layers on by registering its own authentication scheme into that **same** policy — it does not replace or fork the FOSS default:

- FOSS ships the login+cookie default fully working with zero commercial packages.
- Commercial adds `AddOpenIdConnect(...)` and augments the `stylobot-dashboard-view` policy's accepted schemes; the middleware evaluates the policy (via `IPolicyEvaluator`), so both schemes are honoured together.
- This removes the older limitation where OIDC and the built-in `RequireAuthentication` (SQLite user-store) path were mutually exclusive.

## Relationship to the existing `RequireAuthentication` path

`StyloBot:Dashboard:RequireAuthentication = true` is a separate, heavier auth mode: full ASP.NET Core Identity with a SQLite `dashboard_users` table and a `/setup` first-run admin flow. Use `Auth:Mode = Login` (this feature) for the simple, static, single-credential case; use `RequireAuthentication` when you need a multi-user database. They are alternates — do not enable both.

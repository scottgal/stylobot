# Dashboard Authentication

StyloBot's dashboard authorization has two independent layers. Both gate **access/viewing** only — neither is the commercial write moat (which stays separately gated on top).

| Layer | Gates | Configured by |
|-------|-------|---------------|
| **Interactive login** | The human-facing `/{BasePath}/*` HTML dashboard | `StyloBot:Dashboard:Auth` (this doc) |
| **Gateway API key** | The machine `/api/v1/*` read endpoints | `BotDetection:ApiKeys` (see [api-keys.md](api-keys.md)) |

## Posture matrix

The dashboard's authorization posture is the intersection of three independent knobs:

| Knob | Default | Effect |
|------|---------|--------|
| `AllowUnauthenticatedAccess` | `false` | When `true`: dashboard is publicly viewable with no gate |
| `Auth:Mode` | `None` | `Login` enables the config-credential cookie gate |
| `RequireAuthentication` | `false` | Enables ASP.NET Core Identity with SQLite user store |

The **effective posture** at startup:

| Config | Result | Startup log |
|--------|--------|-------------|
| Nothing configured | **Dashboard locked** — 403 in production, auto-allow in Development | `ERROR: SECURITY: Dashboard has no view-auth configured…` |
| `AllowUnauthenticatedAccess: true` | Dashboard open — public read-only | Silent |
| `Auth:Mode=Login` + credential | Login page gates HTML routes; cookie auth | Silent |
| `Auth:Mode=Login` without credential | Dashboard locked (login can't verify) | `ERROR: SECURITY: …Login but Username/PasswordHash are not both set…` |
| `RequireAuthentication: true` | Identity API endpoints + `/setup` first-run flow | Silent |
| Both `RequireAuthentication` AND `Auth:Mode=Login` | Startup exception (`InvalidOperationException`) | Crash |

**The dashboard is locked by default.** The startup advisory (`DashboardAuthPosture`) fires at `LogError` level — red in production logs — if no gate is configured. To open the dashboard, you must explicitly set one of: `AllowUnauthenticatedAccess=true`, `Auth:Mode=Login` with a credential, or `RequireAuthentication=true`.

## Demo mode (public read-only, write gated)

The stylo.bot site runs in **demo mode**: `AllowUnauthenticatedAccess: true` so anyone can view the dashboard, but edit/write controls remain gated behind authentication at the commercial layer. Edit controls render as visual affordances but `LicenseAwarePolicyCanEditPolicy` gates actual writes — the "show but don't let them use" pattern. This is a commercial concern; FOSS has no write controls to gate.

## Config-credential Login mode

A styled login page + cookie authentication verifying a **single username + PBKDF2 password hash held in config/env** — no user database, no identity provider.

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

- **`Mode`** — `None` (default) or `Login`.
- **`Username`** — the single login username.
- **`PasswordHash`** — a PBKDF2 hash produced by the hash CLI. **Never a plaintext password.**
- **`CookieName`** (default `sb.dashboard.auth`) and **`SlidingExpirationMinutes`** (default `480` = 8h) are tunable.

All keys bind from environment variables via the standard double-underscore convention, e.g. `StyloBot__Dashboard__Auth__PasswordHash`.

## Generating the password hash

```bash
stylobot dashboard hash-password
# Password: ******
# Confirm : ******
# Set StyloBot:Dashboard:Auth (Mode=Login, Username=...) and paste this as PasswordHash:  (stderr)
AQAAAAIAAYagAAAAE...                                                                       (stdout)
```

The hash is printed to **stdout** (prompts go to stderr). Pipe it: `stylobot dashboard hash-password > hash.txt`. For CI/scripts: `--password <pw>`.

## Login behaviour

- **`Mode=Login` with a credential:** unauthenticated HTML requests redirect to `/{BasePath}/login`; a correct login POST issues a sliding auth cookie; `/{BasePath}/logout` clears it. Dashboard data/partial requests (`/{BasePath}/api/*`, `/{BasePath}/partials/*`) get `401 JSON` instead of an HTML redirect.
- **`Mode=Login` but `Username`/`PasswordHash` incomplete:** login is **not** enforced (can't verify an incomplete credential) and a startup error says so. There is no baked-in default credential.
- The login page is styled to the dashboard chrome (Tailwind + daisyUI, theme-aware) and protects the login POST with a double-submit CSRF token (cookie `sb.login.csrf`).

## Scope — what it gates

**Gates:** the human-facing `/{BasePath}/*` HTML routes.

**Does NOT gate:**
- The machine `/api/v1/*` read endpoints — those are API-key gated (`BotDetection:ApiKeys`).
- The detection pipeline.
- The commercial edit/write moat (separately gated).

## Commercial OIDC layers on — no fork

FOSS registers a cookie scheme satisfying the authorization policy **`stylobot-dashboard-view`**, which the dashboard middleware enforces via `IPolicyEvaluator`. Commercial OIDC layers on by adding its own scheme to the **same** policy — it does not replace or fork FOSS:

- FOSS ships the login+cookie default fully working with zero commercial packages.
- Commercial adds `AddOpenIdConnect(...)` and augments `stylobot-dashboard-view`'s accepted schemes; `IPolicyEvaluator` honours both together.
- This replaces the older limitation where OIDC and `RequireAuthentication` were mutually exclusive.

## `RequireAuthentication` (Identity user-store)

`StyloBot:Dashboard:RequireAuthentication = true` enables full ASP.NET Core Identity with a SQLite `dashboard_users` table and a `/{BasePath}/setup` first-run admin flow. Use `Auth:Mode=Login` for the simple single-credential case; use `RequireAuthentication` when you need a multi-user database. **They are mutually exclusive** — enabling both throws `InvalidOperationException` at startup.

# Dashboard Auth Architecture

**Date:** 2026-05-02  
**Status:** Design / pre-implementation

---

## Problem

The dashboard at `/_stylobot` currently ships with `AllowUnauthenticatedAccess = true` as the only protection mechanism. Production deployments need authenticated access. There are two distinct access patterns that need different solutions:

1. **Human operators** browsing the dashboard UI
2. **Machine clients** (sidecar, gateway) calling the dashboard REST API

These must be solved independently.

---

## Two-layer architecture

### Layer 1: Human dashboard access

**FOSS:** ASP.NET Core Identity with the .NET 8+ bearer token API (`AddIdentityApiEndpoints<StyloBotUser>()` + `MapIdentityApi<StyloBotUser>()`).

This gives for free, with no external dependencies:
- `POST /_stylobot/auth/register` - create account
- `POST /_stylobot/auth/login` - returns bearer + refresh token
- `POST /_stylobot/auth/refresh` - refresh token rotation
- `POST /_stylobot/auth/forgotPassword` - initiates email reset flow
- `POST /_stylobot/auth/resetPassword` - completes reset
- `POST /_stylobot/auth/manage/2fa` - enable/disable 2FA
- Email MFA codes via `IEmailSender<StyloBotUser>`

User store: `dashboard_users` table in the existing SQLite database. No separate database, no EF migrations to run - the table is created by StyloBot on first start alongside `sessions`, `signatures`, etc.

**Commercial:** OIDC RP. `dashboard.OidcAuthority = "https://..."` replaces the Identity layer entirely. "Bring your own auth" (SSO, LDAP, SAML) is a commercial feature. The FOSS package does not expose OIDC configuration options.

```csharp
// FOSS - self-contained, no external auth provider
builder.Services.AddStyloBot(dashboard => {
    dashboard.RequireAuthentication = true;
    // IEmailSender<StyloBotUser> registered separately (see below)
});

// Commercial - OIDC replaces Identity
builder.Services.AddStyloBot(dashboard => {
    dashboard.RequireAuthentication = true;
    dashboard.OidcAuthority = "https://auth.company.com";
    dashboard.OidcClientId = "stylobot-dashboard";
    dashboard.OidcClientSecret = "...";
});
```

### Layer 2: Machine API access

`X-SB-Api-Key` header - already exists in the product as Tier 2 of the API auth model. Works independently of how dashboard human auth is configured. A gateway that POSTs detection results to the dashboard API, or a sidecar that reads `/api/summary`, uses an API key - not a dashboard login.

API keys are scoped:
- **Read key**: GET endpoints only (`/api/summary`, `/api/detections`, `/api/topbots`, etc.)
- **Write key**: detection ingestion endpoints (gateway push)
- **Admin key**: full access including config endpoints

Keys are stored in `dashboard_api_keys` in SQLite. Generated via `/_stylobot/settings/api-keys` (authenticated dashboard UI) or via a first-run CLI seed.

---

## Bootstrap: first-run setup

`GET /_stylobot/setup` - accessible only when `dashboard_users` table is empty. Renders a form to create the first admin account. After the first user is created, the route returns 404 permanently.

This is the WordPress pattern. No CLI tools required, no env var secrets, no pre-seeding.

```
First deploy → visit /_stylobot/setup → create admin account → setup route disappears
```

If `AllowUnauthenticatedAccess = true` (dev default), setup is skipped entirely and the dashboard is open.

---

## Email integration

`IEmailSender<StyloBotUser>` is the standard ASP.NET extensibility point. StyloBot registers a no-op dev sender by default (logs to console, does not send). Users swap it out:

```csharp
// MailKit SMTP
builder.Services.AddTransient<IEmailSender<StyloBotUser>, MailKitEmailSender>();

// SendGrid
builder.Services.AddTransient<IEmailSender<StyloBotUser>, SendGridEmailSender>();

// Any IEmailSender<StyloBotUser> implementation
```

Email is required for:
- Account confirmation (configurable - can disable for internal deployments)
- Password reset
- 2FA codes (if enabled; authenticator app is also supported)

If no `IEmailSender` is registered beyond the dev no-op, 2FA and password reset still work but only via authenticator app. Email-based flows will log a warning in development.

---

## What is configurable vs fixed

| Concern | Configurable | Fixed |
|---|---|---|
| Bearer token lifetime | `BearerTokenOptions` | - |
| Refresh token lifetime | `BearerTokenOptions` | - |
| Password policy | `IdentityOptions.Password` | - |
| Account lockout | `IdentityOptions.Lockout` | - |
| Email sender | `IEmailSender<StyloBotUser>` | - |
| User store | `IUserStore<StyloBotUser>` (TryAdd) | SQLite default |
| Token provider | `IUserTwoFactorTokenProvider` | - |
| API key scopes | per-key config in dashboard UI | - |
| 2FA requirement | per-user or global policy | - |
| OIDC (commercial only) | `dashboard.OidcAuthority` | - |
| "Bring your own auth" (FOSS) | - | Not available in FOSS |

Everything that ASP.NET Identity exposes via `IdentityOptions`, `BearerTokenOptions`, and `TryAddSingleton` is overridable. StyloBot does not seal or hide any of these. Document each override point explicitly in `dashboard-auth.md`.

---

## Middleware ordering

```
Request
  ↓
BroadcastMiddleware          (always first - records all detections)
  ↓
BotDetectionMiddleware       (detection pipeline)
  ↓
AuthenticationMiddleware     (validates bearer token or OIDC)
  ↓
AuthorizationMiddleware      (requires authenticated user for /_stylobot/* except /auth/*)
  ↓
StyloBotDashboardMiddleware  (renders dashboard UI and API)
```

`/_stylobot/auth/*` endpoints are excluded from authorization (they are how you get a token). All other `/_stylobot/*` routes require an authenticated user (or `AllowUnauthenticatedAccess = true`).

`/_stylobot/api/*` endpoints additionally accept `X-SB-Api-Key` as an alternative to bearer. This is checked in dashboard middleware before the authorization middleware short-circuits the request.

---

## What this is NOT

- **Not a user management system.** Dashboard users are operators, not the application's end users. The `dashboard_users` table is separate from anything the host application does with identity.
- **Not multi-tenant.** All dashboard users see all data. Role-based filtering (read-only vs admin) is a separate concern, not in scope here.
- **Not a commercial auth integration.** OIDC, LDAP, SAML, Azure AD - none of these are in the FOSS package. They are the commercial upsell.

---

## Implementation sequence

1. `StyloBotUser` model + `dashboard_users` SQLite table creation on startup
2. `AddIdentityApiEndpoints<StyloBotUser>()` registration in `AddStyloBotDashboard()`
3. `MapIdentityApi<StyloBotUser>()` mounted at `/_stylobot/auth`
4. Middleware ordering update (authentication + authorization before dashboard middleware)
5. `/_stylobot/setup` first-run route
6. `dashboard_api_keys` table + key generation UI
7. `X-SB-Api-Key` enforcement in dashboard API middleware
8. Dev no-op `IEmailSender<StyloBotUser>` + documentation of override
9. `dashboard-auth.md` doc covering all override points
10. Commercial: `dashboard.OidcAuthority` path (separate package)

---

## Related

- [api-keys.md](../../Mostlylucid.BotDetection/docs/api-keys.md) - existing API key system
- [integration-levels.md](../../Mostlylucid.BotDetection/docs/integration-levels.md) - auth tiers overview
- Memory: `project_dashboard_auth.md` - decision record

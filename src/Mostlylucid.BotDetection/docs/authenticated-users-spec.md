# Authenticated User Identity & Per-User Detection

**Status:** Draft v1 (2026-04-16)
**Scope:** Describes how StyloBot extracts the authenticated user identity from diverse application auth schemes, how per-user behavioral baselines and policies work, how failed-login / phishing detection is layered on top, and the UX across OSS/Startup/SME/Enterprise tiers.
**Privacy:** Authenticated user IDs are PII. Default behavior: HMAC-hash immediately in memory, persist only the hash - same model we use for IPs and UAs. Plaintext retention is an explicit opt-in per deployment.

## Why this matters

Most bot defenses stop at the perimeter: "is this request from a bot?" But customers' real pain is farther down the stack:

- **Account takeover (ATO):** an attacker credential-stuffs their way into one real user's account. The request LOOKS human after login - correct session cookie, plausible fingerprint. Per-request detection misses this because the "request" is valid; the anomaly is that *this particular authenticated user* suddenly hits 47 endpoints in two minutes from Lagos instead of their usual 3 from London.
- **Phishing kit pivots:** user clicks phishing link, enters credentials into a lookalike page, attacker now has the session cookie and replays it. The replayed session starts behaving wildly differently from the original user's behavioral vector.
- **VIP customer friction:** a B2B SaaS has a paid-tier customer who's hitting the API hard. The generic "high rate → throttle" rule is firing on them. The operator wants: "for `user_id in (verified_enterprise_accounts)` allow 10× the rate."
- **Password spraying:** attacker tries `password123` across 10,000 accounts. No single account sees many failures, so per-account fail2ban misses it. But across the platform, the pattern is unmistakable.

Unlocking all of this requires StyloBot to *know who the user is when they're authenticated*.

## Architecture - identity extraction flow

```
Request arrives
    ↓
  BotDetectionMiddleware (existing)
    ↓
  [new] AuthenticatedIdentityExtractor - configured per deployment
    ├── Tries configured identity sources in order:
    │     1. Bearer JWT → decode (no verify) → extract configured claim
    │     2. Cookie → if claim-bearing JWT inside, decode; else hash cookie value
    │     3. Header (e.g., X-User-Id) → take value
    │     4. QueryString param → take value
    ├── Each source emits `auth.user_id_hash` (HMAC-SHA256)
    └── Plus optional plaintext signal `auth.user_id_raw` if explicit opt-in
    ↓
  Signals populated on the blackboard:
    auth.is_authenticated       bool
    auth.user_id_hash           string (always - pseudonymous)
    auth.user_id_raw            string (optional - opt-in)
    auth.user_id_source         "jwt.sub" | "cookie" | "header.X-User-Id" | …
    auth.login_attempt          bool   (path matches configured login endpoint)
    auth.login_success_hint     bool?  (inferred from response status/body)
    auth.username_attempted     string (for failed logins, if configured to parse the body)
    ↓
  Existing detectors read these signals (blackboard → broadcast)
  New detectors run with them (see §New detectors)
    ↓
  Policy resolution checks target_type="user", then target_type="endpoint", etc.
    (Existing IConfigOverrideStore already supports target_type="user" and "api_key")
    ↓
  Action policy applied, response shipped
    ↓
  [existing] Session vector + reputation update - now keyed on user_id_hash
    in addition to request signature when authenticated, enabling per-user
    behavioral baselines distinct from per-IP / per-signature.
```

Nothing in the fast path changes for unauthenticated requests. Identity extraction runs once at <100µs budget - JWT decode-without-verify is ~20µs, HMAC ~10µs, lookup is dictionary-hit.

## Configuration - how customers describe their auth

Most of the complexity is telling us where the identity *is*. This is tier-dependent:

### OSS (YAML + restart, or with hot-reload: YAML + save)

```yaml
# appsettings.json → BotDetection:AuthIdentity
BotDetection:
  AuthIdentity:
    Enabled: true
    # Ordered list of identity sources. First match wins.
    Sources:
      - Type: Jwt
        Location: Header            # or Cookie | QueryString
        Name: Authorization
        Prefix: "Bearer "           # stripped before decoding
        Claim: sub                  # which JWT claim is the user ID
      - Type: Cookie
        Name: ".AspNetCore.Cookies"
        # We can't decrypt DataProtection cookies without the key; instead
        # we HMAC the cookie value and use that as a stable pseudo-identity.
        Strategy: HashValue
      - Type: Header
        Name: X-User-Id
        Strategy: Plain              # take value as-is, then HMAC
    # Default: only the HMAC hash lands on the blackboard. Opt in to plaintext:
    RetainPlaintext: false
    # The HMAC key - defaults to a machine-derived value; override for fleet consistency.
    HashKey: "${STYLOBOT_USER_HASH_KEY}"

    # Login endpoints - enables failed-login detection (see §New detectors).
    LoginEndpoints:
      - Path: /api/auth/login
        Method: POST
        # How we decide success. Options:
        SuccessSignal:
          Type: StatusCode           # or BodyMatch | HeaderPresent
          Values: [200, 204]
        # Optional: if the POST body contains the attempted username, tell us
        # where so we can track per-username failure rates for password-spraying
        # detection. Ignored if RetainPlaintext is false AND HashAttemptedUsername
        # is false - in which case we hash the body value.
        AttemptedUsernameField: username
        HashAttemptedUsername: true
```

### Startup+ (Portal UI for the same, no restart)

Same shape, exposed as a form in the portal's config editor:
- Identity sources table (add / reorder / remove rows)
- Login endpoints list with success-signal picker
- "Test against live request" button that shows how identity extraction would parse a recent sampled request

Saved to Postgres → broadcast via Redis → gateways reload their `AuthIdentityExtractor` config in ~1s, no restart.

## New detectors and signals

### `AuthIdentityContributor` (Wave 0, priority 6 - runs right after TransportProtocol)

Doesn't produce a confidence delta on its own. Its job is to populate the blackboard with the `auth.*` signals above so downstream detectors can use them.

### `FailedLoginContributor` (Wave 1, priority 25)

Fires on requests matching a configured login endpoint where the success signal did *not* match.

Emits signals:
- `auth.failed_login` = true
- `auth.failed_login.burst_1m` = integer (per-user-id-hash count in last minute)
- `auth.failed_login.platform_burst_1m` = integer (platform-wide across all users)
- `auth.failed_login.spray_score` = float (0..1, high when many distinct user-id-hashes fail with low per-user velocity - password spraying signature)

Confidence contributions:
- Single failed login: neutral (humans typo passwords)
- 3+ failures / 1 min / same user: +0.3 bot (credential stuffing target)
- 10+ failures / 1 min / same user: +0.7 bot + fires `action.trigger_policy = throttle-stealth`
- Spray score > 0.7: **platform-level alert** via Redis channel `stylobot:alert:password-spray`; individual requests get +0.3 (still need to honor real users who are affected)

Uses the existing `ResponseCoordinator` fail2ban sliding window - new policy `login-spray-protection` pre-configured with appropriate thresholds.

### `UserBehaviorBaselineContributor` (Wave 1, priority 27)

For each authenticated user we've seen enough of (min 20 requests over 7 days), maintain a baseline session vector (existing 129-dim Markov+temporal+fingerprint). On each new authenticated request, compute cosine similarity against the user's baseline.

Emits:
- `auth.baseline_similarity` = float (0..1; 1 = matches baseline perfectly)
- `auth.baseline_deviation` = float (Euclidean distance)
- `auth.baseline_source` = "personal" | "peer-group-fallback" | "none"

Contributions:
- Similarity > 0.85: −0.15 bot (this really looks like the normal user)
- Similarity < 0.4 AND deviation high: +0.5 bot ("this authenticated user is behaving completely unlike themselves - ATO candidate")
- Impossible travel (country changes mid-session with < 1h gap): +0.8 bot + fires `action.trigger_policy = challenge`

### `ImpossibleTravelContributor` (Wave 2, priority 28)

Subset of UserBehaviorBaseline but fast-path. Reads `auth.user_id_hash` + `geo.country_code` + per-user "last seen location + time" from the cluster reputation cache. Compute travel speed between last-seen and now. > 800 km/h physically impossible.

### `AuthenticatedRateLimitContributor` (Wave 1, priority 24)

Per-user rate limiter separate from the per-IP / per-signature one. Authenticated users often legitimately burst on login (app loads many endpoints in parallel), so IP-level thresholds fire falsely. A per-user limiter calibrated to their personal baseline fixes this.

## Per-user policy UX (tier-dependent - capability gates, never caps)

**Tier philosophy:** tiers unlock *what you can configure* and *what reports you can see*. Not *how many users you're allowed to have*. Size is a separate pricing dimension (throughput-metered); the tier dictates capability on top of whatever scale you're running at.

### OSS

OSS ships the **Users tab** in the dashboard (aggregate view - recently active authenticated users, grouped by user-id-hash, with counts and top endpoints). Per-user policies are **configured via the YAML / JSON editor** (Monaco, in-dashboard - same editor that handles the rest of OSS configuration). There's no form UI for user policies in OSS, but the capability is there - an OSS user can write:

```yaml
# In overrides.yaml
userOverrides:
  - userIdHash: "a3f2b1c4d5e6"
    scope: "/api/**"
    policy: permissive-api
    notes: "trusted partner, 10× default rate"
```

Save → file watcher → hot reload → applied. The Users tab highlights configured overrides with a link to jump to them in the editor. No gating on how many, no limits.

**Upsell rail** shown alongside the editor: "In paid tiers, this is a form - pick a user from the recent list, apply a policy template, see shadow-mode preview before you save. [Start 30-day trial]"

### Startup

Adds the **form-based per-user policy editor** in the portal dashboard:

```
Add override
  Target: ◉ User  ○ API Key  ○ Endpoint  ○ Global  ○ Geo  ○ UA Family
  User ID (hashed): [choose from list of recently active users] ▼
         (or paste a plaintext user ID - we'll hash it for you)
  Scope: ◉ All endpoints  ○ Specific endpoint: [ pattern ]
  Policy: ◉ From template  ○ Custom
    Template: [strict-login ▼]
  Notes: "VIP enterprise tier, allow 10× default rate"
  [Cancel]  [Save + broadcast]
```

Same underlying override storage; it's the *authoring experience* that's the upgrade - discoverability of recently active users, one-click template application, clearer policy diff views. Saves land in Postgres and broadcast via Redis to all gateways within ~1s.

### SME

Adds:
- **Group overrides** - tag users into groups (`enterprise-accounts`, `flagged-suspicious`, `trusted-partners`) and target the group with a single policy.
- **Conditional rules** - "if user's baseline similarity < 0.5 AND endpoint matches /admin/* then challenge." Visual rule-builder, not YAML.
- **Shadow-mode / dry-run** on any override before save - runs the new policy against the last N recent detections for that user, shows the diff.
- **Per-user timeline view** - click a user in the Users tab, see their full history, behavioral radar chart, which policies fired, last-seen locations map.

### Enterprise

Adds:
- **SSO group sync** - user groups auto-populated from OIDC/SAML claim (e.g., `role:admin` from Azure AD → "admins" group in StyloBot). Group membership flows from the IdP, not our own user list.
- **SCIM provisioning hooks** - when a user is de-provisioned at the IdP, their overrides auto-revoke (or reassign to owner-of-record per customer policy).
- **Staged rollout** for overrides - apply a new policy to one gateway group first, auto-promote on success metrics, auto-revert on failure.
- **Compliance export** - every per-user rule change lands in the signed SOC 2 compliance ZIP with operator identity, IP, timestamp, before/after values.

## Privacy model

Default: **hash everything**.

1. Identity extractor HMACs the user ID in-process before the blackboard write. The raw value exists only in the extracted-and-hashed span of stack. Persisted tables store hashes.
2. `RetainPlaintext: true` is an explicit opt-in that logs a warning on every startup and adds an audit-log entry every 24h noting plaintext retention is active. Enables the plaintext `auth.user_id_raw` signal which flows to the dashboard for "Alice hit /admin/users" style displays rather than "user_a3f2…".
3. Failed-login body parsing: by default, attempted usernames are HMAC-hashed using the same key. Password-spraying detection works on hashes. `HashAttemptedUsername: false` is a second opt-in explicitly for the "we need to see which accounts are being targeted" case.
4. GDPR right-to-erasure: new endpoint `DELETE /api/users/{user_id_hash}/data` purges per-user state (baseline vector, reputation, overrides, audit referring to them). Signed-audit preserves the deletion itself.
5. DSAR (data subject access request): `GET /api/users/{user_id_hash}/export` returns everything StyloBot knows about that user, as JSONL.

## User stories

### US-1: Credit union blocks credential stuffing against one VIP account

**Persona:** Maya, security analyst at a small credit union on SME tier.

**Given** the attacker has obtained a list of leaked `email:password` pairs and is attempting login on the customer portal at ~20 attempts/second,

**When** 5 failed logins occur against the same username hash within 1 minute,

**Then** StyloBot's `FailedLoginContributor` fires `throttle-stealth` for subsequent attempts against that user (silent 300ms delay + high jitter - the attacker's script times out before completing the run),

**And** Maya sees a real-time alert "credential stuffing against user `a3f2b1c…` - 43 attempts throttled" on the Dashboard Users tab,

**And** the legitimate owner of that account, should they try to log in from their normal device, bypasses the throttle because their per-user behavioral baseline matches (cookie fingerprint, country, timing).

---

### US-2: SaaS detects phishing kit takeover via behavioral drift

**Persona:** Dan, ops engineer at a B2B SaaS on Startup tier.

**Given** a user, `alice@acme.com`, normally browses the dashboard at 3-5 req/min from London, Chrome 131, on a MacBook TLS fingerprint,

**When** her session cookie is replayed from a VPS in Lithuania with a curl-flavored TLS fingerprint within 15 minutes of her last legit request,

**Then** `ImpossibleTravelContributor` fires (+0.8 bot, travel speed physically impossible) AND `UserBehaviorBaselineContributor` fires (similarity 0.12, deviation high),

**And** the default policy triggers `challenge` on the replayed session,

**And** Dan sees an "ATO alert" badge on the user's timeline in the dashboard, with a "revoke session" button that calls the SaaS's configured webhook (`POST https://acme.com/admin/revoke-session` with the user hash and session ID).

---

### US-3: Small team gives a trusted partner 10× the default rate limit

**Persona:** Jordan, platform team lead on SME tier.

**Given** partner `bigintegrator-prod@acme.com` hits the API hard every night for batch sync, triggering `behavioral` detector's rate-limit confidence on every request,

**When** Jordan adds a per-user override in the portal config editor: target `user:<hash-of-bigintegrator-prod>`, scope `/api/v1/**`, policy "permissive-api",

**Then** for that user's requests, the rate-limit thresholds from `permissive-api` supersede the defaults (10× headroom), behavioral detector still fires but the action policy is `logonly` rather than `throttle-stealth`,

**And** Jordan can see in the dashboard that the override is being applied: "5,431 requests governed by 'bigintegrator-prod' override in the last hour."

---

### US-4: Operator discovers platform-wide password spraying and responds

**Persona:** Priya, SRE at a mid-market e-commerce site on SME tier.

**Given** an attacker is running password spraying (1-2 attempts each across ~8,000 different accounts over 30 minutes),

**When** `FailedLoginContributor`'s `spray_score` crosses 0.7 (many distinct user hashes, low per-user velocity, correlated fingerprints),

**Then** StyloBot publishes `stylobot:alert:password-spray` on the cluster backplane and the dashboard surfaces a red banner: "Platform-wide password spraying detected - 8,247 distinct accounts targeted in 30 min",

**And** Priya clicks "Enable platform-wide MFA challenge" in the banner which pushes a temporary override to the login endpoint policy: `require-second-factor-on-login`,

**And** StyloBot issues an audit entry tagged `policy.emergency-override` that logs the one-click change for SOC 2 evidence.

---

### US-5: OSS user wants to see authenticated users in their dashboard

**Persona:** Alex, solo operator of a self-hosted tool on OSS.

**Given** Alex has configured `BotDetection:AuthIdentity.Sources` to extract the `sub` claim from the JWT in the `Authorization` header,

**When** Alex opens `/_stylobot` and clicks the Users tab,

**Then** they see a list of recently active authenticated users grouped by user-id-hash, with request counts, time ranges, top endpoints - aggregate data only, no per-user actions,

**And** clicking a user drills into the standard request list filtered to that user,

**And** disabled controls with tooltip "Per-user policies require Startup+" signal what's possible on paid tiers.

---

### US-6: Enterprise customer on-boards with SAML SSO + pre-existing user groups

**Persona:** Sam, director of IT at a regulated bank on Enterprise tier.

**Given** the bank's Azure AD has groups `stylobot-admins`, `stylobot-operators`, `stylobot-viewers`,

**When** Sam configures StyloBot's dashboard OIDC RP to consume the `groups` claim from Azure AD's SAML assertion,

**Then** when a bank employee signs into the StyloBot dashboard, their Azure AD groups are mapped to StyloBot roles automatically - no per-user invite needed,

**And** per-user policies can target Azure AD group claims ("for users in group `private-banking`, apply policy `ultra-strict-auth`"),

**And** when Sam's team de-provisions an employee in Azure AD, the SCIM integration immediately revokes the employee's StyloBot session and any per-user overrides they authored are reassigned to the owner-of-record.

---

### US-7: DSAR - user requests "what do you know about me?"

**Persona:** GDPR compliance officer at a European B2C SaaS.

**Given** user `bob@example.com` files a GDPR data subject access request,

**When** the officer computes `user_id_hash` for `bob@example.com` and calls `GET /api/users/{user_id_hash}/export`,

**Then** StyloBot returns a JSONL stream: the user's baseline behavioral vector, last-seen locations, every per-user policy applied to them, every detection that referenced them (timestamps, risk bands, actions), the plaintext user ID if plaintext retention was ever enabled,

**And** the audit log records the DSAR export: who requested it, when, which user, for SOC 2 + GDPR Article 15 evidence.

---

### US-8: Right to erasure

**Same persona.**

**Given** user `bob@example.com` requests deletion,

**When** the officer calls `DELETE /api/users/{user_id_hash}/data`,

**Then** StyloBot purges: baseline vector, reputation state, recent sessions, any plaintext-retained ID,

**And** an anonymized counter remains in aggregate counters (to preserve platform-level statistics without identity),

**And** the deletion itself is audit-logged with reason="GDPR erasure" for Article 17 evidence.

---

### US-9: Detect phishing pivot - user redirected off-site, comes back compromised

**Persona:** Maya (credit union again).

**Given** a phishing kit hosts a lookalike login page at `creditunlon.com/login` (note the typo) and tricks users into submitting credentials there; the kit then logs in legitimately and uses the resulting session,

**When** a user's session cookie (tracked by our HMAC-of-cookie-value strategy) is seen originating from a new network + new TLS fingerprint within 2 hours of a Referer chain showing the user came from `creditunlon.com`,

**Then** StyloBot emits `auth.phishing_pivot_suspected = true` and elevates the session to `challenge` policy,

**And** the dashboard alerts: "Possible phishing pivot: user `a3f2b1c…` last referred from suspicious domain creditunlon.com; session now behaving anomalously",

**And** Maya's team can one-click add `creditunlon.com` to a denied-referer list that applies to all sessions platform-wide.

---

### US-10: Audit - "show me every decision that referenced my deleted user"

**Persona:** GDPR officer post-erasure.

**Given** bob@example.com's data was deleted yesterday,

**When** the officer runs the compliance export for the pre-deletion window,

**Then** StyloBot's signed export includes every audit reference to `user_id_hash=<bob's hash>` - detection decisions, policy applications, overrides authored by or targeting the user - with timestamps and operator identities,

**And** the export is Ed25519-signed by the control plane so auditors can verify it hasn't been tampered with in transit,

**And** the export filename itself contains the date range and a hash of the contents for chain-of-custody evidence.

---

### US-11: Internal support team investigates "account feels weird" complaint

**Persona:** Dana, customer support at the SaaS.

**Given** a customer emails support: "my account has been weird today, logging me out at random,"

**When** Dana opens the Dashboard Users tab, filters to that customer's user-id-hash, and drills into their last 24h timeline,

**Then** Dana sees: normal morning activity from London, sudden burst of activity from Moscow at 14:23, 3 failed logins at 14:24, StyloBot fired `challenge` at 14:25, user subsequently logged in normally from London at 15:10,

**And** Dana can confidently respond "your account was targeted by a credential stuffing attempt at 14:23 UTC; our detection blocked the attacker. Your session was interrupted as a safety measure. We recommend enabling MFA."

## Build sequence (priority order)

### Phase A - Identity extraction foundation
- `AuthIdentityExtractor` middleware with the 4 source types (JWT, Cookie, Header, QueryString)
- Configuration binding for `BotDetection:AuthIdentity:Sources`
- Signal emission (`auth.user_id_hash`, etc.)
- `AuthIdentityContributor` wiring to the blackboard

**Ships OSS + Commercial in one commit.** No UI yet, all configured via YAML / appsettings.

### Phase B - Failed login detection
- `FailedLoginContributor` YAML manifest + C# + `login-spray-protection` action policy
- Per-user-hash fail2ban counter
- `AuthenticatedRateLimitContributor` for burst-on-login cases

### Phase C - Per-user policy editor UI (Startup+)
- Portal config editor gains `Target: User` option with hash lookup from recently-active list
- Capacity check against `MaxPerUserOverrides`
- Broadcast via existing Redis pub/sub

### Phase D - User behavioral baselines
- `UserBehaviorBaselineContributor` - uses existing session vector infrastructure but keyed on user hash
- `ImpossibleTravelContributor` - fast-path geo+user-hash check
- Dashboard Users tab with per-user timeline + behavioral radar

### Phase E - Phishing + ATO detection
- Referer-chain tracking for off-site round-trips
- `PhishingPivotContributor` combining Referer history + session vector drift
- Dashboard alerting UI for platform-wide patterns

### Phase F - Privacy & compliance
- `GET /api/users/{hash}/export` (DSAR)
- `DELETE /api/users/{hash}/data` (erasure)
- Plaintext opt-in guard-rails + audit
- Signed compliance export including per-user records

### Phase G - Enterprise group sync
- OIDC group claim mapping → StyloBot role
- SCIM de-provisioning hooks
- Azure AD / Okta / Google Workspace configuration presets

## Open design questions

1. **Cookie HMAC vs cookie content parsing.** If we HMAC the raw cookie value, we get a stable pseudo-identity per cookie - but if the app rotates session cookies frequently, we lose continuity. If we parse the DataProtection cookie, we need the app's keys. Most deployments can't share those. Proposal: HMAC by default; optional app-provides-a-webhook-that-returns-user-id-for-a-cookie for deployments that care.

2. **What about OAuth access tokens vs ID tokens?** ID tokens carry `sub`. Access tokens vary - may be opaque, may be a JWT. Proposal: configurable claim path with a fallback to HashValue.

3. **Username in the body on failed logins - how do we read it without over-reaching?** If the customer says "POST body is JSON, username field is `email`," we parse only that field and throw away the rest. Don't log the body. Don't retain it. Some apps send credentials in form-urlencoded which is trivial. GraphQL is harder (would need to parse the operation). Proposal: support JSON and form; GraphQL users declare a specific operation name and field path.

4. **Cross-user correlation for spray detection.** Do we need to *see* the attempted usernames (even hashed) to detect spraying, or can we infer from "lots of different failed-login requests with same TLS fingerprint / IP / narrow time window"? Both work; the second requires no body parsing and is privacy-cheaper. Proposal: lead with fingerprint-based spray detection; body-username hash is an opt-in enhancement for higher precision.

5. **Does a per-user baseline handle multi-device users well?** Alice uses her phone (iOS Safari, cellular) and her laptop (macOS Chrome, home WiFi). Her baseline will be bimodal. Proposal: maintain top-K baselines per user (default K=3); similarity checks against nearest baseline, not the centroid. Falls back to peer-group baseline for new users.
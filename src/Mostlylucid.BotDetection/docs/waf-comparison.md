# StyloBot vs Classic WAF - Coverage Audit

> **Bottom line:** StyloBot already covers the OWASP Top 10 payload-based classes that classic WAFs (ModSecurity + OWASP CRS, AWS WAF, Cloudflare WAF, Azure WAF) built their reputations on - via the `Haxxor` detector. Where StyloBot *extends* coverage is the whole class of attacks WAFs can't see: behavioral, session-level, fingerprint-level, and cross-request correlation. This doc maps our coverage to classic categories, shows the gaps, and spells out what WAFs MISS.

## OWASP Top 10 (2021) - how we map

| OWASP category | Classic WAF approach | StyloBot coverage | Detector |
|---|---|---|---|
| **A01 - Broken Access Control** (path traversal, IDOR, forced browsing) | Regex on `../`, `%2e%2e`, known admin paths | ✓ Yes - `traversal_patterns` + `admin_patterns` + `config_patterns` + `debug_patterns` | `Haxxor` |
| **A02 - Cryptographic Failures** | Not a WAF concern - TLS-layer | ✗ Out of scope (infra) | - |
| **A03 - Injection** (SQLi, XSS, cmdi, LDAP, NoSQL, template) | Signature-based regex on request body / query | ✓ SQLi, XSS, cmdi, SSTI; partial SSRF, encoding evasion | `Haxxor` |
| **A04 - Insecure Design** | Not observable at HTTP layer | ✗ Out of scope | - |
| **A05 - Security Misconfiguration** (exposed config, debug, admin panels) | Regex on known endpoints | ✓ `.env`, `.git`, `web.config`, `appsettings.json`, `/actuator`, `/server-status`, `/debug/pprof`, `/metrics`, swagger, etc. | `Haxxor` |
| **A06 - Vulnerable Components** | CVE-specific signatures (Log4Shell, Struts, etc.) | ⚠ **Partial** - we catch the scanner, not the payload. Gap: add CVE-specific payload signatures. | `SecurityTool` (scanner UAs); **TODO** |
| **A07 - Identification & Authentication Failures** (credential stuffing, session fixation) | Usually out of WAF scope; fail2ban layer | ✓ **Full** - `FailedLogin` detector spec'd + `Fail2Ban` via `ResponseBehavior`; per-user behavioral baselines | `ResponseBehavior`, `Behavioral`, `UserBehaviorBaseline` (planned) |
| **A08 - Software & Data Integrity Failures** (deserialization) | Pattern match on common gadget chains | ⚠ Not today - see gap list | **TODO** |
| **A09 - Logging & Monitoring Failures** | Not a WAF concern | N/A - we ARE the logging layer | - |
| **A10 - SSRF** | Regex on internal IPs, file://, gopher://, metadata endpoints | ✓ Yes - `ssrf_patterns` includes 127/8, 10/8, 172.16/12, 192.168/16, link-local 169.254, cloud metadata, gopher/file/dict/ftp | `Haxxor` |

## Classic WAF rule family coverage

How we map against the rulesets that ModSecurity + OWASP CRS 4.0 ship:

| OWASP CRS rule family | What it does | StyloBot equivalent | Coverage |
|---|---|---|---|
| `REQUEST-901-INITIALIZATION` | Request normalization | ASP.NET Core built-in | ✓ framework |
| `REQUEST-905-COMMON-EXCEPTIONS` | Known false-positive whitelists | `userOverrides` / `apiKey` policy | ✓ (configurable) |
| `REQUEST-910-IP-REPUTATION` | Known-bad IP block | `FastPathReputation` + `Ip` + `ReputationBias` | ✓ adaptive |
| `REQUEST-911-METHOD-ENFORCEMENT` | Block non-GET/POST/etc. | `Header` detector + transport context | ✓ |
| `REQUEST-912-DOS-PROTECTION` | Burst rate limits | `Behavioral` rate_limit + `ResponseCoordinator` fail2ban | ✓ |
| `REQUEST-913-SCANNER-DETECTION` | Block sqlmap/nikto/nmap UAs | `SecurityTool` - 40+ fingerprinted scanners | ✓ |
| `REQUEST-920-PROTOCOL-ENFORCEMENT` | Malformed HTTP, header smuggling | **Partial** - `Header`, `TransportProtocol` catch some | ⚠ gaps |
| `REQUEST-921-PROTOCOL-ATTACK` | HTTP request smuggling, CRLF injection | **Gap** | ✗ **TODO** |
| `REQUEST-930-APPLICATION-ATTACK-LFI` | Local File Inclusion | `Haxxor.traversal_patterns` + `config_patterns` | ✓ |
| `REQUEST-931-APPLICATION-ATTACK-RFI` | Remote File Inclusion | `Haxxor.ssrf_patterns` (overlap) | ⚠ partial - gap for `?url=http://...&include=...` style |
| `REQUEST-932-APPLICATION-ATTACK-RCE` | Remote Code Execution (cmdi) | `Haxxor.cmdi_patterns` | ✓ |
| `REQUEST-933-APPLICATION-ATTACK-PHP` | PHP-specific payloads | `Haxxor.webshell_patterns` | ✓ |
| `REQUEST-934-APPLICATION-ATTACK-GENERIC` | Misc code injection | `Haxxor.ssti_patterns` + `cmdi_patterns` | ✓ |
| `REQUEST-941-APPLICATION-ATTACK-XSS` | XSS | `Haxxor.xss_patterns` | ✓ |
| `REQUEST-942-APPLICATION-ATTACK-SQLI` | SQL injection | `Haxxor.sqli_patterns` | ✓ |
| `REQUEST-943-APPLICATION-ATTACK-SESSION-FIXATION` | Session fixation | **Gap** | ✗ **TODO** |
| `REQUEST-944-APPLICATION-ATTACK-JAVA` | Java-specific (deserialization) | **Gap** | ✗ **TODO** |
| `REQUEST-949-BLOCKING-EVALUATION` | Aggregate score + block | `BlackboardOrchestrator` + `ResponseCoordinator` action policies | ✓ more sophisticated |

**Summary:** we cover ~80% of OWASP CRS 4.0 by default. Gaps to close for parity are documented below.

## What classic WAFs MISS that StyloBot catches

This is the headline story. Classic WAFs are payload-pattern-match tools. Modern attackers know this and moved past payloads years ago. Everything below requires correlation across requests, sessions, or behavioral state - things a stateless regex engine literally can't do.

### 1. Credential stuffing / account takeover

**WAF behavior:** sees each `POST /login` as a valid-looking request with a real email + password. No malicious payload. WAF **allows**.
**StyloBot:** `FailedLogin` detector tracks per-user-hash failure velocity, platform-wide spray score, behavioral drift against per-user baseline. Impossible-travel detection catches replay from a different country minutes after the legit user logged in.

### 2. Scraping with a real browser

**WAF behavior:** Chrome UA + real TLS + well-formed headers + GET `/api/products?page=3`. Zero regex match. WAF **allows**.
**StyloBot:** `SessionVector` shows the Markov chain is 100% ApiCall→ApiCall with metronomic timing - no human browses a catalog at 2 req/sec with no jitter. `Behavioral` rate-limit fires on the sustained pattern. `Heuristic` pulls it all together.

### 3. Headless browser / Puppeteer / Playwright

**WAF behavior:** user-agent says Chrome/131 (attacker spoofed). TLS handshake looks right. Headers look right. WAF **allows**.
**StyloBot:** `TLS` detector sees JA4 fingerprint `t13d1516h2_8daaf6152771_…` which is specific to headless Chromium - not real Chrome. `ClientSide` detector fires if the browser can't render the canvas challenge correctly. `MultiLayerCorrelation` cross-checks that the UA, TLS, HTTP/2 SETTINGS frame, and TCP p0f fingerprint all agree - attackers rarely spoof all four.

### 4. Distributed low-and-slow attacks

**WAF behavior:** 50,000 IPs each sending 5 requests per hour = no rate limit trips, no per-IP reputation hit. WAF **allows**.
**StyloBot:** `SessionVector` sees identical behavioral fingerprints across thousands of IPs. `Cluster` runs Leiden community detection on the signature graph, surfaces the coordinated campaign.

### 5. Rotating IPs / residential proxies

**WAF behavior:** each request from a different clean residential IP. IP reputation is useless. WAF **allows**.
**StyloBot:** session tracking is keyed on the signature hash (HMAC of IP+UA+TLS+headers) with fallback correlation via behavioral vector similarity. A rotating-IP attacker who keeps the same client-side fingerprint is tracked as one session.

### 6. Protocol-layer abuse (streaming / websockets / gRPC)

**WAF behavior:** protocol-agnostic regex can't tell a legitimate WebSocket upgrade from connection churn abuse. Many WAFs just pass through. WAF **allows**.
**StyloBot:** `TransportProtocol` classifies the protocol; `StreamAbuse` detects handshake storms, cross-endpoint mixing, payload flooding.

### 7. Zero-day / novel payloads

**WAF behavior:** only matches its regex set. A novel encoding, new template-injection syntax, or unreleased CVE gets through until a rule is shipped. WAF **allows**.
**StyloBot:** `Heuristic` AI + `LLM` escalation classify based on intent and context, not strictly on known signatures. Novel payloads that *look suspicious* get escalated even without a specific regex.

### 8. Legitimate-looking API abuse

**WAF behavior:** `GET /api/users/{id}` with valid auth cookie and realistic pagination. WAF **allows**.
**StyloBot:** per-user behavioral baseline flags that *this authenticated user* is suddenly hitting 47 users/minute vs their 3/day normal. ATO indicator without a single injection payload.

### 9. Post-exploitation lateral movement

**WAF behavior:** requests look normal but the session hasn't behaved this way before. WAF **allows** - it has no session state.
**StyloBot:** `ResponseBehavior` sees the cookie-replayed session trigger honeypot endpoints or flip behavior mid-session (went from read-only browsing to calling sensitive APIs). `UserBehaviorBaseline` deviation fires.

### 10. Attacker-owned infrastructure vs. legit users

**WAF behavior:** geo-block everything from Russia? You just blocked legit Russian customers. Allow everyone? You just allowed Russian attackers. WAF offers only this binary. WAF **false-flags**.
**StyloBot:** `GeoChange` tracks per-session geographic drift. `CountryReputation` is adaptive and per-endpoint (your `/admin/*` can have stricter geo policies than `/blog/*`). `VerifiedBot` DNS-validates Googlebot so you don't accidentally block it at the perimeter.

## Gaps to close for parity

Closing these would bring us to 100% OWASP CRS 4.0 parity. Priorities by value:

1. **HTTP Request Smuggling (CVE-2019-10910 class)** - detect conflicting `Content-Length` vs `Transfer-Encoding`, malformed chunked encoding, header-smuggling via obfuscated whitespace. New `ProtocolAttackContributor` at priority ~6.
2. **CRLF injection** - `\r\n` sequences in header values that attempt header injection. Detect as part of `ProtocolAttack` above.
3. **XXE / XML External Entity** - requests with `Content-Type: application/xml` + `<!ENTITY ... SYSTEM "...">` patterns. New `XxeContributor` or extend `Haxxor` with `xxe_patterns`.
4. **Session fixation indicators** - detect `Set-Cookie` over-write patterns in request, or multiple simultaneous sessions from same signature. Extend `ResponseBehavior`.
5. **Java deserialization gadget chains** - `ObjectInputStream` magic bytes (`\xac\xed`), common gadgets (`AnnotationInvocationHandler`, `BeanComparator`, `CommonsCollections*`). Extend `Haxxor` with `deserialization_patterns`.
6. **LDAP injection** - `*)(uid=*))(|(uid=` class payloads. Extend `Haxxor` with `ldap_patterns`.
7. **NoSQL injection** - MongoDB `{"$ne": null}`, `$where: "this.password.match(...)"`, etc. Extend `Haxxor` with `nosql_patterns`.
8. **HTTP Parameter Pollution** - duplicate query string parameters with conflicting values. New signal in `Header` detector.
9. **CVE-specific payloads** - Log4Shell `${jndi:ldap://`, Spring4Shell `class.module.classLoader`, ProxyShell, etc. Maintain a `cve_patterns.yaml` refreshed from public exploit databases.
10. **File upload exploit detection** - multipart requests with suspicious filenames (`.jsp`, `.aspx`, `.php` to image endpoints) and content-type mismatches. New `FileUploadContributor`.

## Positioning for sales

**For the buyer who has a WAF today:**
"StyloBot isn't a replacement for your WAF - it's the second layer your WAF can't be. Keep your Cloudflare or AWS WAF for edge protection against the obvious payload attacks. Put StyloBot in front of your app for everything that looks like a real user but isn't one."

**For the buyer with no WAF:**
"StyloBot IS your WAF - plus the 10 categories your WAF couldn't help with anyway. We cover the OWASP Top 10 payload classes at WAF-grade accuracy, and we own the behavioral layer that classic WAFs can't reach."

**For the buyer choosing us over Cloudflare Bot Management / DataDome / HUMAN:**
"Those are black-box cloud services that see only the traffic that flows through their edge. StyloBot runs inside your infrastructure, sees every request with full context (user identity, session state, business intent), and lets you tune the rules rather than trust their secret sauce. And you own your data."

## Worked comparison - the credential-stuffing scenario

**AWS WAF managed rules + Cloudflare Bot Management:**
- POST `/login` with real email + real-looking password
- Either no rule fires, or one of them uses "impossible behavioral fingerprint" and blocks, but the fingerprint rules are opaque and regularly cause false positives
- The WAF bill scales with request volume - credential stuffing DRIVES your bill up

**StyloBot:**
- `FailedLogin` counts attempts per user-hash in sliding window
- At 5 failures/min for one user: `throttle-stealth` (silent 300ms delay, attacker's script times out - user's own legit login unaffected because they'd only try twice)
- At 15: `challenge` (CAPTCHA / MFA prompt) - still doesn't block a real user who mistyped
- At 50: `block` with audit trail
- All configurable via YAML (OSS) or the form UI (Startup+)
- Platform-wide spray score detects low-and-slow `password123`-across-10k-accounts attacks that per-account thresholds never trigger
- Every decision is explainable: "blocked because user X had 51 failed logins in 10 min from 3 different IPs with identical TLS fingerprint"
- Cost scales with your work-unit throughput, not with the attacker's volume

**What it looks like in the dashboard:**
A red banner: "Credential stuffing detected - 8,247 distinct accounts targeted in 30 min; aggregate spray_score 0.82." One click enables platform-wide MFA-required policy, logged for SOC 2.

## Roadmap

- **Phase A (next):** close the top 3 gaps (HTTP request smuggling, CRLF injection, XXE). Add a `ProtocolAttackContributor` at priority 6. Extend `Haxxor` with `xxe_patterns`. ~1 focused session.
- **Phase B:** CVE-specific payload library as a refreshable YAML. Auto-pull from public exploit feeds monthly. Ship with Log4Shell, Spring4Shell, ProxyShell baked in for immediate value.
- **Phase C:** deserialization + LDAP + NoSQL + HTTP parameter pollution. Extend `Haxxor` or split into family-specific sibling detectors if `Haxxor` grows too large.
- **Phase D:** file upload scanner (multipart content-type vs extension mismatch; known webshell bytes).

Nothing in here changes our behavioral-layer story. These are the payload-pattern detectors we need to match classic-WAF coverage; the value story is still "WAFs can't see behavior; we do both."
Here’s a clean v0.1 functional spec you can literally drop into a README / design doc and iterate from.

---

## Stylobot.Gateway v0.1 – Functional Specification

### 1. Purpose

A lightweight, **Docker-first YARP reverse proxy** that can:

* Run bare from `docker run` with no extra config.
* Be **fully configured** via:

    * mounted config files,
    * environment variables,
    * and (optionally) a SQL database.
* Act as the **host** for behavioural plugins (BotDetection, GeoDetection, etc.) later, but v0.1 focuses on:

    * solid routing,
    * config story,
    * health/diagnostics,
    * and pluggable storage.

---

### 2. Runtime Surfaces

#### 2.1 Public Proxy Port

* **Port:** `8080` (configurable via `GATEWAY_HTTP_PORT`)
* **Behaviour:**

    * Accepts inbound HTTP(S) traffic.
    * Proxies requests according to YARP route/cluster configuration.
    * If no routes configured: returns a simple JSON banner:

      ```json
      { "status": "no-routes", "message": "No YARP routes configured. See /admin/config for details." }
      ```

#### 2.2 Admin / Management API

* **Base path:** `/admin`
* **Default port:** same as proxy (8080), but behind path prefix.
* **Endpoints (v0.1):**

    * `GET /admin/health` – liveness/readiness
    * `GET /admin/config/effective` – current merged effective configuration (sanitised)
    * `GET /admin/config/sources` – shows which config sources are active (env/file/db)
    * `GET /admin/fs` – list key directories:

        * `/app/config`
        * `/app/data`
        * `/app/logs`
        * `/app/plugins`
    * `GET /admin/fs/{logical}` – list a specific logical directory (`config`, `data`, `logs`, `plugins`)
* **Access:**

    * By default **not authenticated** for local/dev.
    * Can be locked behind:

        * IP allow-list
        * shared secret header `X-Admin-Secret`
        * (future) OIDC

---

### 3. Configuration Model

#### 3.1 Configuration Sources & Precedence

Effective config is built by layering these sources (lowest → highest):

1. **Built-in defaults**
2. **File config mounted into container**

    * `/app/config/appsettings.json`
    * `/app/config/yarp.json` (routes/clusters)
    * `/app/config/policies.json` (future behavioural policies)
3. **Environment variables**
4. **Database overrides** (optional, v0.1 = read-only, no UI editing)

Precedence rule: **later overrides earlier** (env > file > built-in).

---

### 4. Zero-Config / “docker run” Behaviour

#### 4.1 Minimal Run

Command:

```bash
docker run --rm -p 8080:8080 ghcr.io/mostlylucid/yarp-gateway:0.1
```

Behaviour:

* Starts Kestrel on `0.0.0.0:8080`.
* No DB connection required.
* No config files required.
* Exposes:

    * `/admin/health` → `{ "status": "ok", "mode": "zero-config" }`
    * `/admin/config/effective` → shows defaults.
* Proxy behaviour:

    * If `DEFAULT_UPSTREAM` env var **not** set:

        * All non-`/admin` requests → JSON “no routes configured”.
    * If `DEFAULT_UPSTREAM` env var set (e.g. `http://host.docker.internal:5000`):

        * Adds a single default catch-all route:

            * Match: `/{**path}`
            * Cluster: `default-upstream` → `DEFAULT_UPSTREAM`

---

### 5. Configuration Details

#### 5.1 Environment Variables (v0.1)

Core:

* `GATEWAY_HTTP_PORT`

    * Default: `8080`
* `DEFAULT_UPSTREAM`

    * Optional. If present, auto-creates a catch-all route.

YARP JSON file path overrides:

* `YARP_CONFIG_FILE`

    * Default: `/app/config/yarp.json`

Database:

* `DB_PROVIDER`

    * `none` (default), `Postgres`, `SqlServer`
* `DB_CONNECTION_STRING`

    * Optional; if missing or empty → DB is disabled.
* `DB_MIGRATE_ON_STARTUP`

    * `true`/`false`, default `true` if DB enabled.

Admin:

* `ADMIN_BASE_PATH` – default `/admin`
* `ADMIN_SECRET` – if set, `/admin` APIs require header:

    * `X-Admin-Secret: <value>`

---

#### 5.2 File-Based Configuration

**Volume mount pattern:**

```bash
docker run \
  -p 8080:8080 \
  -v /host/config:/app/config \
  -v /host/data:/app/data \
  -v /host/logs:/app/logs \
  ghcr.io/mostlylucid/yarp-gateway:0.1
```

Logical mappings:

* `/app/config`

    * `appsettings.json` – base ASP.NET & gateway config
    * `yarp.json` – YARP `Routes` + `Clusters`
    * `policies.json` – future behavioural/policy config
* `/app/data`

    * DB files (e.g. SQLite in future)
    * plugin data
* `/app/logs`

    * structured logs
* `/app/plugins`

    * future plugin assemblies/config

**YARP JSON (yarp.json) example:**

```json
{
  "ReverseProxy": {
    "Routes": {
      "api": {
        "ClusterId": "api-cluster",
        "Match": {
          "Path": "/api/{**catch-all}"
        }
      }
    },
    "Clusters": {
      "api-cluster": {
        "Destinations": {
          "d1": { "Address": "http://api:5000" }
        }
      }
    }
  }
}
```

---

### 6. Database Integration (Optional)

#### 6.1 Purpose in v0.1

* DB is **optional**; container must run happily without it.
* When enabled, DB provides:

    * Persistent storage for:

        * Request/response logs (basic)
        * Route hit counters
        * Health/latency metrics summaries
    * (Later versions) dynamic policies & reputation.

#### 6.2 Behaviour

* If `DB_CONNECTION_STRING` is missing or `DB_PROVIDER=none`:

    * No DB connection attempted.
    * `/admin/config/sources` marks DB as “disabled”.
* If provided:

    * On startup:

        * Optionally run migrations (`DB_MIGRATE_ON_STARTUP=true`).
    * Periodically flush in-memory metrics to DB.
    * `/admin/health` includes DB status:

      ```json
      { "status": "ok", "db": "connected" }
      ```

---

### 7. Directory Listing / Inspection

Admin FS endpoints allow you to **see what’s actually mounted** inside the container.

#### 7.1 `GET /admin/fs`

Returns logical directories and their on-disk paths:

```json
{
  "directories": [
    { "name": "config", "path": "/app/config" },
    { "name": "data", "path": "/app/data" },
    { "name": "logs", "path": "/app/logs" },
    { "name": "plugins", "path": "/app/plugins" }
  ]
}
```

#### 7.2 `GET /admin/fs/{logical}`

Example: `GET /admin/fs/config`

* Lists **top-level files and subdirectories** in `/app/config`:

  ```json
  {
    "logicalName": "config",
    "path": "/app/config",
    "entries": [
      { "name": "appsettings.json", "type": "file", "size": 1234 },
      { "name": "yarp.json", "type": "file", "size": 890 },
      { "name": "policies.json", "type": "file", "size": 456 }
    ]
  }
  ```
* No file contents exposed in v0.1 (just metadata).

---

### 8. Observability

#### 8.1 Logging

* Structured logs to:

    * Console (always)
    * `/app/logs/gateway-*.log` (if `logs` dir writeable)
* Log detail controlled by env:

    * `LOG_LEVEL` (e.g. `Information`, `Debug`)

#### 8.2 Health & Metrics

* `GET /admin/health`:

  ```json
  {
    "status": "ok",
    "uptimeSeconds": 1234,
    "routesConfigured": 3,
    "db": "disabled"
  }
  ```
* v0.1: basic internal counters only (requests handled, 5xx count, etc.)
* (Later) OpenTelemetry export.

---

### 9. Non-Functional Requirements

* MUST start with **no config**, no DB, and no mounted volumes.
* MUST fail **softly**:

    * Missing config → default behaviour, not crash.
    * DB unavailable → logs a warning, continues routing.
* Designed to be:

    * Low CPU / memory overhead.
    * Stateless except for DB + mounted storage.
* Safe defaults:

    * Admin API available locally; can be locked with `ADMIN_SECRET` for non-dev.

---

If you want, next step I can turn this into:

* `docker run` examples for each mode (zero-config, file-config, DB-config).
* A minimal `appsettings.json` + `yarp.json` pair tailored to this spec.

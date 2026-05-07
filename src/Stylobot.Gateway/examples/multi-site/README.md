# StyloBot Gateway — Multi-Site / SaaS

One gateway fronts three virtual hosts with different protection levels:

| Host | Policy | Why |
|------|--------|-----|
| `admin.example.com` | `admin-strict` (block at 0.4) | Internal tool — no bots should reach it |
| `api.example.com` | `api` (skip page-load detectors) | REST clients don't have browser behavior |
| `www.example.com` | `default` (standard) | Public site — balanced detection |

> **Note:** `PathPolicies` in `appsettings.json` applies globally across all hosts. Per-host
> policy differentiation is handled by YARP routing — each host routes to a separate cluster,
> and you can configure per-cluster bot detection behavior at the application level using the
> `X-Bot-*` headers the gateway injects.

## Automatic HTTPS

This example uses ACME auto-cert (Let's Encrypt). Set `DOMAIN` and `ACME_EMAIL` in `.env`:

```bash
cp .env.example .env
# Edit: DOMAIN=example.com, ACME_EMAIL=ops@example.com, ADMIN_SECRET=...
docker compose up -d
```

The gateway automatically obtains and renews certificates for all virtual hosts.

## Quick Start

```bash
cp .env.example .env
docker compose up -d
```

Replace the stub nginx services in `docker-compose.yml` with your actual apps.

## Adding More Virtual Hosts

1. Add a new route to `config/yarp.json` with the host match condition.
2. Add a new cluster pointing at the new service.
3. Add the new service to `docker-compose.yml`.

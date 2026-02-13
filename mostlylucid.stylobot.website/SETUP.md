# Website Setup (Simple)

This guide is for operators who want the site online quickly.

## Requirements

- Linux server with Docker + Docker Compose
- Domain DNS pointing to the server
- Ports 80 and 443 open

## 1. Copy files to server

Copy these files to a folder such as `/opt/stylobot`:

- `docker-compose.yml`
- `Caddyfile`
- `.env.example`

## 2. Configure environment

```bash
cp .env.example .env
```

Set required values in `.env`:

- `BOTDETECTION_SIGNATURE_HASH_KEY`
- `BOTDETECTION_CLIENTSIDE_TOKEN_SECRET`

Optional for advanced stacks:

- `POSTGRES_PASSWORD`
- database connection fields

## 3. Start services

```bash
docker compose up -d
```

## 4. Verify

```bash
docker compose ps
curl -I http://localhost
curl http://localhost/health
```

## 5. Open the docs and demo

- `https://your-domain/docs`
- `https://your-domain/Home/LiveDemo`

## Update later

```bash
docker compose pull
docker compose up -d
```

For deeper deployment details, see `DEPLOYMENT.md`.

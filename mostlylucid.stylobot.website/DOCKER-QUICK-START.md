# Docker Quick Start

## Fast path

```bash
cp .env.example .env
# set required secrets
docker compose up -d
```

Open:

- `http://localhost`
- `http://localhost/docs`

## Verify health

```bash
docker compose ps
docker compose logs --tail 100
curl http://localhost/health
```

## Stop

```bash
docker compose down
```

## Rebuild local image

```bash
# from mostlylucid.stylobot.website
./build-docker.sh -c
# or Windows
.\build-docker.ps1 -Compress
```

See `SETUP.md` for first-time server setup and `DEPLOYMENT.md` for production rollout.

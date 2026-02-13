# Deploy on Server

This is the practical path for most teams.

## Minimal deployment

1. Copy `.env.example` to `.env`
2. Set required secrets
3. Run compose

```bash
cp .env.example .env
docker compose up -d
```

## Required values

- `BOTDETECTION_SIGNATURE_HASH_KEY`
- `BOTDETECTION_CLIENTSIDE_TOKEN_SECRET`
- `POSTGRES_PASSWORD` (if using database services)

## Verify deployment

```bash
docker compose ps
docker compose logs --tail 100
curl http://localhost/health
curl http://localhost/bot-detection/health
```

## Recommended rollout

1. Deploy in detect-only mode
2. Observe for at least 24 hours
3. Tune thresholds and policies
4. Enable stronger actions gradually

# Website Deployment Guide

Production-oriented deployment guide for the Stylobot website.

## Recommended rollout model

1. Deploy with detection/observation first
2. Review logs and dashboard behavior
3. Tune thresholds and path policies
4. Enable stronger enforcement gradually

## Deploy with Docker Compose

```bash
cp .env.example .env
# configure secrets and domain settings
docker compose up -d
```

## Required secrets

- `BOTDETECTION_SIGNATURE_HASH_KEY`
- `BOTDETECTION_CLIENTSIDE_TOKEN_SECRET`

If you use database containers:

- `POSTGRES_PASSWORD`

## Operational checks

```bash
docker compose ps
docker compose logs --tail 200
curl http://localhost/health
curl http://localhost/bot-detection/health
```

## Post-deploy checks

- Home page loads
- `/docs` loads and markdown pages render
- Mermaid diagrams render on docs pages
- Code blocks are highlighted
- `/Home/LiveDemo` responds
- Dashboard `/_stylobot` is reachable

## Rollback

If needed, redeploy previous image tag and run:

```bash
docker compose up -d
```

## References

- `SETUP.md`
- `DOCKER-QUICK-START.md`
- `docs/DEPLOYMENT-WORKFLOW.md`

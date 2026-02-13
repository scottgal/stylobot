# Security and Privacy

## Security principles

- Least privilege for deployment secrets
- Admin endpoints protected with `X-Admin-Secret` when configured
- Progressive enforcement rollout

## Privacy model

Stylobot emphasizes zero-PII detection artifacts and controlled response metadata.

## Operator checklist

- Rotate secrets regularly
- Limit response header detail in production
- Keep dashboard and admin endpoints access-controlled

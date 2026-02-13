# Operations

## Daily checks

```bash
curl http://localhost:5080/bot-detection/health
curl http://localhost:8080/admin/health
```

## Monitoring focus

- Error rates
- False positive trends
- Policy transitions
- Processing latency

## Change management

1. Deploy in observe mode first
2. Compare baseline and new behavior
3. Tighten policy thresholds incrementally
4. Keep rollback path ready

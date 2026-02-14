# Live Demo Guide

Use this page to validate behavior or run a product walkthrough.

## Surfaces to keep open

- `/Home/LiveDemo`: curated explanation UI
- `/_stylobot`: real-time dashboard feed
- `/bot-detection/check`: raw request check endpoint

## What to watch first

- `botProbability`: how likely traffic is automated
- `confidence`: how strong/consistent the evidence is
- `riskBand`: operator-facing severity bucket
- `topReasons`: why the score moved
- `recommendedAction`: policy output
- `processingTimeMs`: latency profile

## Recommended walkthrough sequence

1. Baseline request using a normal browser UA.
2. Bot-like request with obvious scanner/scraper UA.
3. Replay with repeated cadence to trigger behavioral shifts.
4. Compare reason stacks between events in dashboard.
5. Explain action mapping by risk band.

## Useful endpoints

- `/bot-detection/check`
- `/bot-detection/stats`
- `/bot-detection/health`
- `/_stylobot/api/summary`
- `/_stylobot/api/detections?limit=50`

## Interpretation tips

- Do not treat one detector hit as final truth.
- High probability with low confidence is often a challenge/throttle candidate, not immediate block.
- Watch trends over a request cluster, not single events.
- Validate policy behavior on real traffic before enforcement.

## Related docs

- `how-stylobot-works`
- `detectors-in-depth`
- `frequently-asked-questions`

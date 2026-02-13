# Frequently Asked Questions

## Is Stylobot a WAF replacement?

No. It is focused bot detection and response logic that you can run in your stack.

## Do I need an LLM to use Stylobot?

No. Heuristic mode works without LLM dependencies.

## Can I start without blocking traffic?

Yes. Start in observe mode and tune before enforcing blocks/challenges.

## Where are detection results available?

- Bot detection endpoints
- Dashboard UI
- Response headers (if enabled)
- Application context for custom logic

## Does it store personal data?

Stylobot is designed around a zero-PII architecture for detection artifacts. Keep your own logs and integrations aligned with your compliance requirements.

## What if I get false positives?

Lower aggressiveness, update path-specific policies, and validate with real traffic over time.

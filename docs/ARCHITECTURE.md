# Architecture

## Core components

- Detection middleware (`Mostlylucid.BotDetection`)
- UI/dashboard components (`Mostlylucid.BotDetection.UI`)
- Persistent storage (`Mostlylucid.BotDetection.UI.PostgreSQL`)
- Gateway mode (`Stylobot.Gateway`)

## Processing flow

1. Request enters middleware
2. Fast contributors execute
3. Policy determines escalation path
4. Result is stored/exposed through context and optional headers
5. Learning/reputation services update over time

## Deployment patterns

- Inline in app
- Reverse proxy gateway
- Full compose stack with DB and dashboard

---
id: sessions
title: "Sessions"
section: sessions
commercial: false
---

# Sessions

The Sessions tab tracks groups of related requests as coherent visits rather than individual hits. A session represents a continuous period of activity from a single visitor -- human or bot -- before they go quiet for a defined idle period.

## What a session represents

When a visitor arrives on your site, StyloBot begins grouping their requests into a session. That session stays open as long as requests keep arriving within the idle window (typically a few minutes). When the visitor goes quiet, the session closes and is recorded.

This gives you a more meaningful unit than raw requests -- a bot making 500 requests in two minutes is one session, not 500 events.

## What the table shows

- **Session ID** -- a short identifier for the session. Click it to see every request it contains.
- **IP / Signature** -- the source and fingerprint for this session.
- **Classification** -- the overall verdict: Human, Bot, or Threat.
- **Duration** -- how long the session ran.
- **Requests** -- how many individual hits were made during the session.
- **Requests/min** -- the rate of activity. High rates are a strong bot signal.
- **Paths visited** -- the number of distinct paths accessed. Bots often visit many paths quickly; humans browse more selectively.

## What to look for

Very short sessions with high request counts and many distinct paths are the clearest bot pattern. Human sessions tend to be longer, slower, and more focused.

**Tip:** A session with hundreds of requests per minute but a human classification may indicate a misconfigured rate-limit rule -- flag it for review.

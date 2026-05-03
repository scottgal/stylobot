---
id: your-detection
title: "Your Detection"
section: always
commercial: false
---

# Your Detection

The "Your Detection" card shows how StyloBot has classified *you* -- the person currently logged in to the dashboard. It is a live self-check that confirms the detection pipeline is working and gives you a concrete example of what a human classification looks like.

## What the card shows

- **Classification** -- Human, Bot, or Threat. For dashboard operators this should always be Human.
- **Confidence score** -- how certain StyloBot is of your classification, from 0 to 100.
- **Signals detected** -- the specific behavioural clues that contributed to your score (e.g. "consistent mouse movement", "normal request cadence", "matching browser fingerprint").
- **Your IP** -- the address StyloBot sees for your current session.
- **Your signature** -- the browser fingerprint StyloBot assigned to your session.

## Why this is useful

If you are testing a detection rule change and want to verify it does not affect real users, your own card is an instant sanity check. If the card shows you as a Bot or gives you an unexpectedly low human confidence score, that is a signal that a recently changed rule may be too aggressive.

## What to look for

Your confidence score should be 85 or above during normal browsing. Scores below 70 for an active human user typically indicate a fingerprinting issue worth investigating.

**Tip:** Viewing the dashboard through a VPN or over a mobile connection may lower your confidence score -- this is expected behaviour, not an error.

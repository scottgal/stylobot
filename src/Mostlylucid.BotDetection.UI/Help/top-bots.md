---
id: top-bots
title: "Top Bots"
section: overview
commercial: false
---

# Top Bots

The Top Bots sidebar lists the bot signatures that have made the most requests during the current time window. It gives you a quick read on which automated actors are most active against your site right now.

## What is a signature?

A **signature** is a fingerprint -- a combination of behavioural and technical clues that StyloBot uses to recognise a particular bot or bot family. Think of it like a face recognition system, but for automated traffic. Two bots from the same tool will share a signature even if they come from different IP addresses.

Signatures can be based on:

- The pattern of headers a request sends
- How quickly requests arrive and in what order
- The claimed browser or crawler identity
- Mouse movement and interaction patterns (for browser-based bots)

## Reading the list

- **Name** -- the bot family or campaign name, if known. Unknown bots appear as a short hash.
- **Requests** -- how many times that signature has been seen in the current window.
- **% of total** -- that bot's share of all traffic, including humans.

## What to look for

A single signature making up a large share of total traffic is worth investigating. Click the signature name to jump to the Clusters tab and see where those requests came from and what they targeted.

**Tip:** Well-behaved crawlers like Googlebot will appear here too -- that is normal and expected.

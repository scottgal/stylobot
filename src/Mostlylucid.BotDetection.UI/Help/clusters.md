---
id: clusters
title: "Bot Clusters"
section: clusters
commercial: false
---

# Bot Clusters

The Bot Clusters tab shows groups of bots that StyloBot has determined are working together -- using similar techniques, targeting the same paths, or arriving in coordinated waves. Clustering turns a list of individual bot sessions into a picture of organised campaigns.

## What clustering means

StyloBot compares the behaviour of every detected bot session against every other. Sessions that share enough in common -- the same signature, similar request patterns, overlapping target paths, requests arriving at similar times -- get grouped into a cluster.

A cluster is StyloBot's way of saying: "These automated actors are probably the same tool, the same operator, or the same coordinated attack."

## What the table shows

- **Cluster ID** -- a generated label for the group.
- **Members** -- how many distinct IP addresses or sessions belong to this cluster.
- **Signature family** -- the shared fingerprint or technique.
- **Top targets** -- the paths this cluster focused on.
- **First seen / Last seen** -- the time span of the campaign.
- **Risk level** -- Green / Amber / Red based on the threat signals within the cluster.

## Coordinated attacks

When a cluster shows many members arriving over a short window all targeting the same endpoint, that is a coordinated attack pattern -- often credential stuffing, inventory monitoring, or denial-of-service probing.

## What to look for

Large clusters (many members) with Red risk and a tight time window deserve immediate attention. A cluster that disappeared and then reappeared may indicate the operator changed IPs and restarted their campaign.

**Tip:** Clicking a cluster shows the individual sessions it contains -- useful for confirming the pattern before escalating.

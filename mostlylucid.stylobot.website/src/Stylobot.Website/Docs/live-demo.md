# Live Demo Guide

Use this page when presenting Stylobot or validating behavior.

## What to watch

- Bot probability
- Risk band
- Detector reasons
- Recommended action
- Processing time

## Suggested demo sequence

1. Send a standard browser User-Agent
2. Send a scraper/scanner User-Agent
3. Show score and reasons change in real time
4. Show dashboard event stream

## Demo endpoints

- `/bot-detection/check`
- `/bot-detection/stats`
- `/bot-detection/health`

## Interpreting results

- Low score does not always mean human certainty
- High score should be combined with policy context
- Start with monitoring before hard blocking

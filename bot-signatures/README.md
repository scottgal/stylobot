# Bot Detection Signature Repository

**Generated:** 2025-12-12 22:54:54 UTC
**Model:** ministral-3:3b
**Version:** 1.0.0

## Overview

Each file in this directory is a complete behavioral signature that can be replayed to test bot detection.

## File Format

Each scenario file (e.g., `natural-browsing.json`, `rapid-scraper.json`) contains:
- **scenarioName**: Kebab-case identifier
- **scenario**: Human-readable description
- **confidence**: Bot confidence score (0.0-1.0)
- **requests**: Array of sequential HTTP requests with timing
- **patterns**: Observable behavioral patterns
- **reasoning**: Why this is bot/human behavior

## Usage

Use `stylobot.bdfreplay.cli` to replay these signatures:
```
stylobot.bdfreplay.cli --signature natural-browsing.json --target http://localhost:5000
```

## License

Generated for bot detection research and development.

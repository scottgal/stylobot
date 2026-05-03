# Reputation Bias Detection

Applies learned reputation patterns from prior detections to influence scoring for current requests. Closes the learning feedback loop by feeding back patterns learned by the ReputationMaintenanceService into early detection stages.

## How It Works

The detector runs in Wave 1 (priority 45) after the User-Agent signal has been extracted. It queries the `IPatternReputationCache` for three pattern types: normalized User-Agent hash, IP range (/24 for IPv4, /48 for IPv6), and a combined signature (UA + IP + path). Neutral patterns are skipped.

For each matched non-neutral pattern, the detector creates a reputation contribution based on the pattern's state and accumulated support. **ConfirmedBad** and **ManuallyBlocked** patterns receive high-weight bot contributions (default weight 2.5) with the cached bot score as confidence. **Suspect** and **ProbablyBad** patterns receive moderate positive (bot-direction) contributions. **ProbablyGood** patterns receive negative (human-direction) contributions. The contribution weight scales with the pattern's support score.

Combined signature matches (UA + IP + path) receive an additional multiplier (default 1.5x) because they are more specific and less prone to false positives than individual UA or IP matches. Path normalization replaces GUIDs and numeric IDs with placeholders before hashing to group structurally similar requests.

Unlike FastPathReputationContributor (which handles instant allow/abort for high-confidence patterns), ReputationBias handles the softer scoring adjustments for patterns that are not yet conclusively classified. It does not trigger early exits.

## Signals Emitted

| Signal Key | Type | Description |
|---|---|---|
| `reputation.bias_applied` | boolean | Whether any reputation bias was applied |
| `reputation.bias_count` | integer | Number of reputation contributions applied |
| `reputation.can_abort` | boolean | Whether any pattern can trigger abort |
| `reputation.{category}.state` | string | Reputation state for matched pattern |
| `reputation.{category}.score` | number | Bot score for matched pattern |
| `reputation.{category}.support` | number | Observation count for matched pattern |

Where `{category}` is `useragent`, `ip`, or `combined`.

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "ReputationBiasContributor": {
        "Parameters": {
          "confirmed_bad_weight": 2.5,
          "combined_pattern_multiplier": 1.5,
          "reputation_weight_multiplier": 1.5,
          "min_support_for_bias": 3.0
        }
      }
    }
  }
}
```

## Parameters

| Parameter | Default | Description |
|---|---|---|
| `confirmed_bad_weight` | 2.5 | Weight for ConfirmedBad/ManuallyBlocked patterns |
| `combined_pattern_multiplier` | 1.5 | Extra multiplier for combined signature matches |
| `reputation_weight_multiplier` | 1.5 | General weight multiplier for all reputation bias |
| `min_support_for_bias` | 3.0 | Minimum observations before bias is applied |
| `match_normalized_ua` | true | Whether to match normalized UA patterns |
| `match_ip_range` | true | Whether to match IP range patterns |
| `ip_range_prefix_length` | 24 | CIDR prefix length for IPv4 range matching |
| `support_scaling_factor` | 0.1 | How much support scales the bias strength |
| `max_support_multiplier` | 2.0 | Maximum support-based weight multiplier |

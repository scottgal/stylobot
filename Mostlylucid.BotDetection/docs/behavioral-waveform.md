# Behavioral Waveform Detection

Wave: 2 (Late, after individual detectors)
Priority: 3
Trigger: Requires User-Agent signal from Wave 0

## Purpose

Correlates multi-request behavioral patterns from the same client signature to detect bot waveforms. Analyzes request timing regularity, path traversal patterns (depth-first vs breadth-first), request rate with HTTP/2 multiplexing awareness, session behavior, and client-side interaction signals.

## Signals Emitted

| Signal Key | Type | Description |
|------------|------|-------------|
| `waveform.signature` | string | Client signature (IP:UA hash) |
| `waveform.timing_regularity_score` | double | Coefficient of variation (low = bot) |
| `waveform.burst_detected` | bool | 10+ requests in 10 seconds |
| `waveform.path_diversity` | double | Unique paths / total requests ratio |
| `waveform.sequential_pattern` | bool | /page/1, /page/2, etc. detected |
| `waveform.traversal_pattern` | string | depth-first-strict, depth-first-loose, mixed |
| `waveform.interval_mean` | double | Mean seconds between requests |
| `waveform.interval_stddev` | double | Standard deviation of intervals |
| `waveform.request_rate` | double | Requests per minute (total) |
| `waveform.page_rate` | double | Page navigations per minute (HTTP/2 aware) |
| `waveform.user_agent_changes` | int | UA changes in session (spoofing indicator) |
| `waveform.session_duration_minutes` | double | Minutes from first to last request |
| `waveform.mouse_events` | int | Mouse event count from client-side |
| `waveform.keyboard_events` | int | Keyboard event count from client-side |
| `waveform.page_requests` | int | HTML page request count |
| `waveform.asset_requests` | int | JS/CSS/image request count |
| `waveform.api_requests` | int | JSON/XML API call count |
| `waveform.transition_page_to_asset` | double | Markov transition probability |
| `waveform.transition_page_to_page` | double | Markov transition probability |
| `waveform.asset_ratio` | double | Assets / total requests |

## Detection Logic

| Pattern | Threshold | Confidence | Description |
|---------|-----------|------------|-------------|
| Timing regularity | CV < 0.15 | 0.7 | Robotic timing precision |
| Request bursts | 10+ in 10s | 0.65 | Rapid-fire requests |
| Low path diversity | < 0.3 unique | 0.3 | Focused targeting |
| Scraper pattern | page-to-page > 0.7 | 0.6 | No asset loading (pure scraper) |
| High page rate | > 30/min | 0.75 | Superhuman browsing speed |
| Fast session | < 1min, 10+ requests | 0.7 | Speed runner |
| UA changes | > 1 change | 0.8 | User-Agent spoofing |
| No mouse events | 0 events | 0.4 | Headless browser |
| Human-like timing | CV 0.3-2.0 | -0.15 | Natural variance (negative = human) |

### HTTP/2 Multiplexing Awareness

The detector distinguishes between HTTP/2 multiplexed asset loading (normal browser behavior) and actual rapid page navigation. Content is classified into three types using Markov chain transition analysis:
- **Page**: text/html, application/xhtml (navigation)
- **Asset**: JS, CSS, images, fonts (auto-loaded by browser)
- **API**: JSON, XML, /api/ paths (data endpoints)

## Performance

Typical execution: 1-5ms (in-memory behavioral cache lookup + analysis).
Maintains 30-minute rolling history of up to 100 requests per client signature.

# Test Bot Detection Against www.mostlylucid.net
# This script tests various user agents against the live site to see bot detection in action

$baseUrl = "https://www.mostlylucid.net"

Write-Host "=== TESTING BOT DETECTION ON WWW.MOSTLYLUCID.NET ===" -ForegroundColor Cyan
Write-Host ""

# Function to test a user agent and show headers
function Test-BotDetection {
    param(
        [string]$Name,
        [string]$UserAgent,
        [string]$Path = "/",
        [hashtable]$ExtraHeaders = @{}
    )

    Write-Host "Testing: $Name" -ForegroundColor Yellow
    Write-Host "  UA: $UserAgent" -ForegroundColor Gray

    try {
        $headers = @{
            'User-Agent' = $UserAgent
        }

        # Add extra headers
        foreach ($key in $ExtraHeaders.Keys) {
            $headers[$key] = $ExtraHeaders[$key]
        }

        $response = Invoke-WebRequest -Uri "$baseUrl$Path" -Headers $headers -MaximumRedirection 0 -ErrorAction SilentlyContinue

        Write-Host "  Status: $($response.StatusCode)" -ForegroundColor Green

        # Show bot detection headers
        if ($response.Headers['X-Bot-Detected']) {
            Write-Host "  Bot Detected: $($response.Headers['X-Bot-Detected'])" -ForegroundColor Red
        }
        if ($response.Headers['X-Bot-Confidence']) {
            Write-Host "  Confidence: $($response.Headers['X-Bot-Confidence'])" -ForegroundColor Red
        }
        if ($response.Headers['X-Bot-Type']) {
            Write-Host "  Type: $($response.Headers['X-Bot-Type'])" -ForegroundColor Red
        }
        if ($response.Headers['X-Bot-Name']) {
            Write-Host "  Name: $($response.Headers['X-Bot-Name'])" -ForegroundColor Red
        }
        if ($response.Headers['X-Bot-Policy']) {
            Write-Host "  Policy: $($response.Headers['X-Bot-Policy'])" -ForegroundColor Magenta
        }
        if ($response.Headers['X-Bot-Processing-Ms']) {
            Write-Host "  Processing: $($response.Headers['X-Bot-Processing-Ms']) ms" -ForegroundColor Gray
        }

    } catch {
        if ($_.Exception.Response) {
            $statusCode = $_.Exception.Response.StatusCode.value__
            Write-Host "  Status: $statusCode (Blocked/Redirected)" -ForegroundColor Red

            # Try to get headers from error response
            $errorHeaders = $_.Exception.Response.Headers
            if ($errorHeaders) {
                $botDetected = $errorHeaders['X-Bot-Detected']
                if ($botDetected) {
                    Write-Host "  Bot Detected: $botDetected" -ForegroundColor Red
                }
            }
        } else {
            Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    Write-Host ""
}

# Test 1: Real Browser (should be allowed)
Test-BotDetection -Name "Chrome Browser (Human)" `
    -UserAgent "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" `
    -ExtraHeaders @{
        'Accept' = 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8'
        'Accept-Language' = 'en-US,en;q=0.9'
    }

# Test 2: Googlebot (verified bot - should be allowed)
Test-BotDetection -Name "Googlebot (Verified)" `
    -UserAgent "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"

# Test 3: Bingbot (verified bot - should be allowed)
Test-BotDetection -Name "Bingbot (Verified)" `
    -UserAgent "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)"

# Test 4: curl (suspicious - might get challenged)
Test-BotDetection -Name "curl (Suspicious)" `
    -UserAgent "curl/8.4.0"

# Test 5: Scrapy (scraper - likely blocked)
Test-BotDetection -Name "Scrapy (Scraper)" `
    -UserAgent "Scrapy/2.11 (+https://scrapy.org)"

# Test 6: HeadlessChrome (automation - suspicious)
Test-BotDetection -Name "HeadlessChrome (Automation)" `
    -UserAgent "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36"

# Test 7: Python requests (library - suspicious)
Test-BotDetection -Name "Python Requests (Library)" `
    -UserAgent "python-requests/2.28.0"

# Test 8: Security Scanner (malicious - should be blocked)
Test-BotDetection -Name "Nikto (Security Scanner)" `
    -UserAgent "Mozilla/5.00 (Nikto/2.1.6) (Evasions:None) (Test:Port Check)"

# Test 9: Social Bot - Facebook
Test-BotDetection -Name "Facebook Bot (Social)" `
    -UserAgent "facebookexternalhit/1.1"

# Test 10: Social Bot - Twitter
Test-BotDetection -Name "Twitter Bot (Social)" `
    -UserAgent "Twitterbot/1.0"

# Test 11: AI Crawler - GPTBot
Test-BotDetection -Name "GPTBot (AI Crawler)" `
    -UserAgent "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0; +https://openai.com/gptbot)"

# Test 12: AI Crawler - ClaudeBot
Test-BotDetection -Name "ClaudeBot (AI Crawler)" `
    -UserAgent "ClaudeBot/1.0; +https://anthropic.com"

# Test 13: Monitor - UptimeRobot
Test-BotDetection -Name "UptimeRobot (Monitor)" `
    -UserAgent "Mozilla/5.0 (compatible; UptimeRobot/2.0; http://www.uptimerobot.com/)"

# Test 14: Empty User Agent (very suspicious)
Test-BotDetection -Name "Empty User Agent (Very Suspicious)" `
    -UserAgent ""

Write-Host "=== TESTING COMPLETE ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  - Real browsers should pass with low/no bot detection" -ForegroundColor Gray
Write-Host "  - Verified bots (Google, Bing) should be allowed" -ForegroundColor Gray
Write-Host "  - Scrapers and automation should be challenged or blocked" -ForegroundColor Gray
Write-Host "  - Security scanners should be blocked immediately" -ForegroundColor Gray
Write-Host "  - AI crawlers behavior depends on site policy" -ForegroundColor Gray
Write-Host ""
Write-Host "Note: Actual behavior depends on www.mostlylucid.net bot detection configuration" -ForegroundColor Yellow
Write-Host ""
Write-Host "To see detailed analysis, check the site's bot detection logs" -ForegroundColor Yellow
Write-Host "If YARP learning mode is enabled, signatures will be captured in JSONL format" -ForegroundColor Yellow

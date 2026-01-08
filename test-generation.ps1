Write-Host "`n=== Testing LLM Bot User-Agent Generation ===`n"

for ($i=1; $i -le 3; $i++) {
    Write-Host "Bot Sample $i..."
    $body = @{
        model = "ministral-3:8b"
        prompt = "Generate ONE realistic bot/crawler User-Agent. Examples: python-requests/2.31.0, Scrapy/2.11.0, curl/8.4.0, Googlebot/2.1. Return ONLY the UA string.`nUser-Agent: "
        stream = $false
    } | ConvertTo-Json

    $response = Invoke-RestMethod -Uri "http://localhost:11434/api/generate" -Method Post -Body $body -ContentType "application/json"
    Write-Host "  Generated: $($response.response.Trim())"
    Write-Host ""
}

Write-Host "`n=== Testing LLM Human User-Agent Generation ===`n"It's 

for ($i=1; $i -le 3; $i++) {
    Write-Host "Human Sample $i..."
    $body = @{
        model = "ministral-3:8b"
        prompt = "Generate ONE realistic browser User-Agent. EXACT format: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36. Return ONLY the UA string.`nUser-Agent: "
        stream = $false
    } | ConvertTo-Json

    $response = Invoke-RestMethod -Uri "http://localhost:11434/api/generate" -Method Post -Body $body -ContentType "application/json"
    Write-Host "  Generated: $($response.response.Trim())"
    Write-Host ""
}

Write-Host "`n✅ Generation test complete!`n"

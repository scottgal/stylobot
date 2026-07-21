**To:** deploy-  
**From:** foss-  
**Priority:** urgent

# FINALIZED CONFIG VALUES FOR WEBSITE LIVE-FEED + COUNTRY WIDGET

Ready for docker-compose.test.yml environment setup. Three keys:

## 1. Live Source TYPE (confirmed)
```
Key:   StyloBot:Source:Live:Type
Value: signalr
```

## 2. Gateway Hub URL (confirmed key, you verify path)
```
Key:   StyloBot:Source:Live:Url
Value: http://gateway:8080/api/v1/hub
```
⚠️ **Verify:** Does gateway image 35f1b517 already expose the dashboard hub at `/api/v1/hub`? If not, flag that the gateway needs a rebuild. My fbb3f6fd fix handles the broadcast side (materializer → beacons), so the hub exists on the gateway — just confirm the path your staging gateway uses.

## 3. Country Widget Style (confirmed)
```
Key:   BotDetection:Dashboard:Materializer:CountryWidgetStyle
Value: map
```

## Ready for
Wire these to website service environment in docker-compose.test.yml, redeploy once dash-'s deletions/wiring lands on origin/main.

Gateway hub path is your verification — I'm proceeding with view implementations. No blocker.

# Protecting an Express app with StyloBot and Caddy

This tutorial walks through connecting an Express application to StyloBot bot detection via the Caddy gRPC middleware. By the end you will have three processes running together: an Express app on port 3000, a StyloBot sidecar on port 5090, and a Caddy reverse proxy on port 80 that calls the sidecar on every request and injects detection headers before the request reaches Express.

## What we are building

```
Browser/curl --> Caddy :80
                    |
                    +--> StyloBot sidecar :5090 (gRPC, per request)
                    |       injects X-StyloBot-* headers
                    |
                    +--> Express app :3000
                              reads headers, decides what to do
```

Caddy handles TLS termination and calls the sidecar synchronously on every request. If the sidecar says the request is a confirmed bot, Caddy can block it immediately with a 403 before Express ever sees it. If the action is `Challenge` or `Throttle`, Caddy passes the request through and Express decides what to do with it.

---

## Step 1: Start the StyloBot sidecar

The sidecar is a .NET 10 process that runs StyloBot's 49 detectors and exposes them over gRPC and REST on the same port.

```bash
cd Mostlylucid.BotDetection.Sidecar
dotnet run
```

You should see output like:

```
[10:01:02 INF] StyloBot sidecar starting on port 5090 (gRPC + REST)
[10:01:02 INF] Now listening on: http://0.0.0.0:5090
```

Verify it is healthy:

```bash
curl http://localhost:5090/health
```

Expected response:

```json
{"status":"healthy","mode":"sidecar","port":5090}
```

If you prefer Docker:

```bash
docker run -d --name stylobot-sidecar \
  -p 127.0.0.1:5090:5090 \
  scottgal/stylobot-sidecar:latest
```

---

## Step 2: Build Caddy with the stylobot module

The standard Caddy binary does not include the stylobot module. You need to build a custom binary using `xcaddy`.

Install xcaddy if you do not have it:

```bash
go install github.com/caddyserver/xcaddy/cmd/xcaddy@latest
```

Build Caddy with the module:

```bash
xcaddy build --with github.com/scottgal/caddy-stylobot
```

This takes a minute or two on first run (it downloads the module and its dependencies). The result is a `caddy` binary in your current directory.

---

## Step 3: Write the Caddyfile

Create a file called `Caddyfile` in the same directory as your `caddy` binary:

```caddyfile
:80 {
    # Call the StyloBot sidecar on every request.
    # Detected headers are injected onto the upstream request before Express sees it.
    stylobot {
        endpoint localhost:5090
        timeout  50ms
        on_block 403
    }

    # Forward to Express.
    reverse_proxy localhost:3000
}
```

The `on_block 403` line means Caddy will return a 403 immediately for any request the sidecar classifies as a confirmed bot with recommended action `Block`. All other requests, including ones flagged `Challenge` or `Throttle`, are forwarded to Express with the detection headers attached.

---

## Step 4: The Express application

Create a file called `app.js`. This is a complete Express application that reads the StyloBot detection headers and makes enforcement decisions.

```javascript
import express from 'express';

const app = express();
const PORT = 3000;

// Middleware: read StyloBot headers and act on them.
// Caddy already blocked confirmed bots (on_block 403), so by the time a
// request reaches here, it is either allowed, throttled, or needs a challenge.
app.use((req, res, next) => {
    const isBot      = req.headers['x-stylobot-isbot']      === 'true';
    const probability = parseFloat(req.headers['x-stylobot-probability'] ?? '0');
    const riskBand   = req.headers['x-stylobot-riskband']   ?? 'Unknown';
    const action     = req.headers['x-stylobot-action']     ?? 'Allow';
    const botName    = req.headers['x-stylobot-botname']    ?? '';
    const threatBand = req.headers['x-stylobot-threatband'] ?? 'None';

    // Log every request with its risk profile.
    console.log(
        `[${new Date().toISOString()}] ${req.method} ${req.path} ` +
        `isBot=${isBot} risk=${riskBand} action=${action}` +
        (botName ? ` name=${botName}` : '')
    );

    // Store on res.locals so route handlers can read them without re-parsing headers.
    res.locals.stylobot = { isBot, probability, riskBand, action, botName, threatBand };

    // Challenge: redirect suspicious-but-not-confirmed traffic to a CAPTCHA.
    if (action === 'Challenge') {
        return res.redirect(302, `/challenge?from=${encodeURIComponent(req.path)}`);
    }

    // Throttle: add an artificial delay for suspicious traffic.
    if (action === 'Throttle') {
        return setTimeout(() => next(), 2000);
    }

    next();
});

// Home page: shows detection context for debugging.
app.get('/', (req, res) => {
    const sb = res.locals.stylobot;
    res.send(`
        <h1>Hello from Express</h1>
        <p>isBot: ${sb.isBot}</p>
        <p>riskBand: ${sb.riskBand}</p>
        <p>action: ${sb.action}</p>
        <p>probability: ${sb.probability.toFixed(4)}</p>
        ${sb.botName ? `<p>botName: ${sb.botName}</p>` : ''}
    `);
});

// Challenge page: placeholder CAPTCHA.
app.get('/challenge', (req, res) => {
    const from = req.query.from ?? '/';
    res.status(429).send(`
        <h1>Human verification required</h1>
        <p>Please complete the CAPTCHA to continue.</p>
        <p>You were heading to: ${from}</p>
        <!-- Integrate your CAPTCHA provider here -->
    `);
});

// API endpoint: shows raw detection headers for debugging.
app.get('/debug/headers', (req, res) => {
    const stylobotHeaders = Object.fromEntries(
        Object.entries(req.headers).filter(([k]) => k.startsWith('x-stylobot-'))
    );
    res.json({ stylobot: stylobotHeaders });
});

app.listen(PORT, () => {
    console.log(`Express listening on http://localhost:${PORT}`);
});
```

Install the Express dependency:

```bash
npm init -y
npm install express
```

---

## Step 5: Start everything and test

Open three terminal windows.

**Terminal 1** (sidecar, if not already running):

```bash
cd Mostlylucid.BotDetection.Sidecar
dotnet run
```

**Terminal 2** (Express):

```bash
node app.js
```

**Terminal 3** (Caddy):

```bash
./caddy run
```

Test with a normal browser-like request:

```bash
curl -s \
  -H "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36" \
  http://localhost/
```

You should get the HTML response from Express. Check Terminal 2 for the log line:

```
[2026-05-02T10:05:33.000Z] GET / isBot=false risk=VeryLow action=Allow
```

To see the raw detection headers your app receives, hit the debug endpoint:

```bash
curl -s \
  -H "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36" \
  http://localhost/debug/headers | python3 -m json.tool
```

---

## Step 6: Simulate a bot

Now send a request with a minimal User-Agent that StyloBot recognizes as a tool:

```bash
curl -si -H "User-Agent: curl/7.68.0" http://localhost/
```

If `on_block 403` is active and the sidecar classifies this as a bot with action `Block`, you will see Caddy return:

```
HTTP/1.1 403 Forbidden
Content-Type: text/plain; charset=utf-8

Forbidden
```

The request never reached Express. Terminal 2 shows no log entry for it.

Try a GPTBot user-agent (a known AI scraper):

```bash
curl -si -H "User-Agent: GPTBot/1.0 (+https://openai.com/gptbot)" http://localhost/
```

Try an obviously headless automation tool:

```bash
curl -si -H "User-Agent: python-requests/2.28.0" http://localhost/
```

Each of these will produce different detection outcomes. Check the sidecar logs to understand which detectors fired.

---

## Step 7: Observe-only mode (let Express decide)

To disable Caddy's enforcement while still getting the headers, change `on_block` to `0` in your Caddyfile:

```caddyfile
:80 {
    stylobot {
        endpoint localhost:5090
        timeout  50ms
        on_block 0        # never block; Express decides
    }
    reverse_proxy localhost:3000
}
```

Reload Caddy:

```bash
./caddy reload
```

Now every request reaches Express regardless of the detection result. The middleware in `app.js` handles the `Block`, `Challenge`, and `Throttle` cases. This is useful when you want to:

- Roll out StyloBot without risking false positives during initial tuning
- Apply different responses per route (block on `/api/*` but challenge on `/`)
- Log bot traffic without affecting it, to measure the baseline before enforcement

---

## What is next

Once you have confirmed the headers are flowing and detection looks correct:

- Enable the StyloBot dashboard by running the full `Mostlylucid.BotDetection.Demo` instead of the sidecar. Visit `/_stylobot` to see real-time session timelines, behavioral radar charts, and cluster visualization.
- Explore the `@stylobot/node` npm package, which provides an Express middleware that wraps the REST API for cases where Caddy is not in the stack.
- Add TLS to Caddy with `tls internal` for local HTTPS, which enables JA3/JA4 TLS fingerprint detection (the sidecar receives more signal and improves accuracy).
- Tune `BotDetection:BotThreshold` in the sidecar's `appsettings.json` (default 0.7). A higher value like 0.85 reduces false positives at the cost of letting some borderline bots through.

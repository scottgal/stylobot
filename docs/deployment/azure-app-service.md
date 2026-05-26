# StyloBot on Azure App Service

You already have a site on App Service. You do not need to move it, repackage it, change DNS, put a separate VM in front of it, or change SKU tier. StyloBot reaches the inside of your existing App Service plan in one of three shapes, and you pick by runtime.

The whole detection stack ships in every shape. The shapes differ only in where the binary runs.

## Pick the path

| You're running | Path | Effort |
|---|---|---|
| ASP.NET Core (Linux or Windows App Service) | A: in-process NuGet | 2 lines of code, 1 config setting, redeploy |
| Node, Python, Java, PHP, Ruby, Go | B: App Service sidecar container | Add a sidecar, call it via loopback |
| Several App Services, or static SPA on Storage, or a third-party app you cannot modify | C: Container App gateway in front | Spin up one Container App, repoint Front Door |

All three end at the same place: a `/_stylobot` dashboard, SQLite reputation store on a persistent path, full detection stack on your traffic.

---

## Path A: ASP.NET Core in-process

### 1. Add the package

```bash
dotnet add package Mostlylucid.BotDetection
```

### 2. Wire it up in `Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStyloBot(dashboard =>
{
    dashboard.AllowUnauthenticatedAccess = false;
});

var app = builder.Build();

app.UseRouting();
app.UseStyloBot();
app.MapControllers();
app.Run();
```

`UseStyloBot()` wires the broadcast, detection, and dashboard middleware in the right order. Detection runs on every request; the dashboard is at `/_stylobot`.

### 3. Pin the database to a persistent path

App Service writes go away on the next deploy or scale event unless they land on `/home` (Linux) or `D:\home` (Windows). That path is a mounted Azure Files share, so it survives restarts and instance reshuffles.

Set `BotDetection:DatabasePath` via either `appsettings.json` or App Service Application Settings. The App Service setting wins.

**appsettings.Production.json:**

```json
{
  "BotDetection": {
    "DatabasePath": "/home/data/stylobot/botdetection.db"
  }
}
```

**Or as an Application Setting** in the portal (Configuration > Application settings):

```
BotDetection__DatabasePath = /home/data/stylobot/botdetection.db
```

On Windows App Service substitute `D:\home\data\stylobot\botdetection.db`.

> The Azure Files mount is reliable but not fast. SQLite over Azure Files is fine for the StyloBot working set (200 sessions/day per node is typical). Past a few thousand sessions/day per node, see the commercial PostgreSQL path at the bottom of this doc.

### 4. Allow Front Door / Application Gateway through, if you have one

If your App Service sits behind Azure Front Door or Application Gateway, the inbound IP StyloBot sees is the proxy's, not the client's. Configure forwarded-headers handling so StyloBot reads the real client IP from `X-Forwarded-For`:

```csharp
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
    // Add the Front Door / App Gateway egress range here.
});

app.UseForwardedHeaders();  // before UseStyloBot
app.UseStyloBot();
```

For Front Door, also add the `X-Azure-FDID` header check so direct hits to the App Service hostname are rejected. See [REVERSE_PROXY_SIGNALS.md](../REVERSE_PROXY_SIGNALS.md) for the full recipe per proxy.

### 5. Verify

Deploy. Hit your site once from a normal browser, then from curl:

```bash
curl -A "curl/8" https://your-app.azurewebsites.net/
```

Open `https://your-app.azurewebsites.net/_stylobot/`. You should see two sessions: one human-shaped (your browser visit), one bot-shaped (the curl). The curl session's probability should be > 0.7 with type `AutomatedTool`.

---

## Path B: non-.NET app, App Service sidecar container

App Service on Linux supports sidecar containers as a GA feature. Your existing app keeps running as the main container; `stylobot-sidecar` runs alongside it in the same App Service instance and talks to your app over loopback.

### 1. Add the sidecar

In the App Service portal, **Deployment Center > Containers > Sidecars**, add:

| Field | Value |
|---|---|
| Image source | Docker Hub (or your private registry) |
| Image | `docker.io/scottgal/stylobot-sidecar:latest` |
| Port | `5090` |
| Startup command | (leave blank) |

Add these as **Application Settings** on the App Service:

```
STYLOBOT_BIND=loopback
STYLOBOT__APIKEYS__0=<a long random string>
BotDetection__DatabasePath=/home/data/stylobot/botdetection.db
```

The sidecar refuses to bind to non-loopback interfaces without an API key configured, so the loopback default is the right shape for App Service.

### 2. Call the sidecar from your app

Your app makes one HTTP call per inbound request to `http://localhost:5090/api/v1/detect`, sends the inbound headers and source IP, and gets back a verdict.

**Node.js (Express)** using the official SDK:

```js
import express from 'express';
import { styloBotMiddleware } from '@stylobot/node';

const app = express();

app.use(styloBotMiddleware({
  mode: 'api',
  endpoint: 'http://localhost:5090/api/v1/detect',
  apiKey: process.env.STYLOBOT_API_KEY,
  onBot: (req, res) => res.status(403).send('Blocked')
}));
```

For Python, Ruby, PHP, Go, Java: post the request headers and source IP as JSON to `/api/v1/detect`, read the verdict, decide. The `@stylobot/core` source is the reference shape.

### 3. View the dashboard

The sidecar image is detection + REST API only; no dashboard view ships with it. Run `docker.io/scottgal/stylobot-ui` as a second App Service (or a second sidecar) pointed at the sidecar's `/api/v1/*` surface. See [yarp-integration.md](../../src/Mostlylucid.BotDetection/docs/yarp-integration.md) for the multi-host dashboard setup.

---

## Path C: Container App gateway in front

Use this shape when you have multiple App Services to protect from one place, a static SPA on Blob Storage that needs the same policy, or a third-party app you cannot modify. The gateway image is `stylobot-gateway`: it terminates traffic, runs detection, then forwards the surviving requests to your existing App Services.

### 1. Deploy the gateway as a Container App

```bash
az containerapp create \
  --name stylobot \
  --resource-group myrg \
  --image docker.io/scottgal/stylobot-gateway:latest \
  --target-port 8080 \
  --ingress external \
  --min-replicas 1 \
  --max-replicas 3 \
  --env-vars \
    "BotDetection__DatabasePath=/data/stylobot/botdetection.db" \
    "ReverseProxy__Clusters__app__Destinations__d1__Address=https://myapp.azurewebsites.net/"
```

Mount a small Azure Files share at `/data` (via the Container Apps storage feature) so the SQLite database survives restarts.

The gateway image is the same YARP-based binary the StyloBot project uses on its own site. Upstreams are configured via the standard YARP `ReverseProxy:Clusters` section; see [yarp-gateway.md](../../src/Mostlylucid.BotDetection/docs/yarp-gateway.md) for multi-upstream config.

### 2. Repoint Front Door

In Front Door, change the origin from the App Service URL to the Container App URL. Front Door now hits the gateway, the gateway runs detection, the gateway forwards survivors to your App Service.

### 3. Lock down direct App Service access

The whole point of putting a gateway in front is that the App Service should not be reachable except through it. Pick one:

- **Service-to-service header**: configure the gateway to add `X-Forwarded-For-StyloBot: <secret>`, configure the App Service to reject requests missing it. Lowest infrastructure cost.
- **VNet integration + Private Endpoint**: put the App Service on a private VNet, allow only the Container App's outbound subnet. Stronger isolation, slightly more infrastructure.

---

## What you get with FOSS

- Full detection stack: behavioural, header, IP, TLS / TCP / HTTP-2 / HTTP-3 fingerprinting, session vectors, entity resolution, threat scoring.
- Self-learning reputation: a signature flagged once accumulates evidence over time without manual list curation.
- Per-route action policies: block, throttle (stealth or status), challenge (proof of work), redirect to honeypot, log-only.
- Local dashboard at `/_stylobot` with sessions, signatures, clusters, countries, endpoints, threats.
- SQLite reputation store on `/home`. Nothing leaves your tenancy unless you configure it to.

This is the whole product. You can run a single App Service indefinitely without paying. Detection capability is not reduced versus the commercial product.

## What the commercial product adds

If you have **one** App Service, FOSS is everything you need.

You will start to feel the gap if you have **several**: dev / staging / prod, different regions, several customers, federated teams. Each App Service has its own dashboard, its own SQLite, its own policy file. A bot pattern observed by one node does not help the next one. Pushing a policy change to the fleet means redeploying the fleet.

The commercial product adds two things:

1. **Central management**: a control plane your fleet reports into. One dashboard across every App Service. One reputation store (PostgreSQL with pgvector) shared across nodes, so a bot fingerprinted on the marketing site is recognised on the checkout API. Cluster discovery runs against the federated dataset, not per-node.

2. **Real-time policy push**: change a policy from the control plane, nodes pick it up in seconds. No redeploy. No slot swap. No appsettings round-trip. The unit of change is a policy, not a deploy.

Both require the federated PostgreSQL backend and the control-plane host; that's why they are commercial. Detection itself is unchanged.

To migrate a FOSS node to a commercial control plane: add the control-plane endpoint and tenant credentials to App Service Application Settings, restart the App Service. The node keeps its local SQLite as a fallback and starts streaming reputation into the federated store.

---

## Common gotchas on App Service

- **TLS fingerprint**: App Service's front-end terminates TLS. JA3 / JA4 fingerprints are not available in Path A or Path B unless Front Door Premium (Bot Management) injects `X-JA3-Hash`. The behavioural and HTTP-2 detectors still fire without TLS fingerprints; detection accuracy on residential-proxy bots takes a small hit.
- **Cold start**: SQLite warms in seconds. The first few requests on a cold instance run without per-signature reputation cache; the behavioural pipeline still classifies them correctly.
- **Always On**: set App Service to Always On if you can. Without it, an idle App Service unloads and re-warms on the next request, which delays the first detection of a new attack burst.
- **Slot swaps**: blue / green slot swap restarts the new slot. SQLite on `/home` persists across the swap because both slots share the home directory. Reputation continuity survives.

---

## Next

- [REVERSE_PROXY_SIGNALS.md](../REVERSE_PROXY_SIGNALS.md): header injection recipes for Front Door, Application Gateway
- [configuration.md](../../src/Mostlylucid.BotDetection/docs/configuration.md): full options reference
- [action-policies.md](../../src/Mostlylucid.BotDetection/docs/action-policies.md): block, throttle, challenge, redirect semantics
- [aws-elastic-beanstalk.md](aws-elastic-beanstalk.md): same patterns, AWS edition

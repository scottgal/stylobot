# StyloBot on AWS Elastic Beanstalk

Beanstalk is the closest AWS equivalent to Azure App Service: managed ASP.NET Core hosting, single command to deploy, the platform handles the load balancer and auto-scaling. For .NET shops it is the fastest place on AWS to bolt StyloBot onto an existing app.

The doc covers the .NET in-process path (Path A) and the gateway-in-front path for when you need to protect non-.NET environments or several Beanstalk apps from one place (Path B). For pure-container workloads (Node, Python, Java, polyglot fleets) skip ahead to [aws-ecs-fargate.md](aws-ecs-fargate.md).

## Pick the path

| You're running | Path | Effort |
|---|---|---|
| .NET on Beanstalk (Windows or Linux platform) | A: in-process NuGet | 2 lines of code, 1 setting, redeploy |
| Several Beanstalk apps, or you want one gateway in front of all of them | B: ECS / Fargate gateway in front of ALB | One service, one image, repoint DNS |

Both end at the same place: a `/_stylobot` dashboard, SQLite reputation store on a persistent volume, full detection stack on your traffic.

---

## Path A: .NET in-process on Beanstalk

### 1. Add the package

```bash
dotnet add package Mostlylucid.BotDetection
```

### 2. Wire it up

In `Program.cs`:

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

### 3. Decide where the SQLite database lives

Beanstalk's default file system is ephemeral. Two persistent options:

**Option 3a: mount EFS (recommended).** Attach an EFS file system to the Beanstalk environment via a `.ebextensions` config. The file system mount point survives instance replacement and auto-scaling.

`.ebextensions/01-efs.config`:

```yaml
packages:
  yum:
    amazon-efs-utils: []

commands:
  01-create-mount:
    command: "mkdir -p /var/stylobot && mount -t efs fs-XXXXXXXX:/ /var/stylobot || true"
```

Then set in Beanstalk Configuration > Software > Environment properties:

```
BotDetection__DatabasePath = /var/stylobot/botdetection.db
```

**Option 3b: S3 + restore on startup (alternative).** If you would rather not run EFS, write a startup hook that pulls the SQLite database from S3 before the app starts and writes it back on shutdown. Lossy on a hard crash but cheaper. Out of scope for this runbook.

> The StyloBot working set per node is small (200 sessions/day is typical for a moderate site). EFS Standard is enough; you do not need EFS IA / Provisioned Throughput.

### 4. Trust the ALB's `X-Forwarded-For`

Beanstalk sits behind an Application Load Balancer (default) or Classic Load Balancer. The inbound IP your app sees is the load balancer's. Configure forwarded-headers handling so StyloBot reads the real client IP:

```csharp
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
    // Optionally add the ALB's CIDR range as a KnownNetwork.
});

app.UseForwardedHeaders();
app.UseStyloBot();
```

If you also have CloudFront in front of the ALB, the `X-Forwarded-For` chain has two hops. StyloBot reads the leftmost untrusted value by default; this is correct for two hops as long as the ALB and CloudFront are both treated as trusted proxies.

### 5. Deploy and verify

```bash
dotnet publish -c Release -o publish
cd publish && zip -r ../site.zip . && cd ..
eb deploy
```

Then:

```bash
curl -A "curl/8" https://your-beanstalk-env.elasticbeanstalk.com/
```

Visit `https://your-beanstalk-env.elasticbeanstalk.com/_stylobot/`. The curl session should appear as a bot; your browser session should appear as a human.

---

## Path B: gateway in front of the ALB

When you have multiple Beanstalk environments to protect from one place, or you want bot detection in front of non-.NET workloads sharing the same ALB, put the `stylobot-gateway` container on ECS / Fargate as a separate service.

This shape is a thin variant of the ECS / Fargate runbook. The detail is in [aws-ecs-fargate.md](aws-ecs-fargate.md); the difference here is what sits **behind** the gateway: your existing Beanstalk environment(s), reached by their internal load balancer URL.

```
Route 53 / CloudFront  ->  ALB  ->  ECS service (stylobot-gateway)  ->  Beanstalk env 1
                                                                    ->  Beanstalk env 2
                                                                    ->  static site on S3
```

The gateway container's YARP config holds the upstream addresses. See [yarp-gateway.md](../../src/Mostlylucid.BotDetection/docs/yarp-gateway.md).

---

## What you get with FOSS

- Full detection stack: behavioural, header, IP, TLS / TCP / HTTP-2 / HTTP-3 fingerprinting, session vectors, entity resolution, threat scoring.
- Self-learning reputation, accumulating evidence across visits.
- Per-route action policies: block, throttle, challenge, redirect, log-only.
- Local dashboard at `/_stylobot`.
- SQLite reputation store on EFS. Nothing leaves your VPC unless you configure it to.

Detection capability is not reduced versus commercial.

## What the commercial product adds

If you have **one** Beanstalk environment, FOSS is everything you need.

If you have **several** (dev / staging / prod, multi-region, several customers), each one has its own dashboard, its own SQLite, its own policy file. A bot fingerprinted in one environment does not help the next one. Pushing a policy change to the fleet means redeploying the fleet.

The commercial product adds:

1. **Central management**: one control plane the fleet reports into. One dashboard. One PostgreSQL + pgvector reputation store shared across nodes. Cluster discovery against the federated dataset, not per-node.

2. **Real-time policy push**: policy changes propagate to nodes in seconds. No redeploy.

To migrate a FOSS node onto a commercial control plane: add the control-plane endpoint and tenant credentials as Beanstalk environment properties, restart the environment. SQLite stays as a local fallback; reputation streams into the federated store.

---

## Common gotchas on Beanstalk

- **Instance replacement**: Beanstalk replaces unhealthy instances. If you used Option 3a (EFS), the new instance picks up the same SQLite database and reputation continuity is preserved. If you used Option 3b (S3 sync) you may lose minutes of accumulated state.
- **ALB health checks**: the ALB's health check user-agent (`ELB-HealthChecker`) is recognised as a benign monitoring bot by default. StyloBot's `AllowMonitoringBots` flag covers it.
- **CloudFront origin shield**: if CloudFront is in front of the ALB, the IP StyloBot sees is the CloudFront edge POP. Configure forwarded headers and add CloudFront's published IP ranges to the trusted-proxy list.
- **TLS fingerprint**: the ALB terminates TLS. JA3 / JA4 fingerprints are not available in this shape unless CloudFront (with the JA3 origin header feature) injects them. The behavioural and HTTP-2 detectors still work; detection on TLS-rotated bots takes a small hit.
- **Multi-AZ deployments**: each instance has its own in-memory state but shares the same SQLite file on EFS. Cross-instance reputation lookups go through the file system; in-flight per-request state is per-instance. For multi-AZ with heavy traffic the commercial PostgreSQL backend reduces I/O hot spots.

---

## Next

- [aws-ecs-fargate.md](aws-ecs-fargate.md): gateway-in-front runbook (Path B above, in detail)
- [REVERSE_PROXY_SIGNALS.md](../REVERSE_PROXY_SIGNALS.md): header injection recipes for CloudFront, ALB
- [configuration.md](../../src/Mostlylucid.BotDetection/docs/configuration.md): full options reference

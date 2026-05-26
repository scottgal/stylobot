# StyloBot on AWS ECS / Fargate

ECS / Fargate is the natural home for the `stylobot-gateway` image on AWS. The gateway runs as its own service, sits behind an ALB, and forwards survivors to your existing workload, which can be anything: another ECS service, an App Runner app, a Beanstalk environment, a static SPA on CloudFront, an on-prem origin reached via PrivateLink.

This is the gateway-in-front shape. Your application code does not change at all. The gateway terminates inbound traffic, runs the full detection stack, then either blocks the request or proxies it to your real upstream.

## When to use this runbook

| Your situation | Use this runbook |
|---|---|
| Polyglot app (Node, Python, Go, Java, PHP, Ruby) on ECS | Yes |
| Several services to protect from one place | Yes |
| Static SPA on S3 / CloudFront that needs bot protection | Yes (gateway is the origin) |
| Third-party app you cannot modify | Yes |
| ASP.NET Core on Beanstalk, single environment | Use [aws-elastic-beanstalk.md](aws-elastic-beanstalk.md) Path A instead (cheaper, lower latency) |

## Architecture

```
Route 53 / CloudFront  ->  ALB  ->  ECS Fargate service: stylobot-gateway  ->  your existing workload
                                              |                              ->  another ECS service
                                              |                              ->  Beanstalk env
                                              |                              ->  static S3 / CloudFront
                                              v
                                   EFS mount for SQLite reputation
```

One stylobot service. Many possible upstreams. The gateway holds the YARP routing table.

---

## Step 1: prepare the task definition

The gateway image is `docker.io/scottgal/stylobot-gateway:latest`. It listens on port 8080 (HTTP) and 8443 (HTTPS); terminate TLS at the ALB and route HTTP to 8080.

Minimum task definition (Fargate, 0.5 vCPU, 1 GB memory is plenty for moderate sites):

```json
{
  "family": "stylobot",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "512",
  "memory": "1024",
  "executionRoleArn": "arn:aws:iam::ACCOUNT:role/ecsTaskExecutionRole",
  "containerDefinitions": [
    {
      "name": "stylobot-gateway",
      "image": "docker.io/scottgal/stylobot-gateway:latest",
      "portMappings": [
        { "containerPort": 8080, "protocol": "tcp" }
      ],
      "environment": [
        { "name": "BotDetection__DatabasePath", "value": "/data/stylobot/botdetection.db" },
        { "name": "ReverseProxy__Routes__r1__ClusterId", "value": "app" },
        { "name": "ReverseProxy__Routes__r1__Match__Path", "value": "{**catch-all}" },
        { "name": "ReverseProxy__Clusters__app__Destinations__d1__Address",
          "value": "https://your-existing-upstream.internal/" }
      ],
      "mountPoints": [
        { "sourceVolume": "stylobot-data", "containerPath": "/data" }
      ],
      "healthCheck": {
        "command": [ "CMD-SHELL", "wget -q -O /dev/null http://localhost:8080/admin/alive || exit 1" ],
        "interval": 30,
        "timeout": 5,
        "retries": 3
      }
    }
  ],
  "volumes": [
    {
      "name": "stylobot-data",
      "efsVolumeConfiguration": {
        "fileSystemId": "fs-XXXXXXXX",
        "rootDirectory": "/stylobot"
      }
    }
  ]
}
```

EFS holds the SQLite reputation store. The volume mount survives task replacement and scale events, so reputation accumulates over the life of the service rather than the life of a single task.

For multiple upstreams, add more `ReverseProxy__Routes__*` and `ReverseProxy__Clusters__*` entries; the full YARP route shape is documented in [yarp-gateway.md](../../src/Mostlylucid.BotDetection/docs/yarp-gateway.md).

---

## Step 2: create the ECS service

```bash
aws ecs create-service \
  --cluster my-cluster \
  --service-name stylobot \
  --task-definition stylobot \
  --launch-type FARGATE \
  --desired-count 2 \
  --network-configuration "awsvpcConfiguration={subnets=[subnet-a,subnet-b],securityGroups=[sg-stylobot],assignPublicIp=DISABLED}" \
  --load-balancers "targetGroupArn=arn:...stylobot-tg,containerName=stylobot-gateway,containerPort=8080" \
  --health-check-grace-period-seconds 60
```

Two replicas across two AZs is the sensible minimum. They share the EFS reputation store, so a request fingerprinted on replica A is recognised on replica B.

The security group needs:

- Inbound 8080 from the ALB's security group.
- Outbound 443 to your upstream (and to Docker Hub on first pull).
- Outbound 2049 (NFS) to the EFS mount target.

---

## Step 3: front it with an ALB

Standard ALB setup, with one wrinkle: enable the `X-Forwarded-For` preserve-host attribute so the gateway sees the real client IP, not the ALB's.

```bash
aws elbv2 modify-load-balancer-attributes \
  --load-balancer-arn arn:...stylobot-alb \
  --attributes Key=routing.http.xff_header_processing.mode,Value=append
```

Target group health check path: `/admin/alive` (HTTP 200 when the gateway is up).

---

## Step 4: lock down direct upstream access

If your upstream is another ECS service or a Beanstalk environment, its security group should now accept inbound traffic **only from the stylobot service's security group**. Updating that ingress rule is the moment the gateway becomes load-bearing; until then, attackers can route around the gateway by hitting the upstream's URL directly.

For S3 / static origins: use a CloudFront Origin Access Identity restricted so only the stylobot service's egress can fetch from S3, or move the static files behind the gateway via the YARP file-server feature.

---

## Step 5: repoint DNS

In Route 53, change the A / ALIAS record for your customer-facing hostname from your existing ALB (in front of the upstream) to the new ALB (in front of stylobot). Old ALB stays up but no longer receives public traffic.

---

## Step 6: verify

```bash
# Should be allowed (browser-like UA)
curl -H "User-Agent: Mozilla/5.0 (Macintosh) Chrome/120.0" https://yoursite.com/

# Should be blocked or throttled (curl UA)
curl -A "curl/8" https://yoursite.com/
```

Then visit `https://yoursite.com/_stylobot/`. The dashboard runs from the gateway. Both sessions should appear: one human-shaped, one bot-shaped.

> By default, `/_stylobot/*` requires authentication. For initial verification you can set `Dashboard__AllowUnauthenticatedAccess=true` as a task env var; **turn it off again before production traffic** or front the dashboard with an authenticated identity provider (Cognito / OIDC).

---

## Where the dashboard lives

The gateway image bundles the dashboard at `/_stylobot/*`. For multi-node deployments where you want a single dashboard host independent of the gateway, run `docker.io/scottgal/stylobot-ui` as a separate service pointed at the gateway's `/api/v1/*` REST surface. See [yarp-integration.md](../../src/Mostlylucid.BotDetection/docs/yarp-integration.md).

---

## What you get with FOSS

- Full detection stack: behavioural, header, IP, TLS / TCP / HTTP-2 / HTTP-3 fingerprinting, session vectors, entity resolution, threat scoring.
- Self-learning reputation across the gateway service.
- Per-route action policies: block, throttle, challenge, redirect, log-only.
- Dashboard at `/_stylobot`.
- SQLite reputation store on EFS, shared across replicas. Nothing leaves your VPC unless you configure it to.

Detection capability is not reduced versus commercial.

## What the commercial product adds

If you have **one** ECS / Fargate gateway in front of **one** application surface, FOSS is everything you need.

You will start to feel the gap when:

- You have several gateway services in several regions or accounts and want a single view across all of them.
- You want to change a policy in production without rolling the ECS service.
- Your fleet generates enough reputation traffic that SQLite over EFS becomes an I/O bottleneck (typically past several thousand sessions/day per node).

The commercial product adds:

1. **Central management**: a control plane your gateway fleet reports into. One dashboard across every region and account. One PostgreSQL + pgvector reputation store (managed by you or hosted by us), so a bot fingerprinted in `us-east-1` is recognised in `eu-west-1`. Cluster discovery runs against the federated dataset.

2. **Real-time policy push**: change a policy from the control plane, gateway services pick it up in seconds. No ECS service update. No task replacement. No deployment circular reasoning between policy change and detection of the bot that prompted it.

To migrate a FOSS gateway onto a commercial control plane: add the control-plane endpoint and tenant credentials as task env vars, redeploy the service. SQLite remains as a local fallback; reputation streams into the federated store.

---

## Common gotchas on ECS / Fargate

- **EFS performance mode**: Bursting (default) is fine for the StyloBot working set. Provisioned Throughput is overkill unless you cross several thousand sessions/day per node.
- **Task replacement**: Fargate replaces tasks during deploys. The new task picks up the same EFS-backed SQLite, so reputation continuity survives. In-memory caches re-warm in seconds.
- **Cold start**: the gateway image takes ~3 seconds to start and ~5 seconds to warm SQLite. Health-check grace period of 60 seconds is more than enough.
- **TLS fingerprint**: the ALB terminates TLS. JA3 / JA4 fingerprints are not available unless CloudFront with the JA3 origin header feature injects them. Behavioural and HTTP-2 detectors still fire; detection on TLS-rotated bots takes a small hit.
- **NAT Gateway egress**: the gateway service needs outbound to Docker Hub on first pull and to your upstream. If your VPC has no public egress, mirror the gateway image into ECR and pull from there.

---

## Next

- [yarp-gateway.md](../../src/Mostlylucid.BotDetection/docs/yarp-gateway.md): full YARP routing config, multi-upstream patterns
- [REVERSE_PROXY_SIGNALS.md](../REVERSE_PROXY_SIGNALS.md): header injection recipes for ALB, CloudFront
- [action-policies.md](../../src/Mostlylucid.BotDetection/docs/action-policies.md): block, throttle, challenge semantics
- [aws-elastic-beanstalk.md](aws-elastic-beanstalk.md): if your workload is .NET on Beanstalk, the in-process path is cheaper

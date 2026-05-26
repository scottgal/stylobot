# StyloBot deployment runbooks

These are the "I already have a site on X, how do I add StyloBot" docs. Each one is a sales-ready runbook for a specific hosting shape. They all end up at the same place: detection running in front of your traffic, a `/_stylobot` dashboard, SQLite reputation store, zero PII off your host.

## Pick your starting point

| You're running | Doc | Path shape |
|---|---|---|
| ASP.NET Core on Azure App Service | [azure-app-service.md](azure-app-service.md) | NuGet in-process, or sidecar container |
| ASP.NET Core on AWS Elastic Beanstalk | [aws-elastic-beanstalk.md](aws-elastic-beanstalk.md) | NuGet in-process |
| Anything on AWS ECS / Fargate | [aws-ecs-fargate.md](aws-ecs-fargate.md) | Gateway container in front of your task |
| Bare VM, container host, or anywhere else | [../install-linux-apt.md](../install-linux-apt.md) + [../brownfield-retrofit.md](../brownfield-retrofit.md) | Binary install, gateway shape |

Non-.NET stacks (Node, Python, PHP, Go, Ruby, Java) follow the **gateway-in-front** or **sidecar** pattern. The cloud-specific docs cover both.

## What's the same everywhere

- The full detection stack ships in every shape. FOSS never has reduced detection capability versus commercial; commercial is purely additive.
- SQLite is the default reputation store and works on any persistent volume.
- The dashboard lives at `/_stylobot` when running with `AddStyloBot()` / `UseStyloBot()`, or on a separate `stylobot-ui` host when running headless.

## What changes per shape

- Where the SQLite database file goes (each cloud has its own "this disk survives a restart" path).
- Which header chain to trust for client IP and TLS fingerprint (Front Door, ALB, CloudFront, Application Gateway each inject differently).
- Whether you deploy via NuGet (in-process) or as a separate container in front.

## Commercial differences

The runbooks each end with a "what does the paid product add" section. The short version, repeated here:

1. **Central management**: one control plane your fleet reports into, one federated reputation store (PostgreSQL + pgvector), one dashboard view across every deployment. A bot fingerprinted on your marketing site is recognised on the checkout API.
2. **Real-time policy push**: change a policy from the control plane and nodes pick it up in seconds, with no redeploy. The unit of change is a policy, not a deploy slot.

Detection capability does not change. The commercial split is about operating a fleet, not about catching more bots.

## Marketplace listings

The runbooks here describe manual / scripted install. Future Azure Marketplace and AWS Marketplace listings will collapse step 1 of each doc into a single click; the rest stays identical. The publisher-side strategy for those listings lives in [marketplace-listing-plan.md](marketplace-listing-plan.md).

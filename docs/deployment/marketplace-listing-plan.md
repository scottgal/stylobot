# Cloud marketplace listing plan

Audience: us, planning how StyloBot gets listed on Azure Marketplace and AWS Marketplace. Customer-facing docs (one-click deploy from Marketplace) come later; this is the publisher-side strategy and checklist.

## Why we want this

Most prospects do not type "bot detection NuGet package" into Google. They search inside their cloud's marketplace, filter by category, click "Deploy". Marketplace listings are a discovery surface, a credibility signal (Microsoft / AWS vetted the image), and a billing channel (transactable through their invoice).

For StyloBot specifically, marketplace listings unlock three things:

1. **Discovery.** A free container or VM offer puts StyloBot in front of every Azure / AWS customer browsing for bot protection inside their portal. No SEO, no ad spend.
2. **Credibility.** Microsoft / AWS run image scans, certify the listing, vouch implicitly. Procurement teams take that more seriously than "download from GitHub".
3. **Transactable commercial billing.** A SaaS listing for the commercial control plane is sold through Microsoft / AWS invoices. The customer's existing cloud spend / committed-spend agreement covers it. No new vendor onboarding.

## Three offer shapes per cloud

Azure and AWS both support three offer types. GCP supports the same three but is deferred because the .NET-shop population on GCP is small.

| Offer type | What we publish | What it costs the customer | Strategic role |
|---|---|---|---|
| **Container** | A Docker image in our registry (or ACR / ECR) | $0/hour (free), customer pays cloud compute | Discovery + frictionless trial of the FOSS gateway |
| **VM image** | A pre-baked Ubuntu image with `stylobot` installed as a systemd service | $0/hour (free), customer pays cloud compute | For VM-centric customers who cannot or will not run containers |
| **SaaS** | A hosted control plane that customers subscribe to | Monthly subscription, billed through the cloud invoice | Commercial monetisation |

The free Container and VM offers are lead-generation for the SaaS offer. They are not the revenue line; they are the funnel.

## Publishing order

1. **Container offer on Azure Marketplace and AWS Marketplace.** Same `stylobot-gateway` image, two listings. Highest reach for lowest effort.
2. **VM image on both.** AMI for AWS, Managed VM Image for Azure. Same `stylobot` AOT binary baked into Ubuntu 22.04 with a systemd service. Customers who cannot run containers (policy or platform reasons) get an equivalent path.
3. **SaaS offer for the commercial control plane.** Transactable, monthly subscription. Customer's FOSS nodes (container or VM) federate into the SaaS tenant. This is where central management and real-time policy push monetise.

Step 1 alone is most of the value: it gets StyloBot in front of customers. Steps 2 and 3 widen the addressable market and add the revenue line.

---

## Azure Marketplace specifics

**Publisher onboarding**: register at [Partner Center](https://partner.microsoft.com/) as a Microsoft AI Cloud Partner Program member (formerly Microsoft Partner Network). Commercial account verification: company registration, bank account, tax forms. Calendar time: a week or two if all the paperwork is ready.

**Microsoft's cut**: 3% on transactable offers. Substantially lower than AWS or app stores; one of the strongest arguments for Azure-first.

**Co-sell**: once you are in the partner program and the listing is "co-sell ready" (a separate qualification on top of the published listing), Microsoft field sellers can recommend StyloBot to their enterprise customers and earn quota credit against it. This is the difference between "we have a Marketplace listing" and "Microsoft's enterprise sellers actively pitch our product". Aim for co-sell ready as a follow-on to the basic listing.

**Container offer specifics**:

- Image stored in **Azure Container Registry** owned by us (we mirror Docker Hub there for the listing).
- Offer wired to deploy to **Azure Container Apps** or **AKS** with a one-click ARM template.
- Image scanning, vulnerability remediation SLA, automated rebuild on base-image CVE.

**VM offer specifics**:

- Ubuntu 22.04 base, hardened per CIS benchmark.
- `stylobot` binary installed via apt from the Cloudsmith repo, configured as a systemd service.
- Pre-configured to listen on port 5080 (HTTP) and 5443 (HTTPS).
- Customer provides their upstream URL during deployment.
- Generation 2 VM image; supports Trusted Launch and Confidential Compute SKUs.

**SaaS offer specifics**:

- Transactable, customer signs up through Marketplace, lands on our portal to provision a tenant.
- Microsoft handles billing, remits to us monthly.
- Webhook-based subscription lifecycle (purchased / suspended / cancelled) wired into the portal.

**Estimated calendar time per listing**: 2 to 4 weeks once paperwork is ready. Image build is fast; certification (security scan, support information, marketing assets) is the slow part.

---

## AWS Marketplace specifics

**Publisher onboarding**: register as an AWS Marketplace seller. Tax interview, bank account for disbursements, public-facing seller profile. Calendar time: a week or two.

**AWS's cut**: 3% on private offers, sliding higher (up to 15% historically) on public offers depending on contract terms. AWS Marketplace renegotiated their fee structure in 2024 and the current SaaS cut is around 3%; verify on the seller portal at the time of listing.

**Co-sell**: AWS Partner Network (APN) tiers (Select / Advanced / Premier). At Advanced and above, AWS field sellers can recommend the listing and earn co-sell credit. Same mechanic as Microsoft, slightly different qualification path.

**Container offer specifics**:

- Image stored in **Amazon ECR** owned by us (mirrored from Docker Hub for the listing).
- Listed for deployment to **ECS / Fargate**, **EKS**, **App Runner**.
- AWS scans the image. Critical CVEs must be resolved before the listing is published; a remediation SLA applies to listed images.

**AMI offer specifics**:

- Ubuntu 22.04 base AMI.
- `stylobot` binary installed via apt from Cloudsmith, configured as a systemd service.
- Default port 5080; customer points DNS / load balancer at it.
- Available across all major AWS regions (we choose the launch set; expansion is incremental).

**SaaS offer specifics**:

- AWS SaaS Contracts (annual commitment) or AWS SaaS Subscriptions (monthly metered).
- Customer subscribes through Marketplace, lands on our portal to provision a tenant.
- AWS handles billing, remits monthly. Customer pays out of their AWS bill / committed-spend agreement (most enterprises strongly prefer this).
- Subscription lifecycle webhooks (SNS notifications) wired into the portal.

**Estimated calendar time per listing**: 3 to 6 weeks. AWS's certification is more thorough than Azure's; expect a few back-and-forth rounds on the security scan and the listing copy.

---

## Cross-cutting requirements

These apply to both clouds and gate all the listings.

**Image hygiene**

- Base image refreshes monthly minimum (we already build off `mcr.microsoft.com/dotnet/aspnet:10.0-alpine`, which Microsoft refreshes).
- CVE response SLA: critical = 7 days, high = 30 days. Both clouds publish their image scan results to you and the customer.
- Reproducible builds. The CI pipeline produces the same image hash from the same source revision; both Marketplaces show that build provenance to customers.

**Documentation deliverables (already mostly written)**

- Per-cloud quickstart (the runbooks in this directory).
- Configuration reference (`docs/configuration.md`).
- Security and privacy disclosure (`docs/SECURITY_AND_PRIVACY.md`).
- Support terms: response time SLAs, contact channel, escalation path. For the free Container / VM offers, "community support via GitHub Issues" is acceptable to both clouds.

**Marketing assets**

- 216x216 logo, 90x90 logo, 115x115 logo. PNG, transparent background.
- Screenshots: dashboard sessions tab, session detail with radar chart, clusters view. 1280x720 minimum, both clouds resize them.
- Short description (100 chars), long description (3000 chars), key features bullet list.
- "Lead form" wiring: who at our end gets the customer's email when they click "Contact" on the listing. Goes to our CRM.

**Billing wire-up (SaaS only)**

- Webhook endpoint on the portal: lifecycle events from Marketplace (Subscribed / Suspended / Reinstated / Unsubscribed).
- Token-exchange handshake on first sign-in (customer's Marketplace token in, our tenant URL out).
- Refund handling: both Marketplaces forward refunds upstream; we cancel the tenant on Unsubscribe.

---

## Recommended sequence and milestones

| Phase | Milestone | Calendar estimate |
|---|---|---|
| 0 | Publisher accounts on both clouds, paperwork complete | 2 weeks |
| 1 | Container offer on Azure Marketplace (free, FOSS gateway) | 3 weeks from end of phase 0 |
| 2 | Container offer on AWS Marketplace (free, FOSS gateway) | 4 weeks from end of phase 0, can overlap with phase 1 |
| 3 | VM / AMI offers on both clouds | 6 weeks |
| 4 | SaaS offer on Azure Marketplace (commercial control plane) | 4 to 6 weeks after the control plane is generally available |
| 5 | SaaS offer on AWS Marketplace | 4 to 6 weeks after Azure SaaS goes live |
| 6 | Co-sell qualification on both | Ongoing; rolling milestone, not gated |

Phase 0 starts the clock. Phases 1 and 2 are the discovery surface; they should be live before any marketing push. Phases 4 and 5 are gated on the commercial control plane being production-ready, which is a separate workstream.

---

## What this means for the customer-side docs

The runbooks in this directory describe manual / scripted install: NuGet package, container image pull, ECS task definition by hand. Once a Marketplace listing is live, step 1 of each runbook collapses into "click Deploy on the listing"; steps 2 onwards stay identical (configure storage path, verify, lock down upstream). The runbooks survive the listing rollout.

Future docs to add once listings exist:

- `azure-marketplace-deploy.md`: the "you just clicked Deploy on Azure Marketplace, here's what happens next" doc.
- `aws-marketplace-deploy.md`: same for AWS.

Both will link back into the existing per-cloud runbooks from the configuration step onwards.

---

## Open questions

- **Listing brand**: do we list as "StyloBot" or "Mostlylucid StyloBot"? Microsoft and AWS both display the publisher name prominently; the listing name should not duplicate it.
- **SaaS pricing model**: monthly subscription vs annual contract vs metered. Decision deferred until the commercial control plane is closer to GA.
- **Private offers**: Azure and AWS both support private offers (custom terms for a specific customer). Useful for enterprise deals. Add to phase 4+ once the SaaS offer is live and we have a customer asking.
- **Multi-tenant vs single-tenant SaaS**: most efficient is multi-tenant control plane with logical isolation per customer. Some enterprise customers will insist on single-tenant. Capture this as a procurement-driven SKU split when it comes up; do not pre-build it.

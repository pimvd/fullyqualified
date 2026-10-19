# Plan: FullyQualified

Oct 19, 2026 · Architect: Claude

## Summary

FullyQualified is an open-source tool that monitors certificates and DNS records for domains, with a paid hosted version for teams that do not want to self-host. I, Claude, am the originator and architect: I designed the concept and the architecture. A team of AI agents in Claude Code and Codex builds it, each with a fixed role.

Target audience: IT departments at European small and mid-sized businesses. The timing is right: the maximum certificate lifetime drops to 100 days in 2027 and 47 days in 2029, making manual management untenable, while NIS2 demands demonstrable control.

About the name: Waarom FullyQualified

## Problem and opportunity

Resellers sell certificates but offer little help with management and automation. Customers often do not know which certificates they have, where they run or when they expire.

- **The pain is in the last mile:** IIS, Azure (Key Vault, Application Gateway, Front Door, App Service), load balancers, Java keystores and appliances without ACME.
- **DNS drift goes unnoticed:** changed MX, NS or SPF records, and dangling CNAMEs that enable subdomain takeover.
- **Gap in the market:** Venafi, Keyfactor and AppViewX are too expensive and complex for smaller companies; tools such as certbot or Uptime Kuma lack inventory and context.
- **Compliance pull:** NIS2 requires demonstrable control, also from companies addressed as suppliers in the chain.

## Positioning

FullyQualified sells proof: the checks feed the NIS2 report, and the report is what customers buy.

- **Promise:** *Every domain, fully qualified: certificates, DNS & NIS2 compliance.*
- **Buyer:** an IT manager with little time, so value must show within 10 minutes: add a domain, connect a cloud, get the first report.
- **European:** hosted in the EU on Scaleway, with a .eu domain.

## Product and scope

See first, act later: monitoring and reporting come before any automated change.

| In scope | Out of scope (for now) |
| --- | --- |
| Certificate discovery via CT logs, endpoint scans and cloud connectors | Issuing certificates or becoming a CA |
| Inventory: expiry, issuer, type, manual or automated renewal | Changing anything in customer environments (until phase 4) |
| DNS checks: SPF, DKIM, DMARC, CAA, MX, NS, DNSSEC via public DNS queries | Receiving or processing DMARC reports (rua/ruf) |
| Baseline per record with an alert on change | Phone or 24/7 support |
| Dangling record and subdomain takeover detection | Uptime or performance monitoring |
| NIS2 report as PDF and Excel | Private endpoints from the hosted version (self-hosted only) |
| Alerts via email, Slack and webhooks (PagerDuty, Opsgenie) | DNS provider APIs such as Cloudflare or TransIP |

Cloud connectors inventory certificate stores, load balancers and CDNs, and DNS zones; Kubernetes ingress is read through the cluster API, independent of the cloud.

| Order | Cloud |
| --- | --- |
| 1 | Azure |
| 2 | AWS |
| 3 | Scaleway |
| 4 | STACKIT |
| 5 | Google Cloud |

Languages: Dutch and English at launch; German, French, Spanish and Italian on the roadmap. All UI text goes through resource files from day one.

Open question: Microsoft Teams as an alert channel, since most of the target audience works in Microsoft 365.

## Business model

Open core under AGPL-3.0: anyone may self-host for free, and anyone offering a modified version as a service must publish their changes.

| Open source (self-hosted) | Paid (hosted) |
| --- | --- |
| Discovery, scanning and cloud connectors | No maintenance; updates and backups handled |
| Dashboard and inventory | NIS2 reports as PDF and Excel |
| DNS checks and drift alerts | SSO and roles |
| Alerts | Multi-tenant and longer history |
| Private endpoints (runs inside your own network) | Later: automated renewal and deployment |

Pricing is per number of domains, with fixed monthly tiers; usage per check is measured internally for fair use only. Prices are still open.

| Tier | Domains | Includes |
| --- | --- | --- |
| Free | 3 | Certificate and DNS checks |
| Starter | 25 | Plus NIS2 report |
| Business | 100 | Plus cloud connectors and SSO |

Contributions: no external pull requests at first, since the agents write the code. Once the community grows, CLA Assistant Lite is switched on, so the license can still change later.

## Technology

A single .NET 10 container with Postgres, published as an image on GHCR with a docker-compose file that runs in one command. The hosted version runs on Scaleway.

| Component | Choice |
| --- | --- |
| Runtime | .NET 10 (LTS), ASP.NET Core minimal APIs |
| Data | Postgres via EF Core and Npgsql, migrations from day one |
| Background jobs | BackgroundService with a job table in Postgres (`FOR UPDATE SKIP LOCKED`) |
| TLS scanning | `SslStream` and `X509Certificate2` |
| CT logs | crt.sh JSON API |
| DNS | DnsClient.NET, public queries only |
| Cloud connectors | Azure SDK, AWS SDK, Scaleway, STACKIT and Google Cloud APIs, Kubernetes API for ingress |
| Frontend | Blazor (static SSR, interactive where needed) |
| Localization | `IStringLocalizer` with resource files; no hard-coded strings |
| Reports | PDF and Excel; libraries chosen in an ADR |
| Secrets | Encrypted with ASP.NET Data Protection |
| ACME (phase 4) | Certes |

Data model: an **asset** (domain, endpoint, cloud resource) has several **checks**; each check produces **results** and **changes**. Checks and cloud connectors are plugins behind one interface each, so a new check or cloud never requires a rebuild. Every table carries a `tenant_id`, even though the self-hosted version uses only one.

## Agent team

Five agents, each with one role, build the product with Claude Code and Codex. GitHub issues and pull requests are their only interface; nothing reaches main without review and a human merge.

| Agent | Tool | Responsible for | Output |
| --- | --- | --- | --- |
| Architect | Claude Code | Architecture, data model, ADRs, splitting work into issues | ADRs in `docs/adr`, issues with acceptance criteria |
| Dev | Claude Code | Implementing features per issue | Pull request |
| Tester | Codex | Unit and integration tests (xUnit, Testcontainers with Postgres) | Tests in the same PR, CI green |
| Reviewer | Codex | Review against ADRs, conventions and security | Approval, or back to dev |
| SEO | Claude Code | Landing page, docs, README, public scanner page | Content PRs with meta tags and structured data |

&#91;embedded content: agent team · 5 roles, 1 review gate\]

Dev and reviewer deliberately run on different models, so each catches the other's blind spots. Shared rules live in `AGENTS.md` (read by Codex) and `CLAUDE.md`; role prompts live as subagents in `.claude/agents/`.

Full operating model: Werkwijze agentteam

## Phasing

Four phases, each closed by a gate: move on only when the criterion under the gate is met.

&#91;embedded content: roadmap · 4 phases, 4 gates\]

The duration of each phase follows from the first customer interviews; phase 1 is deliberately kept small.

## Validation and go-to-market

The strongest signal: users asking whether the tool can also handle renewal for them. Then there is a paid next step.

- **Interviews:** 5 to 10 IT administrators at SMBs currently buying from a reseller. Ask where the manual work is; recurring systems decide what gets automated.
- **Free scanner:** a public page that scans a domain for certificate and DNS health. Lead magnet and demo in one.
- **Open-source launch:** r/selfhosted, r/sysadmin, r/AZURE and Hacker News (Show HN).
- **Support:** self-service docs, GitHub Issues for the community, email with a reply within one working day for paying customers.

## Repository setup

The repository exists with an AGPL-3.0 license; Claude Code sets up the rest from the setup plan.

| Topic | Decision |
| --- | --- |
| License | AGPL-3.0 (GNU Affero General Public License v3.0) |
| Contributions | No external pull requests at first; CLA Assistant Lite prepared and switched on later |
| Branching | Only main, short-lived feature branches, squash merge |
| Protection | Ruleset on main: pull request, human approval, required `ci` check |
| Agent identity | Dedicated GitHub App that can push but never merge |
| Language | Everything in English: code, docs, labels, commits |

Prompt and plan of approach for Claude Code: Claude Code setup plan

## Risks and preconditions

The biggest risk is a missed expiry at a customer; that is why monitoring comes first and acting comes later.

| Risk | Mitigation |
| --- | --- |
| Liability for a missed expiry | Monitoring first; clear terms; professional liability insurance once customers pay |
| Agents drifting from the architecture | ADRs and `AGENTS.md` as the source of truth; the architect agent approves structural changes |
| Defects missed by both dev and test agents | Cross-review on a different model; human merge to main |
| Support load | Self-service, no phone, email only |
| Security of stored cloud credentials | Read-only access, encryption, security review before the hosted launch |
| Scope creep into DMARC reporting or uptime | Explicitly out of scope (see Product and scope) |
| Trademark on the name | EUIPO check and trademark advice (see the naming tab) |

## First actions

- [ ] Commit the setup plan as `docs/SETUP_PLAN.md` and run Claude Code with the prompt
- [ ] Create the GitHub App and add the secrets
- [ ] Check the name: EUIPO, domains, GitHub organization
- [ ] Review and merge the setup pull requests and the proposed ADRs
- [ ] Plan 5 to 10 interviews with IT administrators
- [ ] Switch on the agents with `AGENTS_ENABLED`

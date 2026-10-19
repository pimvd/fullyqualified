# FullyQualified

Every domain, fully qualified: certificates, DNS & NIS2 compliance.

FullyQualified monitors certificates and DNS records for your domains and turns the results into a NIS2 report. It is open source (AGPL-3.0); a hosted version is planned. See [docs/PLAN.md](docs/PLAN.md) for the product plan.

## What it does

- **Certificate discovery** from Certificate Transparency logs (crt.sh) and TLS endpoint scans, with expiry, issuer and a manual/automated renewal guess.
- **DNS checks:** SPF, DKIM (common selectors), DMARC, CAA, MX, NS, DNSSEC, and dangling CNAMEs (subdomain takeover risk). Every value is a baseline; a change is recorded and alerted.
- **Alerts** on status changes and drift via Slack, a generic webhook (PagerDuty, Opsgenie) or email.
- **NIS2 report** as PDF and Excel.
- **UI** in English and Dutch (`EN`/`NL` in the header).

Nothing is ever changed in your environment: it only reads.

## Run it

```sh
docker compose up
```

Open http://localhost:8080. Without Docker you need .NET 10 and Postgres:

```sh
ConnectionStrings__Default="Host=localhost;Database=fullyqualified;Username=postgres;Password=postgres" \
  dotnet run --project src/FullyQualified.Web
```

Migrations are applied on startup.

## Configuration

Environment variables (use `__` for `:`):

| Setting | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings:Default` | local Postgres | Database |
| `Auth:Password` | unset | If set, the UI requires this password (HTTP Basic, any user name). Set it when exposing the app. |
| `Scanning:IntervalHours` | 6 | Rescan interval |
| `Scanning:AllowPrivateAddresses` | true | Set false on a shared install to block scans of private networks |
| `Dns:Server` | system | Resolver IP to use |
| `Alerts:SlackWebhook` | unset | Slack incoming webhook |
| `Alerts:Webhook` | unset | Generic JSON webhook |
| `Alerts:Email:Host/Port/Ssl/User/Password/From/To` | unset | SMTP |

## Develop

```sh
dotnet test
```

Not built yet: cloud connectors (Azure first), SSO and roles, multi-tenant hosting, automated renewal. Rules for contributors and agents are in [AGENTS.md](AGENTS.md).

## License

AGPL-3.0, see [LICENSE](LICENSE).

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
| `Auth:SetupCode` | unset | If set, creating the first account requires this code. Set it when the app is reachable before you have set it up. |
| `Auth:RpId`, `Auth:Origin` | request host | Passkey relying-party domain and origin. Set them when a reverse proxy changes the host name. |
| `Sso:Authority`, `Sso:ClientId`, `Sso:ClientSecret` | unset | OpenID Connect single sign-on (Entra ID, Google, Okta, Keycloak, ...). Set all three to show a "Sign in with ..." button. Redirect URI to register: `https://<host>/auth/sso/signin-oidc`. |
| `Sso:DisplayName` | `SSO` | Name on the sign-in button |
| `Sso:AllowedDomains` | unset | Comma-separated e-mail domains that may use SSO. Required for Entra ID, which does not send `email_verified`; without it only verified addresses are accepted. |
| `Sso:AutoProvision`, `Sso:DefaultRole` | false, Viewer | Create an account on first SSO sign-in instead of requiring an invitation. Never creates admins. |
| `Scanning:IntervalHours` | 6 | Rescan interval |
| `Scanning:AllowPrivateAddresses` | true | Set false on a shared install to block scans of private networks |
| `Dns:Server` | system | Resolver IP to use |
| `Alerts:SlackWebhook` | unset | Slack incoming webhook |
| `Alerts:Webhook` | unset | Generic JSON webhook |
| `Alerts:Email:Host/Port/Ssl/User/Password/From/To` | unset | SMTP |

## Sign in

The first visit opens `/setup`, where you create the first account (an administrator) with a passkey; there are no passwords. After that, admins invite people from the Users page with a single-use link and a role: viewer (read), member (also manage assets) or admin (also manage people). OpenID Connect single sign-on is optional, see the `Sso:*` settings. Passkeys need HTTPS, except on `localhost`.

## Website

The marketing site is plain static files in [`site/`](site), in English and Dutch, with no scripts, cookies or third-party requests. Preview it with `python3 -m http.server -d site 8090`. Its tests are in `tests/FullyQualified.Tests/SiteTests.cs` and run with `dotnet test`. The canonical domain `https://fullyqualified.eu` is a placeholder until the domain is confirmed: replace it in `site/` and in `SiteFixture.Origin`.

## Develop

```sh
dotnet test
```

Not built yet: cloud connectors (Azure first), multi-tenant hosting, automated renewal. Rules for contributors and agents are in [AGENTS.md](AGENTS.md).

## License

AGPL-3.0, see [LICENSE](LICENSE).

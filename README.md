# FullyQualified

Every domain, fully qualified: certificates, DNS & NIS2 compliance.

FullyQualified monitors certificates and DNS records for your domains and turns the results into a NIS2 report. It is open source (AGPL-3.0); a hosted version is planned. See [docs/PLAN.md](docs/PLAN.md) for the product plan.

## What it does

- **Certificates** found by TLS endpoint scans, with expiry, issuer and a manual/automated renewal guess.
- **Subdomain discovery** on request (*Find subdomains*): the public Certificate Transparency logs (crt.sh, with Cert Spotter as fallback) list hostnames that have a certificate. Nothing is added by itself: you review them, add what is yours and ignore the rest with a reason. See below.
- **DNS checks:** A/AAAA, SPF, DKIM (common selectors plus the ones you configure per domain, with key size and revocation), DMARC, CAA, MX, NS, DNSSEC, and dangling CNAMEs (subdomain takeover risk). Every value is a baseline; a change is recorded and alerted. NS, MX and CAA are cross-checked against other public resolvers.
- **Alerts** on status changes and drift via Slack, a generic webhook (PagerDuty, Opsgenie) or email.
- **NIS2 report** as PDF and Excel.
- **UI** in English and Dutch (`EN`/`NL` in the header).

Nothing is ever changed in your environment: it only reads.

## Run it

Install Docker with the Docker Compose plugin. From the repository root, build and start the application, PostgreSQL database and private SMTP relay:

```sh
docker compose up --build -d
```

Open http://localhost:8080. On a fresh database, follow `/setup` to create the first administrator with a passkey. Migrations are applied on startup; database and mail queue data are kept in Docker volumes.

Check the services, follow application logs, or stop the stack while keeping its data:

```sh
docker compose ps
docker compose logs -f app
docker compose down
```

The marketing website runs separately. With Python 3 installed, start it in another terminal from the repository root:

```sh
python3 -m http.server -d site 8090
```

Open http://localhost:8090 (English) or http://localhost:8090/nl/ (Dutch). Stop this server with Ctrl+C.

Without Docker you need the .NET 10 SDK and a running PostgreSQL server. Set the connection string to match your database, then run:

```sh
ConnectionStrings__Default="Host=localhost;Database=fullyqualified;Username=postgres;Password=postgres" \
  dotnet run --project src/FullyQualified.Web
```

The default development URL is http://localhost:5089. For email in this mode, configure a reachable SMTP server as described below.

### Built-in SMTP relay

Docker Compose includes a private Postfix relay and connects the application to it automatically. The relay has no published SMTP port and shares a dedicated network only with the application. Its queue is stored in the `mailqueue` volume, and its delivery logs are available through `docker compose logs -f postfix`.

For external delivery, copy `.env.example` to `.env` and set `FQ_MAIL_HOSTNAME` to the mail server's public hostname and `FQ_MAIL_FROM` to the default sender address. Then run `docker compose up --build -d`. The hostname must resolve to the outbound public IP; arrange matching reverse DNS with your hosting provider and allow outbound TCP port 25. On Scaleway Instances, SMTP must also be enabled in the security group. The default `.invalid` hostname and sender are placeholders for local startup, not working public mail identities.

Admins can open **Alerts → Send a DMARC test email**, enter From, To, Subject and Message, and submit the message. Postfix preserves the sender and does not add DKIM signatures, so it can test unauthorized sending for domains you are authorized to test. For that test, do not authorize this test relay in the tested domain's SPF record. Check the receiving server's `Authentication-Results` header for the DMARC result: a queued message or a rejection alone does not establish that result. A message may also be delayed or rejected because of IP reputation, reverse DNS or network restrictions. Queue acceptance is not confirmation of delivery.

This relay is also the default route for alarm emails and invitations. You can use an external SMTP service by setting `FQ_SMTP_HOST`, `FQ_SMTP_PORT`, `FQ_SMTP_SSL`, `FQ_SMTP_USER` and `FQ_SMTP_PASSWORD` in `.env`. `Ssl=true` enables STARTTLS; use the provider's STARTTLS port. Change `FQ_SMTP_SUBNET` if the default `172.30.25.0/29` overlaps an existing network. SMTP is unencrypted inside this dedicated Docker network; outgoing delivery uses opportunistic TLS.

When running the application outside Docker, configure its existing `Alerts__Email__*` environment variables with a reachable SMTP server. The Compose hostname `postfix` is available only to containers on the mail network; the relay is deliberately not exposed on the host.

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
| `PublicScan:Enabled`, `PublicScan:PerHour`, `PublicScan:Concurrent` | false, 10, 8 | The free public check at `/scan`, see above |
| `Scanning:IntervalHours` | 6 | Rescan interval for plans without one of their own |
| `Scanning:ManualCooldownMinutes` | 5 | Minimum time between two uses of "check everything now" per tenant |
| `Scanning:AllowPrivateAddresses` | true | Set false on a shared install to block scans of private networks |
| `Dns:Server` | system | Resolver IP to use |
| `Dns:CompareResolvers` | `1.1.1.1,9.9.9.9,8.8.8.8` | Public resolvers that NS, MX and CAA answers are cross-checked against to spot DNS tampering. Empty turns it off; resolvers that cannot be reached are ignored. |
| `Alerts:SlackWebhook` | unset | Slack incoming webhook |
| `Alerts:Telegram:BotToken`, `Alerts:Telegram:ChatId` | unset | Telegram bot and the chat, group or channel it writes to. Both are needed. |
| `Alerts:Webhook` | unset | Generic JSON webhook |
| `Alerts:Email:Host/Port/Ssl/User/Password/From/To` | unset | SMTP |

## Organizations (tenants)

Every account belongs to a tenant, which owns its own assets, results, users and alarm settings; tenants cannot see each other's data. The first tenant created, during `/setup`, is the **management tenant**. Its admins get a Tenants screen with every tenant (users, assets, certificates, expiring certificates, problems, last scan, last sign-in), can create new tenants and invite their first admin, and can help users in any tenant. A self-hosted install for one company simply has one tenant.

## Sign in

The first visit opens `/setup`, where you create the first account (an administrator) with a passkey; there are no passwords. After that, admins invite people from the Users page with a single-use link and a role (management admins can invite into any tenant): viewer (read), member (also manage assets) or admin (also manage people). OpenID Connect single sign-on is optional, see the `Sso:*` settings. Passkeys need HTTPS, except on `localhost`.

**Lost a passkey?** An admin chooses *Reset passkey* next to the user: a single-use link (24 hours) sets up a new passkey and removes the old ones. If nobody who could do that can sign in, the person running the server prints a link from the command line:

```sh
docker compose exec app dotnet FullyQualified.Web.dll recover alice@example.com --url https://fq.example.com
```

## Plans, limits and history

Every tenant is on a plan: **Free**, **Starter**, **Business** or **Custom**, edited by the platform owner on the **Limits** page. A plan limits domains, endpoints (hosts with a port, and hosts found in certificate logs), users (open invitations count) and whether the NIS2 report is included, and says how often checks run and how long history is kept: results, changes and alarm logs older than the period are removed automatically, but the newest result of every check is always kept. The platform owner can override any single limit per tenant; the Custom plan is for agreements per customer. The management tenant, which is the whole installation for a self-hoster, runs on Custom with no limits and keeps everything. Admins see their plan and usage on **Organization**; adding past a limit is refused with a message. The starting values are in `docs/PLAN.md`.

**Deleting a tenant.** An admin can delete their own organization from the Organization page, and the platform owner can delete any tenant from its page. Both must type the exact name, everything is removed at once (domains, results, history, alarms, users and their sign-in), and it cannot be undone. The management tenant cannot be deleted.

## When checks run, and running them now

Every asset is checked again once its newest result is older than the **scan interval** of its tenant's plan (for example daily on Free, six-hourly on Starter, hourly on Business; edit them on the Limits page, or override one tenant). A plan without an interval, such as Custom and so the whole of a self-hosted install, uses `Scanning:IntervalHours` (default 6). The scheduler looks once a minute, so a minute is the shortest interval, and a failed check is retried after 15 minutes whatever the interval is.

**Check everything now** (dashboard, members and admins) queues every check of every asset right away. To keep it from becoming a way around the interval, a tenant waits between uses: the **wait of its plan** (Free an hour, Starter 15 minutes, Business 5, edited on the Limits page and overridable per tenant; 0 means no wait), or `Scanning:ManualCooldownMinutes` (default 5) for a plan without one. Results show up within a minute on the Log page. **Scan now** on an asset's page does the same for one asset.

**Test settings** (Organization page, admins) checks that FullyQualified is set up to work for the organization and says in words what is wrong: plan and limits, whether results are fresh for the interval, whether alarms are configured and a test alarm through every channel (optional), and whether administrators can recover from a lost passkey. The platform owner also gets the server's checks: secure connection and passkey address, proxy headers, database migrations, session keys, mail server, single sign-on, name resolution, certificate-log reachability and the public check. Nothing is changed except the test alarm.

## Free public check

`/scan` is a page anyone can use without an account: type a public domain and see its TLS certificate and DNS email and security records. Nothing is stored. It is **off unless you set `PublicScan:Enabled=true`**, because it lets strangers make your server connect to hosts they choose. It therefore accepts only public host names on port 443, always refuses private and local addresses (whatever `Scanning:AllowPrivateAddresses` says), connects to the address it checked, limits each client address to `PublicScan:PerHour` checks (default 10) and `PublicScan:Concurrent` scans at once (default 8). Behind a reverse proxy set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, or every visitor shares the proxy's address and its limit. The website's form sends visitors here.

## Finding and reviewing subdomains

The certificate-log search is optional and runs only when you press **Find subdomains** on a domain, or on all domains from the **Review** page. It sends the domain name to crt.sh (and to Cert Spotter if crt.sh fails), which is why it never runs by itself. It is not part of a domain's status: a failed search is not a problem and never alarms.

What it finds is not monitored yet. The **Review** page lists every hostname, with the domain and service that found it, the issuer and the certificate's expiry. For each one you:

- **Add** it, which makes it an endpoint that is monitored like any other (within your plan's limit), or
- **Ignore** it with a **reason**, which is required. An ignored hostname is kept on record, does not come back at the next search, and can be restored.

Deleting an endpoint that came from the logs puts it on the ignored list too ("Removed from monitoring"). The dashboard says how many hostnames are waiting. The NIS2 report (Excel and PDF) has a *Discovery review* section with every decision, its reason, and who decided when, so you can show that what was found was looked at.

## How to fix what is found

Every finding and error comes with advice. Next to each one on the dashboard and on the asset page is a **How to fix** link to a page that says what the finding means, gives numbered steps to fix it, shows commands to check it yourself, an example record where one helps, and buttons that open the DNS lookup on the right record. It uses the asset's own names, so a DKIM problem shows the exact `selector._domainkey.example.com` to check, and a certificate name mismatch shows which names the certificate covers. It is in English and Dutch. When `Auth:Origin` is set, alarms by Slack, Telegram, webhook and email include the same links.

## Pausing and muting

Not everything needs attention all the time. On an asset's page, editors can:

- **Pause** the asset, optionally until a date. A paused asset is not checked, never alarms, and does not count as a problem (shown as *Paused* on the dashboard). It resumes by itself on the end date, or when someone presses *Resume now*. The dashboard can pause and resume several assets at once.
- **Mute a finding**, such as *No CAA record* or *Certificate expires soon*, optionally until a date. A muted finding stays visible but is not a warning and never alarms. After the end date it counts again.
- **Make a check optional** (*Make optional*), which mutes everything that check can report for that asset.
- **Mute a certificate** in the certificate list: it is left out of the expiring counts and the report, and its expiry never turns the TLS check into a warning. Trust problems still count.

Alarms follow what counts: muting a finding does not raise or clear an alarm, and nothing is sent for a paused asset or an optional check.

## Alarms and the log

Admins set where alarms go on the **Alarms** page: a Slack webhook, a Telegram bot and chat, a generic webhook (PagerDuty, Opsgenie), email recipients, the lowest status that should alarm, and whether recoveries and drift are reported. Webhook addresses and the Telegram bot token are stored encrypted, a tenant cannot point them at private networks when `Scanning:AllowPrivateAddresses` is false, and *Send a test alarm* shows what happened per channel. Without settings, the server-wide `Alerts:*` configuration is used. The **Log** page shows check results and delivered or failed alarms over 7, 30 or 90 days, with charts per day, per check and for changes, filters, and a table view of the chart data.

## Website

The marketing site is plain static files in [`site/`](site), in English and Dutch, with no scripts, cookies or third-party requests. Preview it with `python3 -m http.server -d site 8090`. Its tests are in `tests/FullyQualified.Tests/SiteTests.cs` and run with `dotnet test`. The canonical domain `https://fullyqualified.eu` is a placeholder until the domain is confirmed: replace it in `site/` and in `SiteFixture.Origin`.

## Develop

```sh
dotnet test
```

The database tests (tenant isolation, platform screens, alarms, recovery) run when `FQ_TEST_DB` points at a Postgres server, for example `FQ_TEST_DB="Host=localhost;Username=postgres;Password=postgres" dotnet test`; they create and drop their own databases. Without it they are skipped. CI sets it.

Not built yet: cloud connectors (Azure first), multi-tenant hosting, automated renewal. Rules for contributors and agents are in [AGENTS.md](AGENTS.md).

## License

AGPL-3.0, see [LICENSE](LICENSE).

# 14. Azure Container Apps deployment

Status: proposed.

- **Decision.** Each environment (`dev`, later `prd`) is one Azure resource group described in `infra/main.bicep`: Container Apps (consumption), PostgreSQL Flexible Server (Burstable B1ms), Container Registry (Basic), Log Analytics with a daily cap, and a cost budget with e-mail alerts. GitHub Actions deploys it with OIDC, no stored Azure password (`.github/workflows/deploy*.yml`, `infra/bootstrap.sh`).
- **Dev** runs one container (`Hosting__Role` unset, see ADR 10) to stay cheap. **Prd** sets `splitWorker=true`: a web app plus a worker app.
- **Flow.** Merge to `master` builds the image, pushes it to the environment registry, deploys the Bicep and binds the custom domain with a managed certificate. Prd will be a manual or tag-triggered run behind an environment approval.
- **Why.** Fits the Visual Studio subscription credit (EUR 130 a month, spending limit on). No Application Gateway: Container Apps ingress gives HTTPS and managed certificates for free.
- **Known trade-off.** Postgres is on its public endpoint (TLS required, Azure services allowed) to avoid the cost of a VNet and NAT gateway in dev. Prd should move to VNet integration with a private endpoint.
- **Mail.** No bundled Postfix: Azure blocks outbound port 25. Configure an SMTP relay on 587 in the Settings page.
- **Not included:** the marketing site (planned: Azure Static Web Apps), prd, alerting beyond the budget mail.

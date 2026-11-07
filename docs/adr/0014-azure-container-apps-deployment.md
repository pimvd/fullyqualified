# 14. Azure Container Apps deployment with Terraform

Status: proposed.

- **Decision.** Each environment (`dev`, `prd`) is one Azure resource group with its own Terraform state, described in `infra/`: Container Apps (consumption), PostgreSQL Flexible Server (Burstable B1ms), Log Analytics with a daily cap, and a cost budget with e-mail alerts. One shared Container Registry (Basic) and the state storage live in `fq-shared-rg`. `infra/bootstrap.sh` creates those and the passwordless GitHub-to-Azure identities (OIDC) once.
- **Separate containers.** `web` (UI and migrations, `Hosting__Role=web`) and `worker` (scans, scheduler, retention, alarms, `Hosting__Role=worker`) are two container apps from the same image. There is no single-container mode in Azure.
- **Build once.** A merge to `master` (`release` workflow) builds one image tagged with the first 12 characters of the commit sha, pushes it to the shared registry and deploys that tag to dev.
- **Prd tags are hard-coded** in `infra/envs/prd.tfvars` (`web_image_tag`, `worker_image_tag`). A prd release is a reviewed PR that changes them; the merge starts `deploy-prd`, which waits for approval on the `prd` GitHub environment. Prd is off until the repository variable `PRD_ENABLED` is `true`.
- **Why.** Fits the Visual Studio subscription credit (EUR 130 a month, spending limit on). No Application Gateway: Container Apps ingress gives HTTPS and managed certificates for free. The same artefact that ran in dev is what prd runs.
- **Domain.** The workflow binds the custom domain with a managed certificate after Terraform (`az containerapp hostname bind`), once the CNAME and `asuid` TXT records exist. Terraform ignores the ingress `custom_domain` so it does not undo this.
- **Known trade-off.** Postgres is on its public endpoint (TLS required, Azure services allowed) to avoid the cost of a VNet and NAT gateway. Prd should move to VNet integration with a private endpoint.
- **Mail.** No bundled Postfix: Azure blocks outbound port 25. Configure an SMTP relay on 587 in the Settings page.
- **Not included:** the marketing site (planned: Azure Static Web Apps), Terraform plan on PRs against real state, alerting beyond the budget mail.

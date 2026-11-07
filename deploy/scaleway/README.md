# Development environment on Scaleway (serverless)

One Serverless Container running the app, one Serverless SQL Database, a private registry for the image, all created by Terraform and deployed by a manual GitHub Actions workflow. Read [ADR 0013](../../docs/adr/0013-scaleway-serverless-dev-environment.md) first: this is for development and demos, not production.

> **Status:** validated and linted, but not yet run against a real Scaleway account. Expect to fix small things on the first run (the ADR lists what is unconfirmed).

## What it costs

| State | Cost |
| --- | --- |
| Container scaled to zero, database idle | storage only: the database bills at least 1 GB (a few cents a month); the container and a small registry cost close to nothing |
| Container awake | the free tier of Serverless Containers covers light use; beyond it you pay per second of CPU and memory |
| Database awake | billed per vCPU-second with a 5-minute minimum window |

The job runner polls the database every 3 seconds, so with `scanning_enabled` the database stays awake as long as the container does. That is why scanning is off by default. With it off, queued checks (including "Check everything now") do not run.

## What does not work here

- Email goes through an external SMTP server on port **587** (set the `FQ_SMTP_*` secrets, for example Scaleway Transactional Email). Ports 25 and 465 are blocked, so there is no Postfix, no DMARC test email, and the toolbox's mail checks on those ports fail.
- One container instance only (`max_scale = 1`): the scheduler lock is not reliable on a serverless database.
- No fixed outbound IP, an ephemeral disk, and a slow first request after the container has been asleep.

## One-time setup

1. **A deploy key.** In the Scaleway console create an IAM application for the pipeline with permission to manage Serverless Containers, Container Registry, Serverless SQL Databases, Object Storage and IAM applications, policies and API keys in your project, and create an API key for it. Note the access key, the secret key, the project ID and the organization ID.
2. **Tools on your machine:** `terraform` (1.6 or newer) and the `aws` CLI (only used for the S3-compatible API of Object Storage).
3. **Bootstrap the state bucket** (once):

   ```sh
   export SCW_ACCESS_KEY=... SCW_SECRET_KEY=... SCW_DEFAULT_PROJECT_ID=...
   deploy/scaleway/bootstrap.sh
   ```

   It creates a private bucket, remembers its name in `deploy/scaleway/.bootstrap.env` (ignored by git) and runs `terraform init`. It prints the bucket name.
4. **GitHub settings.** Create the environment `scaleway-dev` (add required reviewers if you want an approval step), the repository variable `TF_STATE_BUCKET`, and these secrets:

   | Secret | Required | Purpose |
   | --- | --- | --- |
   | `SCW_ACCESS_KEY`, `SCW_SECRET_KEY` | yes | the deploy key |
   | `SCW_DEFAULT_PROJECT_ID`, `SCW_DEFAULT_ORGANIZATION_ID` | yes | where everything is created |
   | `FQ_SETUP_CODE` | yes | `Auth:SetupCode`: needed to create the first account on a reachable instance |
   | `FQ_SMTP_HOST`, `FQ_SMTP_USER`, `FQ_SMTP_PASSWORD`, `FQ_MAIL_FROM` | no | outgoing email on port 587 |

## Deploy

Run the **deploy-scaleway** workflow (Actions, Run workflow). It creates the registry, builds and pushes the image tagged with the commit, applies Terraform and waits for `/health`. The address is printed at the end. Open it, go to `/setup` and create the first administrator with a passkey, using the setup code.

Tick *scanning_enabled* only while testing scans.

If passkey registration fails behind the proxy, pin the origin: set `public_origin` (for example `https://<container address>`) in the workflow's Terraform variables, or a `terraform.tfvars` for local runs.

## Run Terraform yourself

```sh
cd deploy/scaleway/terraform
export AWS_ACCESS_KEY_ID=$SCW_ACCESS_KEY AWS_SECRET_ACCESS_KEY=$SCW_SECRET_KEY
export TF_VAR_project_id=... TF_VAR_setup_code=... TF_VAR_image_tag=...
terraform init -backend-config=backend.hcl -backend-config="bucket=$(grep FQ_STATE_BUCKET ../.bootstrap.env | cut -d= -f2)"
terraform plan
```

The image must exist before the container can start: the workflow creates the registry first (`-target=scaleway_registry_namespace.main`), pushes the image, then applies everything.

## Remove everything

```sh
export SCW_ACCESS_KEY=... SCW_SECRET_KEY=... SCW_DEFAULT_PROJECT_ID=... SCW_DEFAULT_ORGANIZATION_ID=...
deploy/scaleway/destroy.sh
```

It asks you to type the state bucket's name, then destroys the container, namespace, registry and its images, the database **and its data**, and the IAM application, policy and key, and only after that deletes the state bucket. If the destroy fails it stops and keeps the bucket, so you can fix the problem and run it again. Your own deploy API key is not removed.

## Files

| Path | What |
| --- | --- |
| `terraform/` | the infrastructure (provider, variables, resources, outputs, static backend settings) |
| `bootstrap.sh` | creates the state bucket and runs `terraform init` |
| `destroy.sh` | removes everything including the state bucket |
| `../../.github/workflows/deploy-scaleway.yml` | the deploy pipeline |

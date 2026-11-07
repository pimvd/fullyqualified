#!/usr/bin/env bash
# One-time setup, safe to re-run. Creates what Terraform itself needs before it can run:
#   - the Terraform state storage in fq-shared-rg (Bicep: infra/bootstrap/state.bicep). The registry is Terraform (infra/shared).
#   - the environment resource group (fq-<env>-rg)
#   - passwordless GitHub -> Azure access (OIDC): one identity that builds images, one per environment
# Usage: infra/bootstrap.sh dev       (az login first, as someone who may create app registrations and role assignments)
#        infra/bootstrap.sh prd       (later)
set -euo pipefail

ENV="${1:?usage: bootstrap.sh <dev|prd>}"
REPO="pimvd/fullyqualified"
LOCATION="${AZURE_LOCATION:-westeurope}"
SUB=$(az account show --query id -o tsv)
TENANT=$(az account show --query tenantId -o tsv)
SHARED_RG="fq-shared-rg"
ENV_RG="fq-${ENV}-rg"
# the same name the workflows derive from the subscription id
STATE="fqtf${SUB//-/}"; STATE="${STATE:0:24}"

for p in Microsoft.App Microsoft.OperationalInsights Microsoft.DBforPostgreSQL Microsoft.ContainerRegistry \
         Microsoft.ManagedIdentity Microsoft.Consumption Microsoft.Storage Microsoft.Network Microsoft.Web; do
  az provider register -n "$p" --wait -o none
done

az group create -n "$SHARED_RG" -l "$LOCATION" -o none
az group create -n "$ENV_RG" -l "$LOCATION" -o none
az deployment group create -g "$SHARED_RG" -f "$(dirname "$0")/bootstrap/state.bicep" -p name="$STATE" -o none
STATE_ID=$(az storage account show -n "$STATE" -g "$SHARED_RG" --query id -o tsv)
SHARED_ID=$(az group show -n "$SHARED_RG" --query id -o tsv)

# create an app registration + service principal + federated credential, print its client id
identity() {
  local name="$1" subject="$2" id
  id=$(az ad app list --display-name "$name" --query '[0].appId' -o tsv)
  if [ -z "$id" ]; then
    id=$(az ad app create --display-name "$name" --query appId -o tsv)
    az ad sp create --id "$id" -o none
  fi
  if ! az ad app federated-credential list --id "$id" --query "[?subject=='$subject'] | [0]" -o tsv | grep -q .; then
    az ad app federated-credential create --id "$id" -o none --parameters "{
      \"name\": \"$(echo "$subject" | tr ':/' '--' | cut -c1-60)\",
      \"issuer\": \"https://token.actions.githubusercontent.com\",
      \"subject\": \"$subject\",
      \"audiences\": [\"api://AzureADTokenExchange\"]}"
  fi
  echo "$id"
}
grant() { az role assignment create --assignee "$1" --role "$2" --scope "$3" -o none 2>/dev/null || true; }

# The build identity applies infra/shared (registry, marketing site) and pushes images.
BUILD_ID=$(identity "fq-build-github" "repo:${REPO}:ref:refs/heads/master")
grant "$BUILD_ID" Contributor "$SHARED_ID"
grant "$BUILD_ID" AcrPush "$SHARED_ID"
grant "$BUILD_ID" "Storage Blob Data Contributor" "$STATE_ID"

ENV_ID=$(identity "fq-${ENV}-github" "repo:${REPO}:environment:${ENV}")
# Owner on its own resource group only: Terraform creates everything in it, including role assignments
grant "$ENV_ID" Owner "/subscriptions/${SUB}/resourceGroups/${ENV_RG}"
# the apps pull from the shared registry: Terraform assigns AcrPull on it and reads it
grant "$ENV_ID" "User Access Administrator" "$SHARED_ID"
grant "$ENV_ID" Reader "$SHARED_ID"
grant "$ENV_ID" "DNS Zone Contributor" "$SHARED_ID"
grant "$ENV_ID" "Storage Blob Data Contributor" "$STATE_ID"

if command -v gh >/dev/null && gh auth status >/dev/null 2>&1; then
  gh variable set AZURE_TENANT_ID --repo "$REPO" --body "$TENANT"
  gh variable set AZURE_SUBSCRIPTION_ID --repo "$REPO" --body "$SUB"
  gh variable set AZURE_CLIENT_ID_BUILD --repo "$REPO" --body "$BUILD_ID"
  gh api -X PUT "repos/${REPO}/environments/${ENV}" --silent
  gh variable set AZURE_CLIENT_ID --env "$ENV" --repo "$REPO" --body "$ENV_ID"
  gh secret set SETUP_CODE --env "$ENV" --repo "$REPO" --body "$(openssl rand -hex 8)"
  [ "$ENV" = prd ] && gh variable set PRD_ENABLED --repo "$REPO" --body true
  echo "GitHub is configured for '${ENV}'."
else
  cat <<EOF
Set these in GitHub (Settings > Secrets and variables > Actions):
  repository variables:   AZURE_TENANT_ID=${TENANT}  AZURE_SUBSCRIPTION_ID=${SUB}  AZURE_CLIENT_ID_BUILD=${BUILD_ID}
  environment '${ENV}':   variable AZURE_CLIENT_ID=${ENV_ID}   secret SETUP_CODE=<random string>
EOF
fi

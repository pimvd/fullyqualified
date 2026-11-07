#!/usr/bin/env bash
# One-time setup per environment: lets GitHub Actions deploy to Azure without any stored password (OIDC).
# Usage: infra/bootstrap.sh dev        (run with `az login` as an account that may create app registrations)
set -euo pipefail

ENV="${1:?usage: bootstrap.sh <dev|prd>}"
REPO="pimvd/fullyqualified"
LOCATION="${AZURE_LOCATION:-westeurope}"
RG="fq-${ENV}-rg"
SUB=$(az account show --query id -o tsv)
TENANT=$(az account show --query tenantId -o tsv)

for p in Microsoft.App Microsoft.OperationalInsights Microsoft.DBforPostgreSQL Microsoft.ContainerRegistry \
         Microsoft.ManagedIdentity Microsoft.Consumption; do
  az provider register -n "$p" --wait -o none
done

az group create -n "$RG" -l "$LOCATION" -o none

APP_ID=$(az ad app list --display-name "fq-${ENV}-github" --query '[0].appId' -o tsv)
if [ -z "$APP_ID" ]; then
  APP_ID=$(az ad app create --display-name "fq-${ENV}-github" --query appId -o tsv)
  az ad sp create --id "$APP_ID" -o none
fi

az ad app federated-credential create --id "$APP_ID" --parameters "{
  \"name\": \"github-${ENV}\",
  \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"repo:${REPO}:environment:${ENV}\",
  \"audiences\": [\"api://AzureADTokenExchange\"]
}" -o none 2>/dev/null || echo "(federated credential already exists)"

# Owner on this resource group only: the deployment creates a role assignment (AcrPull) for the app.
az role assignment create --assignee "$APP_ID" --role Owner \
  --scope "/subscriptions/${SUB}/resourceGroups/${RG}" -o none

PG_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=')

if command -v gh >/dev/null && gh auth status >/dev/null 2>&1; then
  gh api -X PUT "repos/${REPO}/environments/${ENV}" -o /dev/null
  gh variable set AZURE_CLIENT_ID --env "$ENV" --repo "$REPO" --body "$APP_ID"
  gh variable set AZURE_TENANT_ID --env "$ENV" --repo "$REPO" --body "$TENANT"
  gh variable set AZURE_SUBSCRIPTION_ID --env "$ENV" --repo "$REPO" --body "$SUB"
  gh variable set AZURE_LOCATION --env "$ENV" --repo "$REPO" --body "$LOCATION"
  gh secret set PG_PASSWORD --env "$ENV" --repo "$REPO" --body "$PG_PASSWORD"
  gh secret set SETUP_CODE --env "$ENV" --repo "$REPO" --body "$(openssl rand -hex 8)"
  echo "GitHub environment '${ENV}' is configured."
else
  cat <<EOF
Set these on the GitHub environment '${ENV}' (Settings > Environments):
  variables: AZURE_CLIENT_ID=${APP_ID}  AZURE_TENANT_ID=${TENANT}  AZURE_SUBSCRIPTION_ID=${SUB}  AZURE_LOCATION=${LOCATION}
  secrets:   PG_PASSWORD=${PG_PASSWORD}  SETUP_CODE=<any random string>
EOF
fi

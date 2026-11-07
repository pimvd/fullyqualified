#!/usr/bin/env bash
# One-time setup on a clean Scaleway account: creates the private Object Storage bucket that holds the Terraform state, then runs
# `terraform init` against it. Safe to run again; it reuses the bucket it created.
#
# Needs: the aws CLI (used only for the S3-compatible API), terraform, and these variables:
#   SCW_ACCESS_KEY, SCW_SECRET_KEY       a Scaleway API key that may manage Object Storage in the project
#   SCW_DEFAULT_PROJECT_ID               the project to use
# Optional: FQ_STATE_BUCKET to choose the bucket name (it must be globally unique), SCW_REGION (default fr-par).
set -euo pipefail

REGION="${SCW_REGION:-fr-par}"
ENDPOINT="https://s3.${REGION}.scw.cloud"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$HERE/.bootstrap.env"

for tool in aws terraform; do
  command -v "$tool" >/dev/null || { echo "Missing tool: $tool" >&2; exit 1; }
done
: "${SCW_ACCESS_KEY:?Set SCW_ACCESS_KEY}"
: "${SCW_SECRET_KEY:?Set SCW_SECRET_KEY}"
: "${SCW_DEFAULT_PROJECT_ID:?Set SCW_DEFAULT_PROJECT_ID}"

# The S3 API and the Terraform S3 backend both read the standard AWS variables.
export AWS_ACCESS_KEY_ID="$SCW_ACCESS_KEY"
export AWS_SECRET_ACCESS_KEY="$SCW_SECRET_KEY"
export AWS_DEFAULT_REGION="$REGION"

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  source "$ENV_FILE"
  echo "Reusing state bucket $FQ_STATE_BUCKET from $ENV_FILE"
else
  FQ_STATE_BUCKET="${FQ_STATE_BUCKET:-fq-tfstate-$(head -c 4 /dev/urandom | od -An -tx1 | tr -d ' \n')}"
fi

if aws s3api head-bucket --bucket "$FQ_STATE_BUCKET" --endpoint-url "$ENDPOINT" 2>/dev/null; then
  echo "Bucket $FQ_STATE_BUCKET already exists"
else
  echo "Creating private bucket $FQ_STATE_BUCKET in $REGION"
  aws s3api create-bucket --bucket "$FQ_STATE_BUCKET" --acl private --endpoint-url "$ENDPOINT" >/dev/null
fi

# Remember the bucket so destroy.sh can find and remove it. The file is ignored by git.
printf 'FQ_STATE_BUCKET=%s\nSCW_REGION=%s\n' "$FQ_STATE_BUCKET" "$REGION" > "$ENV_FILE"

terraform -chdir="$HERE/terraform" init -input=false -reconfigure \
  -backend-config=backend.hcl -backend-config="bucket=$FQ_STATE_BUCKET"

cat <<EOF

Done. State bucket: $FQ_STATE_BUCKET

Next:
  1. In GitHub, add the repository variable TF_STATE_BUCKET=$FQ_STATE_BUCKET and the secrets listed in deploy/scaleway/README.md.
  2. Run the "deploy-scaleway" workflow.
EOF

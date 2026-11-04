#!/usr/bin/env bash
# Removes everything the deployment created: the container, its namespace, the registry and its images, the serverless database,
# the IAM application, policy and key, and finally the Terraform state bucket itself. After this nothing is left to pay for.
#
# This deletes the database and all its data. It asks you to type the state bucket's name first. For non-interactive use set
# FQ_CONFIRM_DESTROY to the bucket name.
#
# Needs: aws CLI, terraform, SCW_ACCESS_KEY, SCW_SECRET_KEY, SCW_DEFAULT_PROJECT_ID (and SCW_DEFAULT_ORGANIZATION_ID).
# The API key you use to run this is yours and is not deleted.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$HERE/.bootstrap.env"

for tool in aws terraform; do
  command -v "$tool" >/dev/null || { echo "Missing tool: $tool" >&2; exit 1; }
done
: "${SCW_ACCESS_KEY:?Set SCW_ACCESS_KEY}"
: "${SCW_SECRET_KEY:?Set SCW_SECRET_KEY}"
: "${SCW_DEFAULT_PROJECT_ID:?Set SCW_DEFAULT_PROJECT_ID}"

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  source "$ENV_FILE"
fi
: "${FQ_STATE_BUCKET:?No .bootstrap.env found. Set FQ_STATE_BUCKET to the state bucket name.}"
REGION="${SCW_REGION:-fr-par}"
ENDPOINT="https://s3.${REGION}.scw.cloud"

export AWS_ACCESS_KEY_ID="$SCW_ACCESS_KEY"
export AWS_SECRET_ACCESS_KEY="$SCW_SECRET_KEY"
export AWS_DEFAULT_REGION="$REGION"

echo "This will delete EVERYTHING in project $SCW_DEFAULT_PROJECT_ID that this deployment created, including the database and its data,"
echo "and then the Terraform state bucket $FQ_STATE_BUCKET."
answer="${FQ_CONFIRM_DESTROY:-}"
if [[ -z "$answer" ]]; then
  read -r -p "Type the bucket name to continue: " answer
fi
[[ "$answer" == "$FQ_STATE_BUCKET" ]] || { echo "Not confirmed, nothing was changed." >&2; exit 1; }

# Required variables have no effect on a destroy, but Terraform insists on values for them.
export TF_VAR_project_id="$SCW_DEFAULT_PROJECT_ID"
export TF_VAR_image_tag="destroy"
export TF_VAR_setup_code="destroy"

terraform -chdir="$HERE/terraform" init -input=false -reconfigure \
  -backend-config=backend.hcl -backend-config="bucket=$FQ_STATE_BUCKET"
terraform -chdir="$HERE/terraform" destroy -input=false -auto-approve

# Only reached when the destroy succeeded, so a failed destroy never loses the state that says what is left.
echo "Deleting the state bucket $FQ_STATE_BUCKET"
aws s3 rb "s3://$FQ_STATE_BUCKET" --force --endpoint-url "$ENDPOINT"

rm -f "$ENV_FILE"
rm -rf "$HERE/terraform/.terraform"
echo "Everything is removed."

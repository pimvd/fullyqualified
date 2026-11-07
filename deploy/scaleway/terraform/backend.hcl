# Static settings for the S3-compatible state backend on Scaleway Object Storage.
# Pass the bucket separately: terraform init -backend-config=backend.hcl -backend-config="bucket=<name>"
# Credentials come from AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY (set them to the Scaleway access and secret key).
key    = "fullyqualified/dev/terraform.tfstate"
region = "fr-par"

endpoints = {
  s3 = "https://s3.fr-par.scw.cloud"
}

skip_credentials_validation = true
skip_region_validation      = true
skip_requesting_account_id  = true
skip_metadata_api_check     = true
skip_s3_checksum            = true

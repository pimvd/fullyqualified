terraform {
  required_version = ">= 1.6"

  required_providers {
    scaleway = {
      source  = "scaleway/scaleway"
      version = "~> 2.0"
    }
  }

  # State lives in a private Scaleway Object Storage bucket. The bucket is created by ../bootstrap.sh; its name is passed with
  # -backend-config="bucket=..." and the other settings come from backend.hcl.
  backend "s3" {}
}

provider "scaleway" {
  project_id = var.project_id
  region     = var.region
  zone       = var.zone
}

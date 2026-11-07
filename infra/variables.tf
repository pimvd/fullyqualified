variable "environment" {
  description = "dev or prd"
  type        = string
  validation {
    condition     = contains(["dev", "prd"], var.environment)
    error_message = "environment must be dev or prd."
  }
}

variable "domain" {
  description = "Public host name of the web app"
  type        = string
}

variable "acr_name" {
  description = "Name of the shared container registry (created by bootstrap.sh)"
  type        = string
}

variable "acr_resource_group" {
  type    = string
  default = "fq-shared-rg"
}

# Image tags. Dev gets the commit that was just built from the workflow (-var). Prd hard-codes them in envs/prd.tfvars,
# so a prd release is a reviewed change of that file.
variable "web_image_tag" {
  type = string
  validation {
    condition     = length(var.web_image_tag) > 0 && !startswith(var.web_image_tag, "REPLACE")
    error_message = "Set web_image_tag to a tag that exists in the registry."
  }
}

variable "worker_image_tag" {
  type = string
  validation {
    condition     = length(var.worker_image_tag) > 0 && !startswith(var.worker_image_tag, "REPLACE")
    error_message = "Set worker_image_tag to a tag that exists in the registry."
  }
}

variable "web_max_replicas" {
  type    = number
  default = 1
}

variable "worker_max_replicas" {
  type    = number
  default = 1
}

variable "setup_code" {
  description = "First-run setup code for /setup (optional)"
  type        = string
  default     = ""
  sensitive   = true
}

variable "budget_amount" {
  description = "Monthly budget in EUR for the resource group"
  type        = number
}

variable "budget_email" {
  type = string
}

variable "budget_start_date" {
  description = "First day of a month, RFC3339"
  type        = string
  default     = "2026-11-01T00:00:00Z"
}

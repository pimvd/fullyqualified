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

variable "dns_zone_name" {
  type    = string
  default = "fq.vandijkcloud.nl"
}

variable "acr_resource_group" {
  type    = string
  default = "rg-fq-shared-weu"
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

variable "postfix_image_tag" {
  type = string
  validation {
    condition     = length(var.postfix_image_tag) > 0 && !startswith(var.postfix_image_tag, "REPLACE")
    error_message = "Set postfix_image_tag to a tag that exists in the registry."
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

variable "mail_hostname" {
  description = "Public host name the mail relay announces (should match its outbound IP and reverse DNS, or that of the smarthost)"
  type        = string
}

variable "mail_from" {
  description = "Sender address of alert mail"
  type        = string
}

variable "postfix_networks" {
  description = "IPv4 ranges allowed to submit mail. The relay is only reachable inside the container app environment."
  type        = string
  default     = "10.0.0.0/8 100.64.0.0/10 172.16.0.0/12"
}

variable "smtp_relay_host" {
  description = "Smarthost the relay delivers through on port 587 (SES, Brevo, Mailgun, ...). Empty: try direct delivery, which Azure blocks."
  type        = string
  default     = ""
}

variable "smtp_relay_user" {
  type      = string
  default   = ""
  sensitive = true
}

variable "smtp_relay_password" {
  type      = string
  default   = ""
  sensitive = true
}

# Naming follows the Azure Cloud Adoption Framework: <type>-<workload>-<component>-<environment>-<region>.
variable "region_short" {
  description = "Short region code used in names (westeurope = weu)"
  type        = string
  default     = "weu"
}

# Tags (CAF recommended set): every resource carries Workload, Environment, Owner, Criticality, ManagedBy, Repository.
variable "owner" {
  type = string
}

variable "criticality" {
  description = "Business criticality tag: low, medium, high or mission-critical"
  type        = string
  default     = "low"
}

variable "cost_center" {
  description = "Optional CostCenter tag"
  type        = string
  default     = ""
}

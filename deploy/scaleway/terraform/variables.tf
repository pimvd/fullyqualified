variable "project_id" {
  description = "Scaleway project that holds everything. Not a secret, but not committed either: pass it with TF_VAR_project_id."
  type        = string
}

variable "region" {
  description = "Region for the registry, container and database."
  type        = string
  default     = "fr-par"
}

variable "zone" {
  description = "Default zone (required by the provider, nothing here uses zonal resources)."
  type        = string
  default     = "fr-par-1"
}

variable "environment" {
  description = "Short name used in resource names."
  type        = string
  default     = "dev"
}

variable "image_name" {
  description = "Image repository name inside the container namespace's registry."
  type        = string
  default     = "fullyqualified"
}

variable "image_tag" {
  description = "Image tag to run. The pipeline uses the commit SHA."
  type        = string
}

variable "min_scale" {
  description = "Instances kept running. 0 scales to zero when idle (almost no cost, and the background worker is off while asleep)."
  type        = number
  default     = 0
}

variable "cpu_limit" {
  description = "vCPU for the container in milli-vCPU (for example 560). Scaleway validates the allowed combinations with the memory limit at apply time."
  type        = number
  default     = 560
}

variable "memory_limit_mb" {
  description = "Memory for the container in MB."
  type        = number
  default     = 1024
}

variable "scanning_enabled" {
  description = "Run the background job runner. It polls the database every 3 seconds, which keeps the serverless database awake while the container is awake, so it is off by default."
  type        = bool
  default     = false
}

variable "db_min_cpu" {
  description = "Minimum database CPU units. 0 lets the database go idle."
  type        = number
  default     = 0
}

variable "db_max_cpu" {
  description = "Maximum database CPU units. Kept low so a runaway query cannot become an expensive one."
  type        = number
  default     = 2
}

variable "setup_code" {
  description = "Auth:SetupCode. Required to create the first account on a reachable instance."
  type        = string
  sensitive   = true
}

variable "smtp_host" {
  description = "External SMTP host on port 587 (for example Scaleway Transactional Email). Empty disables email. Ports 25 and 465 are blocked on Serverless Containers."
  type        = string
  default     = ""
}

variable "smtp_user" {
  description = "SMTP user."
  type        = string
  default     = ""
  sensitive   = true
}

variable "smtp_password" {
  description = "SMTP password."
  type        = string
  default     = ""
  sensitive   = true
}

variable "mail_from" {
  description = "Default sender address for alarm email and invitations."
  type        = string
  default     = ""
}

variable "public_origin" {
  description = "Optional https origin (for example https://fq.example.com) to pin passkeys to. Leave empty to use the request host; set it if passkey registration fails behind the Scaleway proxy."
  type        = string
  default     = ""
}

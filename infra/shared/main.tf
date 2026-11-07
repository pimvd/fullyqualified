# Shared by all environments (ADR 0014): the container registry and the marketing site.
# The resource group fq-shared-rg and the state storage are created by infra/bootstrap.sh (Bicep for the state).

terraform {
  required_version = ">= 1.9"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
}

variable "dns_zone_name" {
  description = "Zone delegated to Azure DNS. Everything under it is managed here and in infra/app."
  type        = string
  default     = "fq.vandijkcloud.nl"
}

variable "acr_name" {
  description = "Globally unique registry name, derived from the subscription id by the workflow"
  type        = string
}

data "azurerm_resource_group" "shared" {
  name = "fq-shared-rg"
}

resource "azurerm_container_registry" "acr" {
  name                = var.acr_name
  resource_group_name = data.azurerm_resource_group.shared.name
  location            = data.azurerm_resource_group.shared.location
  sku                 = "Basic"
  admin_enabled       = false
}

# Marketing site (the static files in site/). Free tier: no cost. The custom domain is bound by the deploy-site workflow.
resource "azurerm_static_web_app" "site" {
  name                = "fq-site"
  resource_group_name = data.azurerm_resource_group.shared.name
  location            = "westeurope"
  sku_tier            = "Free"
  sku_size            = "Free"
}

# DNS: delegate this zone once, at the provider of vandijkcloud.nl, with NS records for the name servers in the output.
resource "azurerm_dns_zone" "fq" {
  name                = var.dns_zone_name
  resource_group_name = data.azurerm_resource_group.shared.name
}

# The zone apex cannot hold a CNAME, so the marketing site is an alias record to the Static Web App.
resource "azurerm_dns_a_record" "site" {
  name                = "@"
  zone_name           = azurerm_dns_zone.fq.name
  resource_group_name = data.azurerm_resource_group.shared.name
  ttl                 = 300
  target_resource_id  = azurerm_static_web_app.site.id
}

output "dns_name_servers" {
  description = "Create NS records for the zone name pointing at these, at the provider of the parent domain"
  value       = azurerm_dns_zone.fq.name_servers
}

output "acr_login_server" {
  value = azurerm_container_registry.acr.login_server
}

output "site_name" {
  value = azurerm_static_web_app.site.name
}

output "site_default_hostname" {
  value = azurerm_static_web_app.site.default_host_name
}

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

output "acr_login_server" {
  value = azurerm_container_registry.acr.login_server
}

output "site_name" {
  value = azurerm_static_web_app.site.name
}

output "site_default_hostname" {
  value = azurerm_static_web_app.site.default_host_name
}

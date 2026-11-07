# FullyQualified on Azure Container Apps (ADR 0014). One state and one resource group per environment.
# The resource group (rg-fq-<env>-weu), the shared container registry and the state storage are created by infra/bootstrap.sh.

terraform {
  required_version = ">= 1.9"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
  # configured by the workflow with -backend-config (storage account, container and key per environment)
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
  # credentials come from the ARM_* environment variables (OIDC in GitHub Actions)
}

data "azurerm_resource_group" "this" {
  name = "rg-fq-${var.environment}-${var.region_short}"
}

data "azurerm_container_registry" "shared" {
  name                = var.acr_name
  resource_group_name = var.acr_resource_group
}

data "azurerm_dns_zone" "fq" {
  name                = var.dns_zone_name
  resource_group_name = var.acr_resource_group # the zone lives in the shared resource group
}

locals {
  record_name = trimsuffix(var.domain, ".${var.dns_zone_name}")
  suffix      = "${var.environment}-${var.region_short}"
  location    = data.azurerm_resource_group.this.location
  registry    = data.azurerm_container_registry.shared.login_server

  connection_string = "Host=${azurerm_postgresql_flexible_server.pg.fqdn};Database=fullyqualified;Username=fqadmin;Password=${random_password.pg.result};SSL Mode=Require"

  # the same settings for both containers; the web container adds the passkey origin
  common_env = {
    ASPNETCORE_FORWARDEDHEADERS_ENABLED = "true"
    Scanning__AllowPrivateAddresses     = "false"
    Dns__CompareResolvers               = "1.1.1.1,9.9.9.9,8.8.8.8"
    Alerts__Email__Host                 = "ca-fq-postfix-${local.suffix}"
    Alerts__Email__Port                 = "25"
    Alerts__Email__Ssl                  = "false"
    Alerts__Email__From                 = var.mail_from
  }
}

locals {
  tags = merge({
    Workload    = "fullyqualified"
    Environment = var.environment
    Owner       = var.owner
    Criticality = var.criticality
    ManagedBy   = "terraform"
    Repository  = "pimvd/fullyqualified"
  }, var.cost_center == "" ? {} : { CostCenter = var.cost_center })
}

resource "random_string" "suffix" {
  length  = 5
  upper   = false
  special = false
}

resource "random_password" "pg" {
  length  = 32
  special = false
}

# ---- identity: lets the container apps pull from the shared registry ----

resource "azurerm_user_assigned_identity" "apps" {
  name                = "id-fq-${local.suffix}"
  resource_group_name = data.azurerm_resource_group.this.name
  location            = local.location

  tags = local.tags
}

resource "azurerm_role_assignment" "acr_pull" {
  scope                = data.azurerm_container_registry.shared.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.apps.principal_id
}

# ---- logs: the first 5 GB a month are free, the daily cap stops ingestion above 0.2 GB a day ----

resource "azurerm_log_analytics_workspace" "law" {
  name                = "log-fq-${local.suffix}"
  resource_group_name = data.azurerm_resource_group.this.name
  location            = local.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 0.2

  tags = local.tags
}

# ---- database: reached over its public endpoint with TLS (see ADR 0014 for the trade-off) ----

resource "azurerm_postgresql_flexible_server" "pg" {
  name                          = "psql-fq-${local.suffix}-${random_string.suffix.result}"
  resource_group_name           = data.azurerm_resource_group.this.name
  location                      = local.location
  version                       = "17"
  sku_name                      = "B_Standard_B1ms"
  storage_mb                    = 32768
  backup_retention_days         = 7
  geo_redundant_backup_enabled  = false
  administrator_login           = "fqadmin"
  administrator_password        = random_password.pg.result
  public_network_access_enabled = true

  lifecycle {
    ignore_changes = [zone]
  }

  tags = local.tags
}

resource "azurerm_postgresql_flexible_server_database" "fq" {
  name      = "fullyqualified"
  server_id = azurerm_postgresql_flexible_server.pg.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# "Allow Azure services"
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure" {
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.pg.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# ---- container apps: web (UI + migrations) and worker (scans, scheduler, retention, alarms) ----

resource "azurerm_container_app_environment" "env" {
  name                       = "cae-fq-${local.suffix}"
  resource_group_name        = data.azurerm_resource_group.this.name
  location                   = local.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id

  tags = local.tags
}

resource "azurerm_container_app" "web" {
  name                         = "ca-fq-web-${local.suffix}"
  resource_group_name          = data.azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.apps.id]
  }

  registry {
    server   = data.azurerm_container_registry.shared.login_server
    identity = azurerm_user_assigned_identity.apps.id
  }

  secret {
    name  = "db-connection"
    value = local.connection_string
  }

  dynamic "secret" {
    for_each = var.setup_code == "" ? [] : [1]
    content {
      name  = "setup-code"
      value = var.setup_code
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = var.web_max_replicas

    container {
      name   = "web"
      image  = "${local.registry}/fq-web:${var.web_image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "ConnectionStrings__Default"
        secret_name = "db-connection"
      }
      env {
        name  = "Auth__Origin"
        value = "https://${var.domain}"
      }
      env {
        name  = "Auth__RpId"
        value = var.domain
      }
      dynamic "env" {
        for_each = var.setup_code == "" ? [] : [1]
        content {
          name        = "Auth__SetupCode"
          secret_name = "setup-code"
        }
      }
      dynamic "env" {
        for_each = local.common_env
        content {
          name  = env.key
          value = env.value
        }
      }
    }
  }

  depends_on = [azurerm_role_assignment.acr_pull, azurerm_postgresql_flexible_server_database.fq, azurerm_container_app.postfix]

  lifecycle {
    # the custom domain is bound by the deploy workflow (az containerapp hostname bind)
    ignore_changes = [ingress[0].custom_domain]
  }

  tags = local.tags
}

resource "azurerm_container_app" "worker" {
  name                         = "ca-fq-worker-${local.suffix}"
  resource_group_name          = data.azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.apps.id]
  }

  registry {
    server   = data.azurerm_container_registry.shared.login_server
    identity = azurerm_user_assigned_identity.apps.id
  }

  secret {
    name  = "db-connection"
    value = local.connection_string
  }

  template {
    # one replica schedules (Postgres advisory lock), all replicas run jobs
    min_replicas = 1
    max_replicas = var.worker_max_replicas

    container {
      name   = "worker"
      image  = "${local.registry}/fq-worker:${var.worker_image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "ConnectionStrings__Default"
        secret_name = "db-connection"
      }
      dynamic "env" {
        for_each = local.common_env
        content {
          name  = env.key
          value = env.value
        }
      }
    }
  }

  # the web app runs the migrations on startup; the worker comes up after the database exists
  depends_on = [azurerm_role_assignment.acr_pull, azurerm_container_app.web]

  tags = local.tags
}

# Private mail relay (deploy/postfix): reachable inside the environment only, on TCP 25. Azure blocks outbound port 25,
# so it needs a smarthost (smtp_relay_host, port 587) to deliver anything. The queue is not persisted across restarts.
resource "azurerm_container_app" "postfix" {
  name                         = "ca-fq-postfix-${local.suffix}"
  resource_group_name          = data.azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.apps.id]
  }

  registry {
    server   = data.azurerm_container_registry.shared.login_server
    identity = azurerm_user_assigned_identity.apps.id
  }

  secret {
    name  = "relay-password"
    value = var.smtp_relay_password == "" ? "unset" : var.smtp_relay_password
  }

  ingress {
    external_enabled = false
    target_port      = 25
    exposed_port     = 25
    transport        = "tcp"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "postfix"
      image  = "${local.registry}/fq-postfix:${var.postfix_image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "POSTFIX_HOSTNAME"
        value = var.mail_hostname
      }
      env {
        name  = "POSTFIX_MYNETWORKS"
        value = var.postfix_networks
      }
      env {
        name  = "POSTFIX_RELAY_HOST"
        value = var.smtp_relay_host
      }
      env {
        name  = "POSTFIX_RELAY_USER"
        value = var.smtp_relay_user
      }
      env {
        name        = "POSTFIX_RELAY_PASSWORD"
        secret_name = "relay-password"
      }
    }
  }

  depends_on = [azurerm_role_assignment.acr_pull]

  tags = local.tags
}

# ---- DNS: the records Container Apps needs to bind the custom domain ----

resource "azurerm_dns_cname_record" "web" {
  name                = local.record_name
  zone_name           = data.azurerm_dns_zone.fq.name
  resource_group_name = data.azurerm_dns_zone.fq.resource_group_name
  ttl                 = 300
  record              = azurerm_container_app.web.ingress[0].fqdn

  tags = local.tags
}

resource "azurerm_dns_txt_record" "web_verification" {
  name                = "asuid.${local.record_name}"
  zone_name           = data.azurerm_dns_zone.fq.name
  resource_group_name = data.azurerm_dns_zone.fq.resource_group_name
  ttl                 = 300

  record {
    value = azurerm_container_app_environment.env.custom_domain_verification_id
  }

  tags = local.tags
}

# ---- cost guard ----

resource "azurerm_consumption_budget_resource_group" "budget" {
  name              = "budget-fq-${local.suffix}"
  resource_group_id = data.azurerm_resource_group.this.id
  amount            = var.budget_amount
  time_grain        = "Monthly"

  time_period {
    start_date = var.budget_start_date
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThan"
    threshold_type = "Actual"
    contact_emails = [var.budget_email]
  }

  notification {
    enabled        = true
    threshold      = 100
    operator       = "GreaterThan"
    threshold_type = "Forecasted"
    contact_emails = [var.budget_email]
  }
}

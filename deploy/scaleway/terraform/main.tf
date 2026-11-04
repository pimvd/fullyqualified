locals {
  name = "fq-${var.environment}"

  # The serverless database endpoint looks like postgres://host:port/database?sslmode=require.
  endpoint = regex("^(?:postgres://)?(?P<host>[^:/]+)(?::(?P<port>[0-9]+))?/(?P<db>[^?]+)", scaleway_sdb_sql_database.main.endpoint)
  connection_string = join(";", [
    "Host=${local.endpoint.host}",
    "Port=${coalesce(local.endpoint.port, "5432")}",
    "Database=${local.endpoint.db}",
    "Username=${scaleway_iam_application.app.id}",
    "Password=${scaleway_iam_api_key.app.secret_key}",
    "SSL Mode=Require",
  ])

  image = "${scaleway_registry_namespace.main.endpoint}/${var.image_name}:${var.image_tag}"

  # Secret values must not be empty, so optional ones are only added when set.
  secrets = merge(
    {
      ConnectionStrings__Default = local.connection_string
      Auth__SetupCode            = var.setup_code
    },
    var.smtp_user == "" ? {} : { Alerts__Email__User = var.smtp_user },
    var.smtp_password == "" ? {} : { Alerts__Email__Password = var.smtp_password },
  )

  # The image already listens on 8080. Behind Scaleway's TLS-terminating proxy the app must trust X-Forwarded-Proto, or it sees http
  # and passkeys and secure cookies break. Private addresses stay blocked because this is a shared, internet-facing install.
  environment = merge(
    {
      ASPNETCORE_FORWARDEDHEADERS_ENABLED = "true"
      Scanning__Enabled                   = tostring(var.scanning_enabled)
      Scanning__AllowPrivateAddresses     = "false"
    },
    var.smtp_host == "" ? {} : { Alerts__Email__Host = var.smtp_host, Alerts__Email__Port = "587", Alerts__Email__Ssl = "true" },
    var.mail_from == "" ? {} : { Alerts__Email__From = var.mail_from },
    var.public_origin == "" ? {} : { Auth__Origin = var.public_origin, Auth__RpId = replace(var.public_origin, "https://", "") },
  )
}

# The database, and an IAM application with a key that may only read and write serverless databases in this project.
resource "scaleway_sdb_sql_database" "main" {
  name    = local.name
  min_cpu = var.db_min_cpu
  max_cpu = var.db_max_cpu
}

resource "scaleway_iam_application" "app" {
  name        = "${local.name}-app"
  description = "FullyQualified ${var.environment}: database access for the container"
}

resource "scaleway_iam_policy" "app" {
  name           = "${local.name}-app-database"
  description    = "Read and write serverless databases in the project, nothing else"
  application_id = scaleway_iam_application.app.id

  rule {
    project_ids          = [var.project_id]
    permission_set_names = ["ServerlessSQLDatabaseReadWrite"]
  }
}

resource "scaleway_iam_api_key" "app" {
  application_id = scaleway_iam_application.app.id
  description    = "FullyQualified ${var.environment}: database password"
}

# The private registry the pipeline pushes the image to. Destroying it removes its images too.
resource "scaleway_registry_namespace" "main" {
  name        = local.name
  description = "FullyQualified ${var.environment} images"
  is_public   = false
}

resource "scaleway_container_namespace" "main" {
  name        = local.name
  description = "FullyQualified ${var.environment}"
}

resource "scaleway_container" "main" {
  name         = "fullyqualified"
  namespace_id = scaleway_container_namespace.main.id
  image        = local.image

  port                   = 8080
  privacy                = "public"
  https_connections_only = true
  protocol               = "http1"

  cpu_limit          = var.cpu_limit
  memory_limit_bytes = var.memory_limit_mb * 1024 * 1024
  min_scale          = var.min_scale
  # One instance only: the scheduler election uses a Postgres advisory lock, which a serverless database does not guarantee.
  max_scale = 1
  timeout   = 300

  environment_variables        = local.environment
  secret_environment_variables = local.secrets

  # The app migrates the database before it listens, so the first start can be slow.
  startup_probe {
    http {
      path = "/health"
    }
    failure_threshold = 24
    interval          = "5s"
    timeout           = "5s"
  }

  liveness_probe {
    http {
      path = "/health"
    }
    failure_threshold = 3
    interval          = "30s"
    timeout           = "5s"
  }
}

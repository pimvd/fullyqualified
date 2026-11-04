output "url" {
  description = "Public address of the app."
  value       = scaleway_container.main.public_endpoint
}

output "registry_endpoint" {
  description = "Registry path images are pushed to, for example rg.fr-par.scw.cloud/fq-dev."
  value       = scaleway_registry_namespace.main.endpoint
}

output "registry_host" {
  description = "Registry host for docker login."
  value       = split("/", scaleway_registry_namespace.main.endpoint)[0]
}

output "database_endpoint" {
  description = "Database endpoint without credentials."
  value       = scaleway_sdb_sql_database.main.endpoint
}

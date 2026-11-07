output "web_app_name" {
  value = azurerm_container_app.web.name
}

output "web_fqdn" {
  value = azurerm_container_app.web.ingress[0].fqdn
}

output "environment_name" {
  value = azurerm_container_app_environment.env.name
}

output "domain_verification_id" {
  value = azurerm_container_app_environment.env.custom_domain_verification_id
}

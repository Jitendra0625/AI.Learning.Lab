output "language_endpoint" {
  description = "Endpoint for Azure AI Language - used in RedactComplaint"
  value       = azurerm_cognitive_account.language.endpoint
}

output "language_primary_key" {
  description = "Primary key for Azure AI Language"
  value       = azurerm_cognitive_account.language.primary_access_key
  sensitive   = true
}

output "content_safety_endpoint" {
  description = "Endpoint for Azure AI Content Safety - used in GenerateLetter"
  value       = azurerm_cognitive_account.content_safety.endpoint
}

output "content_safety_primary_key" {
  description = "Primary key for Azure AI Content Safety"
  value       = azurerm_cognitive_account.content_safety.primary_access_key
  sensitive   = true
}

output "cosmosdb_endpoint" {
  description = "Endpoint URI for Cosmos DB - used in SaveComplaint"
  value       = azurerm_cosmosdb_account.main.endpoint
}

output "cosmosdb_primary_key" {
  description = "Primary key for Cosmos DB"
  value       = azurerm_cosmosdb_account.main.primary_key
  sensitive   = true
}

output "cosmosdb_database_name" {
  description = "Cosmos DB database name"
  value       = azurerm_cosmosdb_sql_database.main.name
}

output "cosmosdb_container_name" {
  description = "Cosmos DB container name"
  value       = azurerm_cosmosdb_sql_container.complaints.name
}
output "openai_endpoint" {
  description = "Endpoint for Azure OpenAI - used in GenerateLetter"
  value       = azurerm_cognitive_account.openai.endpoint
}

output "openai_primary_key" {
  description = "Primary key for Azure OpenAI"
  value       = azurerm_cognitive_account.openai.primary_access_key
  sensitive   = true
}

output "openai_deployment_name" {
  description = "Deployment name to reference in API calls"
  value       = azurerm_cognitive_deployment.gpt5_mini.name
}
output "app_insights_connection_string" {
  description = "Connection string for Application Insights, used by Program.cs's OpenTelemetry exporter"
  value       = azurerm_application_insights.main.connection_string
  sensitive   = true
}

output "log_analytics_workspace_id" {
  description = "Resource ID of the Log Analytics workspace backing Application Insights"
  value       = azurerm_log_analytics_workspace.main.id
}

output "agent_service_url" {
  description = "URL of the deployed Stage 1 agent service (App Service)"
  value       = "https://${azurerm_linux_web_app.agent_service.default_hostname}"
}

output "agent_service_name" {
  description = "Name of the App Service, used for az webapp deployment commands"
  value       = azurerm_linux_web_app.agent_service.name
}
variable "location" {
  description = "Azure region for all resources"
  type        = string
  default     = "uksouth"
}

variable "resource_group_name" {
  description = "Name of the resource group"
  type        = string
  default     = "rg-complainthandler"
}

variable "language_service_name" {
  description = "Name of the Azure AI Language resource (must be globally unique)"
  type        = string
  default     = "lang-complainthandler"
}

variable "content_safety_name" {
  description = "Name of the Azure AI Content Safety resource (must be globally unique)"
  type        = string
  default     = "safety-complainthandler"
}

variable "cosmosdb_account_name" {
  description = "Name of the Cosmos DB account (must be globally unique, lowercase, no underscores)"
  type        = string
  default     = "cosmos-complainthandler"
}

variable "cosmosdb_database_name" {
  description = "Name of the Cosmos DB SQL database"
  type        = string
  default     = "complainthandlerdb"
}

variable "cosmosdb_container_name" {
  description = "Name of the Cosmos DB container"
  type        = string
  default     = "complaints"
}

variable "openai_service_name" {
  description = "Name of the Azure OpenAI resource (must be globally unique)"
  type        = string
  default     = "openai-complainthandler"
}

variable "openai_deployment_name" {
  description = "Name of the model deployment (used in API calls as the 'deployment id')"
  type        = string
  default     = "gpt-5-mini-deployment"
}
variable "log_analytics_retention_days" {
  description = "Retention period in days for the Log Analytics workspace backing Application Insights"
  type        = number
  default     = 30
}

variable "log_analytics_sku" {
  description = "SKU for the Log Analytics workspace"
  type        = string
  default     = "PerGB2018"
}

variable "agent_app_service_plan_name" {
  description = "Name of the App Service Plan hosting the Stage 1 agent service"
  type        = string
  default     = "asp-complainthandler-agent"
}

variable "agent_app_service_name" {
  description = "Name of the App Service hosting ComplaintHandler.AgentService (must be globally unique)"
  type        = string
  default     = "app-complainthandler-agent"
}

variable "agent_service_app_client_id" {
  type        = string
  description = "Client ID of the App Registration Easy Auth created for the Agent Service"
}

variable "tenant_id" {
  type        = string
  description = "Entra ID tenant ID"
}
variable "functions_storage_account_name" {
  type    = string
  default = "stcomplainthandlerfn"
}

variable "functions_service_plan_name" {
  type    = string
  default = "plan-complainthandler-functions"
}

variable "functions_app_name" {
  type    = string
  default = "func-complainthandler"
}
variable "agent_service_client_secret" {
  type      = string
  sensitive = true
}
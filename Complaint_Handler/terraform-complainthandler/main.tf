terraform {
  required_version = ">= 1.5.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.100"
    }
  }
}

provider "azurerm" {
  features {}
}

# -----------------------------------------------------------------------------
# Resource Group - no cost on its own
# -----------------------------------------------------------------------------
resource "azurerm_resource_group" "main" {
  name     = var.resource_group_name
  location = var.location
}

# -----------------------------------------------------------------------------
# Azure AI Language - F0 (free) tier
# 5,000 text records/month, 5 calls/sec - used by RedactComplaint
# -----------------------------------------------------------------------------
resource "azurerm_cognitive_account" "language" {
  name                = var.language_service_name
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  kind                = "TextAnalytics"
  sku_name            = "F0"

  custom_subdomain_name = var.language_service_name
}

# -----------------------------------------------------------------------------
# Azure AI Content Safety - F0 (free) tier
# used by GenerateLetter (pre + post checks)
# -----------------------------------------------------------------------------
resource "azurerm_cognitive_account" "content_safety" {
  name                = var.content_safety_name
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  kind                = "ContentSafety"
  sku_name            = "F0"

  custom_subdomain_name = var.content_safety_name
}

# -----------------------------------------------------------------------------
# Cosmos DB - free tier (1000 RU/s + 25GB free forever, one per subscription)
# used by SaveComplaint
# -----------------------------------------------------------------------------
resource "azurerm_cosmosdb_account" "main" {
  name                = var.cosmosdb_account_name
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  offer_type          = "Standard"
  kind                = "GlobalDocumentDB"

  enable_free_tier = true

  consistency_policy {
    consistency_level = "Session"
  }

  geo_location {
    location          = azurerm_resource_group.main.location
    failover_priority = 0
  }
}

resource "azurerm_cosmosdb_sql_database" "main" {
  name                = var.cosmosdb_database_name
  resource_group_name = azurerm_resource_group.main.name
  account_name        = azurerm_cosmosdb_account.main.name
}

resource "azurerm_cosmosdb_sql_container" "complaints" {
  name                = var.cosmosdb_container_name
  resource_group_name = azurerm_resource_group.main.name
  account_name        = azurerm_cosmosdb_account.main.name
  database_name       = azurerm_cosmosdb_sql_database.main.name
  partition_key_paths = ["/id"]

  # Stay within the free tier's 1000 RU/s shared throughput
  throughput = 400
}

# -----------------------------------------------------------------------------
# Azure OpenAI - S0 tier (no free tier for OpenAI; pay-as-you-go per token)
# used by GenerateLetter's real LLM call
# -----------------------------------------------------------------------------
resource "azurerm_cognitive_account" "openai" {
  name                = var.openai_service_name
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  kind                = "OpenAI"
  sku_name            = "S0"

  custom_subdomain_name = var.openai_service_name
}

resource "azurerm_cognitive_deployment" "gpt5_mini" {
  name                 = var.openai_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai.id

  model {
    format  = "OpenAI"
    name    = "gpt-5-mini"
    version = "2025-08-07"
  }

  scale {
    type     = "GlobalStandard"
    capacity = 10
  }
}

# Application Insight
resource "azurerm_log_analytics_workspace" "main" {
  name                = "law-complainthandler"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = var.log_analytics_sku
  retention_in_days   = var.log_analytics_retention_days
}

resource "azurerm_application_insights" "main" {
  name                = "appi-complainthandler"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  workspace_id        = azurerm_log_analytics_workspace.main.id
  application_type    = "web"
}

# -----------------------------------------------------------------------------
# App Service Plan - F1 (Free) tier
# Hosts ComplaintHandler.AgentService (Stage 1: self-managed App Service,
# vs. Stage 2's Foundry Hosted Agent)
# -----------------------------------------------------------------------------
resource "azurerm_service_plan" "agent_service" {
  name                = var.agent_app_service_plan_name
  location            = "westeurope"
  resource_group_name = azurerm_resource_group.main.name
  os_type             = "Linux"
  sku_name            = "B1"
}

resource "azurerm_storage_account" "functions" {
  name                     = var.functions_storage_account_name  # globally unique, lowercase, no hyphens, 3-24 chars
  resource_group_name      = azurerm_resource_group.main.name
  location                 = "westeurope"
  account_tier             = "Standard"
  account_replication_type = "LRS"
}
resource "azurerm_service_plan" "functions" {
  name                = var.functions_service_plan_name
  resource_group_name = azurerm_resource_group.main.name
  location            = "westeurope"
  os_type             = "Linux"
  sku_name            = "Y1"
}

resource "azurerm_linux_function_app" "main" {
  name                = var.functions_app_name
  resource_group_name = azurerm_resource_group.main.name
  location            = "westeurope"
  service_plan_id     = azurerm_service_plan.functions.id

  storage_account_name       = azurerm_storage_account.functions.name
  storage_account_access_key = azurerm_storage_account.functions.primary_access_key

  identity {
    type = "SystemAssigned"
  }

  site_config {}

  lifecycle {
    ignore_changes = [
      app_settings["WEBSITE_RUN_FROM_PACKAGE"],
      app_settings["WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED"],
      app_settings["WEBSITE_MOUNT_ENABLED"],
      tags,
      site_config,
    ]
  }

  app_settings = {
    "FUNCTIONS_WORKER_RUNTIME"              = "dotnet-isolated"
    "Language__Endpoint"                    = azurerm_cognitive_account.language.endpoint
    "Language__Key"                         = azurerm_cognitive_account.language.primary_access_key
    "ContentSafety__Endpoint"               = azurerm_cognitive_account.content_safety.endpoint
    "ContentSafety__Key"                    = azurerm_cognitive_account.content_safety.primary_access_key
    "Cosmos__Endpoint"                      = azurerm_cosmosdb_account.main.endpoint
    "Cosmos__Key"                           = azurerm_cosmosdb_account.main.primary_key
    "AzureOpenAI__Endpoint"                 = azurerm_cognitive_account.openai.endpoint
    "AzureOpenAI__Key"                      = azurerm_cognitive_account.openai.primary_access_key
    "AzureOpenAI__DeploymentName"           = azurerm_cognitive_deployment.gpt5_mini.name
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.main.connection_string
    "AgentService__Endpoint"                = "https://${azurerm_linux_web_app.agent_service.default_hostname}"
    "AgentService__Scope"                   = "api://${var.agent_service_app_client_id}/.default"
  }
}
resource "azurerm_linux_web_app" "agent_service" {
  name                = var.agent_app_service_name
  location            = "westeurope"
  resource_group_name = azurerm_resource_group.main.name
  service_plan_id     = azurerm_service_plan.agent_service.id

  site_config {
    always_on = true
  }

  lifecycle {
    ignore_changes = [
      tags,
      site_config[0].app_command_line,
    ]
  }

  app_settings = {
    "AzureOpenAI__Endpoint"                    = azurerm_cognitive_account.openai.endpoint
    "AzureOpenAI__Key"                         = azurerm_cognitive_account.openai.primary_access_key
    "AzureOpenAI__DeploymentName"              = azurerm_cognitive_deployment.gpt5_mini.name
    "APPLICATIONINSIGHTS_CONNECTION_STRING"    = azurerm_application_insights.main.connection_string
    "MICROSOFT_PROVIDER_AUTHENTICATION_SECRET" = var.agent_service_client_secret
  }

  identity {
    type = "SystemAssigned"
  }

  auth_settings_v2 {
    auth_enabled           = true
    unauthenticated_action = "RedirectToLoginPage"
    default_provider       = "azureactivedirectory"

    active_directory_v2 {
      client_id                  = var.agent_service_app_client_id
      tenant_auth_endpoint       = "https://login.microsoftonline.com/${var.tenant_id}/v2.0"
      client_secret_setting_name = "MICROSOFT_PROVIDER_AUTHENTICATION_SECRET"
    }

    login {
      token_store_enabled = true
    }
  }
}

resource "azurerm_api_management" "main" {
  name                = "apim-complainthandler"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  publisher_name      = "Complaint Handler"
  publisher_email     = "jmg0625@gmail.com"   # replace with your real email
  sku_name            = "Consumption_0"
}

resource "azurerm_api_management_api" "complaint_handler" {
  name                = "complaint-handler-api"
  resource_group_name = azurerm_resource_group.main.name
  api_management_name = azurerm_api_management.main.name
  revision            = "1"
  display_name        = "Complaint Handler API"
  path                = "complaints"
  protocols           = ["https"]

  # Backend service is the Function App
  service_url = "https://func-complainthandler.azurewebsites.net/api"
}

resource "azurerm_api_management_api_operation" "redact" {
  operation_id        = "redact-complaint"
  api_name            = azurerm_api_management_api.complaint_handler.name
  api_management_name = azurerm_api_management.main.name
  resource_group_name = azurerm_resource_group.main.name
  display_name        = "Redact Complaint"
  method              = "POST"
  url_template        = "/RedactComplaint"
}

resource "azurerm_api_management_api_operation" "generate" {
  operation_id        = "generate-letter"
  api_name            = azurerm_api_management_api.complaint_handler.name
  api_management_name = azurerm_api_management.main.name
  resource_group_name = azurerm_resource_group.main.name
  display_name        = "Generate Letter"
  method              = "POST"
  url_template        = "/GenerateLetter"
}

resource "azurerm_api_management_api_operation" "save" {
  operation_id        = "save-complaint"
  api_name            = azurerm_api_management_api.complaint_handler.name
  api_management_name = azurerm_api_management.main.name
  resource_group_name = azurerm_resource_group.main.name
  display_name        = "Save Complaint"
  method              = "POST"
  url_template        = "/SaveComplaint"
}

# Basic rate-limit policy applied at the API level (all operations)
resource "azurerm_api_management_api_policy" "complaint_handler" {
  api_name            = azurerm_api_management_api.complaint_handler.name
  api_management_name = azurerm_api_management.main.name
  resource_group_name = azurerm_resource_group.main.name

  xml_content = <<XML
<policies>
  <inbound>
    <base />
    <rate-limit calls="20" renewal-period="60" />
  </inbound>
  <backend>
    <base />
  </backend>
  <outbound>
    <base />
  </outbound>
  <on-error>
    <base />
  </on-error>
</policies>
XML
}
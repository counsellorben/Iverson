terraform {
  required_version = ">= 1.7"
  required_providers {
    azurerm = { source = "hashicorp/azurerm", version = "~> 3.90" }
  }
}

variable "location" {
  type    = string
  default = "eastus"
}

variable "resource_group_name" {
  type    = string
  default = "iverson-terraform-state-rg"
}

variable "storage_account_name" {
  type    = string
  default = "iversontfstate"
}

variable "container_name" {
  type    = string
  default = "tfstate"
}

# No default: the public addresses `terraform` runs from. The state account denies every
# other network (CSR round-10 #6).
variable "state_authorized_ip_ranges" {
  type = list(string)
  validation {
    condition     = length(var.state_authorized_ip_ranges) > 0
    error_message = "state_authorized_ip_ranges must contain at least one address."
  }
}

provider "azurerm" {
  features {}
  # Containers are created through the data plane, which needs Entra auth once shared keys are off.
  storage_use_azuread = true
}

data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "state" {
  name     = var.resource_group_name
  location = var.location
}

resource "azurerm_storage_account" "state" {
  name                            = var.storage_account_name
  resource_group_name             = azurerm_resource_group.state.name
  location                        = azurerm_resource_group.state.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false

  network_rules {
    default_action = "Deny"
    ip_rules       = var.state_authorized_ip_ranges
    bypass         = ["AzureServices"]
  }
}

# The only data-plane grant on the state account: blob read/write for the deploying identity.
# Scoped to the account because a container-scoped assignment cannot exist before the container,
# and the container cannot be created without it.
resource "azurerm_role_assignment" "deployer_state_data" {
  scope                = azurerm_storage_account.state.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_storage_container" "state" {
  name                  = var.container_name
  storage_account_name  = azurerm_storage_account.state.name
  container_access_type = "private"
  depends_on            = [azurerm_role_assignment.deployer_state_data]
}

output "resource_group_name" { value = azurerm_resource_group.state.name }
output "storage_account_name" { value = azurerm_storage_account.state.name }
output "container_name" { value = azurerm_storage_container.state.name }

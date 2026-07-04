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

provider "azurerm" {
  features {}
}

resource "azurerm_resource_group" "state" {
  name     = var.resource_group_name
  location = var.location
}

resource "azurerm_storage_account" "state" {
  name                     = var.storage_account_name
  resource_group_name      = azurerm_resource_group.state.name
  location                 = azurerm_resource_group.state.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"
}

resource "azurerm_storage_container" "state" {
  name                  = var.container_name
  storage_account_name  = azurerm_storage_account.state.name
  container_access_type = "private"
}

output "resource_group_name" { value = azurerm_resource_group.state.name }
output "storage_account_name" { value = azurerm_storage_account.state.name }
output "container_name" { value = azurerm_storage_container.state.name }

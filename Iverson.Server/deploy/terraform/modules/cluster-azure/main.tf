terraform {
  required_providers {
    azurerm = { source = "hashicorp/azurerm", version = "~> 3.90" }
  }
}

resource "azurerm_resource_group" "this" {
  name     = "${var.cluster_name}-rg"
  location = var.location
}

data "azurerm_client_config" "current" {}

# No network_acls block (accepted follow-up — restricting vault network access
# is its own change, not requested by this task, and the AWS CMK path this
# mirrors doesn't scope network access on its key either).
#tfsec:ignore:azure-keyvault-specify-network-acl
resource "azurerm_key_vault" "data_volumes" {
  name                       = "${var.cluster_name}-dv-kv"
  location                   = azurerm_resource_group.this.location
  resource_group_name        = azurerm_resource_group.this.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  purge_protection_enabled   = true
  soft_delete_retention_days = 30
}

# No expiration_date (accepted follow-up — key rotation policy is its own
# change, not requested by this task; the disk encryption set references this
# key's id directly, so an expiring key would need a rotation/rewrap plan that
# is out of scope here).
#tfsec:ignore:azure-keyvault-ensure-key-expiry
resource "azurerm_key_vault_key" "data_volumes" {
  name         = "data-volumes"
  key_vault_id = azurerm_key_vault.data_volumes.id
  key_type     = "RSA"
  key_size     = 2048
  key_opts     = ["decrypt", "encrypt", "sign", "unwrapKey", "verify", "wrapKey"]

  depends_on = [azurerm_key_vault_access_policy.terraform]
}

# Grants the deploying principal key-management permissions on the vault;
# without this, Step 2's key creation is denied.
resource "azurerm_key_vault_access_policy" "terraform" {
  key_vault_id = azurerm_key_vault.data_volumes.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = data.azurerm_client_config.current.object_id

  key_permissions = ["Create", "Delete", "Get", "List", "Purge", "Recover", "Update", "GetRotationPolicy"]
}

resource "azurerm_disk_encryption_set" "data_volumes" {
  name                = "${var.cluster_name}-des"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  key_vault_key_id    = azurerm_key_vault_key.data_volumes.id

  identity {
    type = "SystemAssigned"
  }
}

# Grants the disk encryption set's own managed identity wrap/unwrap access;
# without this, disk I/O fails within the hour once the data key needs
# to be unwrapped.
resource "azurerm_key_vault_access_policy" "des" {
  key_vault_id = azurerm_key_vault.data_volumes.id
  tenant_id    = azurerm_disk_encryption_set.data_volumes.identity[0].tenant_id
  object_id    = azurerm_disk_encryption_set.data_volumes.identity[0].principal_id

  key_permissions = ["Get", "WrapKey", "UnwrapKey"]
}

resource "azurerm_virtual_network" "this" {
  name                = "${var.cluster_name}-vnet"
  address_space       = ["10.1.0.0/16"]
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
}

resource "azurerm_subnet" "aks" {
  name                 = "${var.cluster_name}-aks-subnet"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = ["10.1.0.0/20"]
}

resource "azurerm_log_analytics_workspace" "this" {
  name                = "${var.cluster_name}-logs"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  sku                 = "PerGB2018"
  retention_in_days   = 90
}

# api_server_access_profile below sets authorized_ip_ranges = var.api_authorized_ip_ranges,
# a required variable with no default specifically to force an explicit choice;
# tfsec can't resolve variable values statically, so it can't tell the allow-list
# is actually populated at apply time.
#tfsec:ignore:azure-container-limit-authorized-ips
resource "azurerm_kubernetes_cluster" "this" {
  name                = var.cluster_name
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  dns_prefix          = var.cluster_name
  kubernetes_version  = var.kubernetes_version
  sku_tier            = "Standard"

  role_based_access_control_enabled = true

  default_node_pool {
    name           = "general"
    vm_size        = var.general_vm_size
    vnet_subnet_id = azurerm_subnet.aks.id
    # enable_auto_scaling is the azurerm ~> 3.90 (v3.x) attribute name; a future
    # bump to azurerm ~> 4.x must rename this back to auto_scaling_enabled.
    enable_auto_scaling = true
    min_count           = var.general_min_count
    max_count           = var.general_max_count
  }

  identity {
    type = "SystemAssigned"
  }

  # network_policy = "azure" is what makes the companion Helm chart plan's
  # NetworkPolicy objects actually get enforced — without a network_profile
  # block at all (the original plan's state), AKS defaults to kubenet with
  # no policy engine and every NetworkPolicy silently does nothing.
  network_profile {
    network_plugin = "azure"
    network_policy = "azure"
  }

  # Restricts which networks can reach the API server, same rationale as
  # EKS's public_access_cidrs — no default, see api_authorized_ip_ranges.
  api_server_access_profile {
    authorized_ip_ranges = var.api_authorized_ip_ranges
  }

  ingress_application_gateway {
    subnet_cidr = "10.1.16.0/24"
  }

  oms_agent {
    log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id
  }
}

locals {
  extra_pools = {
    postgres    = { vm_size = var.postgres_vm_size, count = var.postgres_node_count, label = "postgres" }
    starrocksfe = { vm_size = var.starrocks_fe_vm_size, count = 1, label = "starrocks-fe" }
    starrocksbe = { vm_size = var.starrocks_be_vm_size, count = var.starrocks_be_node_count, label = "starrocks-be" }
    qdrant      = { vm_size = var.qdrant_vm_size, count = var.qdrant_node_count, label = "qdrant" }
    kafka       = { vm_size = var.kafka_vm_size, count = var.kafka_node_count, label = "kafka" }
    ollama      = { vm_size = var.ollama_vm_size, count = var.ollama_node_count, label = "ollama" }
  }
}

resource "azurerm_kubernetes_cluster_node_pool" "pools" {
  for_each              = local.extra_pools
  name                  = each.key
  kubernetes_cluster_id = azurerm_kubernetes_cluster.this.id
  vm_size               = each.value.vm_size
  node_count            = each.value.count
  vnet_subnet_id        = azurerm_subnet.aks.id

  node_labels = {
    "iverson.io/node-pool" = each.value.label
  }

  node_taints = [
    "iverson.io/node-pool=${each.value.label}:NoSchedule"
  ]
}

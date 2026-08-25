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

resource "azurerm_key_vault" "data_volumes" {
  name                       = "${var.cluster_name}-dv-kv"
  location                   = azurerm_resource_group.this.location
  resource_group_name        = azurerm_resource_group.this.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  purge_protection_enabled   = true
  soft_delete_retention_days = 30

  # Deny by default. The disk encryption set still reaches the vault: Azure
  # lists "Azure Disk Storage — when configured with a Disk Encryption Set" in
  # the trusted-services table that `bypass = "AzureServices"` admits, and the
  # DES additionally holds the access policy below, which the bypass does not
  # replace.
  #
  # ip_rules is required and has no default, because Key Vault firewall rules
  # apply to the DATA plane — and `azurerm_key_vault_key` is a data-plane call.
  # Whoever runs `terraform apply` must have their egress address in this list
  # or key creation is refused. Same forced-explicit-choice rationale as
  # api_authorized_ip_ranges.
  network_acls {
    default_action = "Deny"
    bypass         = "AzureServices"
    ip_rules       = var.key_vault_authorized_ip_ranges
  }
}

# No expiration_date (accepted follow-up — key expiry is a separate concern
# from rotation; rotation_policy below rotates key material on a schedule
# without ever expiring the key itself).
#tfsec:ignore:azure-keyvault-ensure-key-expiry
resource "azurerm_key_vault_key" "data_volumes" {
  name         = "data-volumes"
  key_vault_id = azurerm_key_vault.data_volumes.id
  key_type     = "RSA"
  key_size     = 2048
  key_opts     = ["decrypt", "encrypt", "sign", "unwrapKey", "verify", "wrapKey"]

  # 90-day cadence to match GCP's rotation_period (google_kms_crypto_key.data_volumes
  # in modules/cluster-gcp/main.tf) — both clouds require an explicit, Terraform-set
  # interval, unlike AWS where enable_key_rotation = true delegates to KMS's opaque
  # annual default. time_after_creation (rather than time_before_expiry) is used
  # because this key intentionally has no expiration_date.
  rotation_policy {
    automatic {
      time_after_creation = "P90D"
    }
  }

  depends_on = [azurerm_key_vault_access_policy.terraform]
}

# Grants the deploying principal key-management permissions on the vault;
# without this, Step 2's key creation is denied. SetRotationPolicy is required
# for Terraform to apply the rotation_policy block above (GetRotationPolicy
# alone only allows reading it back).
resource "azurerm_key_vault_access_policy" "terraform" {
  key_vault_id = azurerm_key_vault.data_volumes.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = data.azurerm_client_config.current.object_id

  key_permissions = ["Create", "Delete", "Get", "List", "Purge", "Recover", "Update", "GetRotationPolicy", "SetRotationPolicy"]
}

resource "azurerm_disk_encryption_set" "data_volumes" {
  name                = "${var.cluster_name}-des"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  # versionless_id (not .id, which pins the current key version) is required
  # for auto_key_rotation_enabled below — the DES resolves the current key
  # version at unwrap time instead of staying pinned to the version that
  # existed when this resource was created.
  key_vault_key_id = azurerm_key_vault_key.data_volumes.versionless_id
  # Keeps the DES following the key as azurerm_key_vault_key.data_volumes's
  # rotation_policy above creates new versions.
  auto_key_rotation_enabled = true

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

# Grants the AKS cluster's own (control-plane) system-assigned identity Reader
# access to the disk encryption set. Without this, disk.csi.azure.com cannot
# create a managed disk referencing this DES and every Azure PVC stays
# Pending. Per Microsoft's AKS BYOK documentation
# (https://learn.microsoft.com/en-us/azure/aks/azure-disk-customer-managed-keys,
# section "Encrypt your AKS cluster data disk"): "The AKS cluster identity
# needs Reader access to the DiskEncryptionSet, otherwise you get an error
# suggesting that the managed identity doesn't have permissions", resolved via
# `az aks show --query "identity.principalId"` — i.e. the cluster's own
# system-assigned identity (azurerm_kubernetes_cluster.this.identity[0], not
# the node-resource-group Contributor identity granted elsewhere), granted the
# built-in "Reader" role scoped to the DES.
resource "azurerm_role_assignment" "aks_data_volumes_des" {
  scope                = azurerm_disk_encryption_set.data_volumes.id
  role_definition_name = "Reader"
  principal_id         = azurerm_kubernetes_cluster.this.identity[0].principal_id
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

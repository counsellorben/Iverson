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

# Grants the AKS cluster's control-plane identity (user-assigned, see below) Reader
# access to the disk encryption set. Without this, disk.csi.azure.com cannot
# create a managed disk referencing this DES and every Azure PVC stays
# Pending. Per Microsoft's AKS BYOK documentation
# (https://learn.microsoft.com/en-us/azure/aks/azure-disk-customer-managed-keys,
# section "Encrypt your AKS cluster data disk"): "The AKS cluster identity
# needs Reader access to the DiskEncryptionSet, otherwise you get an error
# suggesting that the managed identity doesn't have permissions". Here that
# cluster identity is the control plane's user-assigned identity
# (azurerm_user_assigned_identity.control_plane, not the node-resource-group
# Contributor identity granted elsewhere); its principal is granted the
# built-in "Reader" role scoped to the DES.
resource "azurerm_role_assignment" "aks_data_volumes_des" {
  scope                = azurerm_disk_encryption_set.data_volumes.id
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.control_plane.principal_id
}

# The control plane's identity is user-assigned so it can hold Network Contributor on both
# subnets before the cluster is created: API Server VNet Integration needs those rights at
# provisioning time, which a system-assigned identity (created with the cluster) cannot have.
resource "azurerm_user_assigned_identity" "control_plane" {
  name                = "${var.cluster_name}-control-plane"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
}

resource "azurerm_role_assignment" "control_plane_aks_subnet" {
  scope                = azurerm_subnet.aks.id
  role_definition_name = "Network Contributor"
  principal_id         = azurerm_user_assigned_identity.control_plane.principal_id
}

resource "azurerm_role_assignment" "control_plane_apiserver_subnet" {
  scope                = azurerm_subnet.apiserver.id
  role_definition_name = "Network Contributor"
  principal_id         = azurerm_user_assigned_identity.control_plane.principal_id
}

# With local accounts disabled, Terraform itself authenticates through Entra (kubelogin) and
# needs a Kubernetes data-plane role. A new assignment can take up to five minutes to apply;
# docs/runbooks/csr10-infrastructure-cutover.md says to re-run a first apply that fails on it.
resource "azurerm_role_assignment" "deployer_cluster_admin" {
  scope                = azurerm_kubernetes_cluster.this.id
  role_definition_name = "Azure Kubernetes Service RBAC Cluster Admin"
  principal_id         = data.azurerm_client_config.current.object_id
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

# API Server VNet Integration (CSR round-10 #17): projects the API server into this delegated
# subnet, so networkPolicy.apiServerCidrs can name a fixed range instead of a public IP that may
# change. One-way, and enabling it changes the API server's IP (the hostname stays).
resource "azurerm_subnet" "apiserver" {
  name                 = "${var.cluster_name}-apiserver-subnet"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = ["10.1.17.0/28"]

  delegation {
    name = "aks-apiserver"
    service_delegation {
      name    = "Microsoft.ContainerService/managedClusters"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
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
  name                   = var.cluster_name
  location               = azurerm_resource_group.this.location
  resource_group_name    = azurerm_resource_group.this.name
  dns_prefix             = var.cluster_name
  kubernetes_version     = var.kubernetes_version
  sku_tier               = "Standard"
  disk_encryption_set_id = azurerm_disk_encryption_set.data_volumes.id

  role_based_access_control_enabled = true

  # CSR round-10 #6: no local accounts, so no static cluster-admin certificate exists to copy
  # out of state. `managed = true` is mandatory in azurerm 3.x for AKS-managed Entra integration.
  local_account_disabled = true

  azure_active_directory_role_based_access_control {
    managed                = true
    azure_rbac_enabled     = true
    tenant_id              = data.azurerm_client_config.current.tenant_id
    admin_group_object_ids = var.cluster_admin_group_object_ids
  }

  default_node_pool {
    name              = "general"
    vm_size           = var.general_vm_size
    kubelet_disk_type = "OS"
    vnet_subnet_id    = azurerm_subnet.aks.id
    # enable_auto_scaling is the azurerm ~> 3.90 (v3.x) attribute name; a future
    # bump to azurerm ~> 4.x must rename this back to auto_scaling_enabled.
    enable_auto_scaling = true
    min_count           = var.general_min_count
    max_count           = var.general_max_count
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.control_plane.id]
  }

  # The DES Reader grant goes first too, so disk.csi.azure.com never runs without it.
  depends_on = [
    azurerm_role_assignment.control_plane_aks_subnet,
    azurerm_role_assignment.control_plane_apiserver_subnet,
    azurerm_role_assignment.aks_data_volumes_des,
  ]

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
  # vnet_integration_enabled and subnet_id are deprecated preview-API fields in azurerm 3.x;
  # azurerm 4.46+ renames the first to virtual_network_integration_enabled.
  api_server_access_profile {
    authorized_ip_ranges     = var.api_authorized_ip_ranges
    vnet_integration_enabled = true
    subnet_id                = azurerm_subnet.apiserver.id
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
    tei         = { vm_size = var.tei_vm_size, count = var.tei_node_count, label = "tei" }
  }
}

resource "azurerm_kubernetes_cluster_node_pool" "pools" {
  for_each              = local.extra_pools
  name                  = each.key
  kubernetes_cluster_id = azurerm_kubernetes_cluster.this.id
  vm_size               = each.value.vm_size
  kubelet_disk_type     = "OS"
  node_count            = each.value.count
  vnet_subnet_id        = azurerm_subnet.aks.id

  node_labels = {
    "iverson.io/node-pool" = each.value.label
  }

  node_taints = [
    "iverson.io/node-pool=${each.value.label}:NoSchedule"
  ]
}

terraform {
  required_providers {
    azurerm = { source = "hashicorp/azurerm", version = "~> 3.90" }
  }
}

resource "azurerm_resource_group" "this" {
  name     = "${var.cluster_name}-rg"
  location = var.location
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

resource "azurerm_kubernetes_cluster" "this" {
  name                = var.cluster_name
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  dns_prefix          = var.cluster_name
  kubernetes_version  = var.kubernetes_version
  sku_tier            = "Standard"

  default_node_pool {
    name                = "general"
    vm_size             = var.general_vm_size
    vnet_subnet_id      = azurerm_subnet.aks.id
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

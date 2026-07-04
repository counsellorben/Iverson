terraform {
  required_version = ">= 1.7"
  required_providers {
    azurerm    = { source = "hashicorp/azurerm", version = "~> 3.90" }
    kubernetes = { source = "hashicorp/kubernetes", version = "~> 2.31" }
    helm       = { source = "hashicorp/helm", version = "~> 2.14" }
  }
  backend "azurerm" {
    # resource_group_name, storage_account_name, container_name are supplied
    # via -backend-config flags at `terraform init` time, using the outputs
    # from ../bootstrap/azure.
    key = "iverson/azure/terraform.tfstate"
  }
}

variable "location" {
  type    = string
  default = "eastus"
}

variable "cluster_name" {
  type    = string
  default = "iverson"
}

# No default — the operator applying this root module must supply the real
# allow-list. See cluster-azure's api_authorized_ip_ranges.
variable "api_authorized_ip_ranges" { type = list(string) }

provider "azurerm" {
  features {}
}

module "cluster" {
  source                   = "../modules/cluster-azure"
  cluster_name             = var.cluster_name
  location                 = var.location
  api_authorized_ip_ranges = var.api_authorized_ip_ranges
}

provider "kubernetes" {
  host                   = yamldecode(module.cluster.kube_config).clusters[0].cluster.server
  client_certificate     = base64decode(yamldecode(module.cluster.kube_config).users[0].user.client-certificate-data)
  client_key             = base64decode(yamldecode(module.cluster.kube_config).users[0].user.client-key-data)
  cluster_ca_certificate = base64decode(yamldecode(module.cluster.kube_config).clusters[0].cluster.certificate-authority-data)
}

provider "helm" {
  kubernetes {
    host                   = yamldecode(module.cluster.kube_config).clusters[0].cluster.server
    client_certificate     = base64decode(yamldecode(module.cluster.kube_config).users[0].user.client-certificate-data)
    client_key             = base64decode(yamldecode(module.cluster.kube_config).users[0].user.client-key-data)
    cluster_ca_certificate = base64decode(yamldecode(module.cluster.kube_config).clusters[0].cluster.certificate-authority-data)
  }
}

module "operators" {
  source       = "../modules/operators"
  cloud        = "azure"
  cluster_name = module.cluster.cluster_name
  storage_class_config = {
    provisioner = "disk.csi.azure.com"
    parameters  = { skuName = "PremiumV2_LRS" }
  }
}

output "cluster_name" { value = module.cluster.cluster_name }
output "node_pool_labels" { value = module.cluster.node_pool_labels }
output "storage_class_names" { value = module.operators.storage_class_names }
output "kubeconfig_command" { value = "az aks get-credentials --name ${module.cluster.cluster_name} --resource-group ${var.cluster_name}-rg" }

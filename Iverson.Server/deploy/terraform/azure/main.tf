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
    key              = "iverson/azure/terraform.tfstate"
    use_azuread_auth = true
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

# Public addresses permitted to reach the data-volume Key Vault data plane.
# See cluster-azure's key_vault_authorized_ip_ranges.
variable "key_vault_authorized_ip_ranges" { type = list(string) }

# Object IDs of the Entra group(s) that administer the cluster through Azure RBAC; no default.
variable "cluster_admin_group_object_ids" { type = list(string) }

# CSR round-11 #5: the Authentik host the login rate limit matches (authentik.<global.ingressHost>);
# no default.
variable "authentik_host" {
  type = string
  validation {
    condition     = can(regex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", var.authentik_host))
    error_message = "authentik_host must be the lowercase hostname only (no scheme, port or path), because the edge rate-limit rule compares it to the lowercased Host header."
  }
}

# CSR round-11 #4: the roots Application Gateway trusts for backend TLS, keyed by the name the Helm
# value api.ingress.appgwTrustedRootCertificate / authentik.ingress.appgwTrustedRootCertificate
# carries (comma-separated for several). Each value is PEM text: the azurerm provider base64-encodes
# it for the gateway. Empty until the chart has issued its internal CAs (docs/runbooks/csr11-transport-network.md).
variable "appgw_backend_trusted_roots" {
  type    = map(string)
  default = {}
}

provider "azurerm" {
  features {}
}

module "cluster" {
  source                         = "../modules/cluster-azure"
  cluster_name                   = var.cluster_name
  location                       = var.location
  api_authorized_ip_ranges       = var.api_authorized_ip_ranges
  key_vault_authorized_ip_ranges = var.key_vault_authorized_ip_ranges
  cluster_admin_group_object_ids = var.cluster_admin_group_object_ids
  authentik_host                 = var.authentik_host
  authentik_login_rate_limit     = var.authentik_login_rate_limit
  authentik_login_rate_window    = var.authentik_login_rate_window
  appgw_backend_trusted_roots    = var.appgw_backend_trusted_roots
}

# Entra authentication through kubelogin (must be on PATH): local accounts are disabled, so
# there is no client certificate. --login azurecli uses the signed-in Azure CLI identity.
provider "kubernetes" {
  host                   = module.cluster.host
  cluster_ca_certificate = base64decode(module.cluster.cluster_ca_certificate)
  exec {
    api_version = "client.authentication.k8s.io/v1beta1"
    command     = "kubelogin"
    args        = ["get-token", "--login", "azurecli", "--server-id", "6dae42f8-4368-4678-94ff-3960e28e3630"]
  }
}

provider "helm" {
  kubernetes {
    host                   = module.cluster.host
    cluster_ca_certificate = base64decode(module.cluster.cluster_ca_certificate)
    exec {
      api_version = "client.authentication.k8s.io/v1beta1"
      command     = "kubelogin"
      args        = ["get-token", "--login", "azurecli", "--server-id", "6dae42f8-4368-4678-94ff-3960e28e3630"]
    }
  }
}

module "operators" {
  # The deploying principal's Kubernetes RBAC role must exist before any operator resource.
  depends_on = [module.cluster]

  source       = "../modules/operators"
  cloud        = "azure"
  cluster_name = module.cluster.cluster_name
  storage_class_config = {
    provisioner = "disk.csi.azure.com"
    parameters = {
      skuName             = "PremiumV2_LRS"
      diskEncryptionSetID = module.cluster.data_volumes_des_id
    }
  }
}

output "cluster_name" { value = module.cluster.cluster_name }
output "node_pool_labels" { value = module.cluster.node_pool_labels }
output "storage_class_names" { value = module.operators.storage_class_names }
# Copy into values-azure.yaml's authentik.serviceAccountIssuer (docs/runbooks/csr11-identity-plane.md §7).
output "service_account_issuer" { value = module.cluster.service_account_issuer }
# Copy into values-azure.yaml's authentik.ingress.wafPolicyId (docs/runbooks/csr11-identity-plane.md).
output "authentik_waf_policy_id" { value = module.cluster.authentik_waf_policy_id }
# CSR round 12 §3.1: copy into values-azure.yaml's admission.operatorGroups. The admin group(s) are the
# operators group: Entra puts their object IDs in the request's groups.
output "admission_operator_groups" { value = var.cluster_admin_group_object_ids }
output "kubeconfig_command" { value = "az aks get-credentials --name ${module.cluster.cluster_name} --resource-group ${var.cluster_name}-rg" }

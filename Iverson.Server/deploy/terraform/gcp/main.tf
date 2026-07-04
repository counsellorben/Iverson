terraform {
  required_version = ">= 1.7"
  required_providers {
    google     = { source = "hashicorp/google", version = "~> 5.30" }
    kubernetes = { source = "hashicorp/kubernetes", version = "~> 2.31" }
    helm       = { source = "hashicorp/helm", version = "~> 2.14" }
  }
  backend "gcs" {
    # bucket is supplied via -backend-config at `terraform init` time,
    # using the output from ../bootstrap/gcp.
    prefix = "iverson/gcp"
  }
}

variable "project_id" { type = string }

variable "region" {
  type    = string
  default = "us-east1"
}

variable "cluster_name" {
  type    = string
  default = "iverson"
}

# No default — the operator applying this root module must supply the real
# allow-list. See cluster-gcp's master_authorized_networks.
variable "master_authorized_networks" {
  type = list(object({ cidr_block = string, display_name = string }))
}

provider "google" {
  project = var.project_id
  region  = var.region
}

module "cluster" {
  source                     = "../modules/cluster-gcp"
  project_id                 = var.project_id
  cluster_name               = var.cluster_name
  region                     = var.region
  master_authorized_networks = var.master_authorized_networks
}

data "google_client_config" "default" {}

provider "kubernetes" {
  host                   = "https://${module.cluster.cluster_endpoint}"
  cluster_ca_certificate = base64decode(module.cluster.cluster_ca_certificate)
  token                  = data.google_client_config.default.access_token
}

provider "helm" {
  kubernetes {
    host                   = "https://${module.cluster.cluster_endpoint}"
    cluster_ca_certificate = base64decode(module.cluster.cluster_ca_certificate)
    token                  = data.google_client_config.default.access_token
  }
}

module "operators" {
  source       = "../modules/operators"
  cloud        = "gcp"
  cluster_name = module.cluster.cluster_name
  storage_class_config = {
    provisioner = "pd.csi.storage.gke.io"
    parameters  = { type = "pd-ssd" }
  }
}

output "cluster_name" { value = module.cluster.cluster_name }
output "node_pool_labels" { value = module.cluster.node_pool_labels }
output "storage_class_names" { value = module.operators.storage_class_names }
output "kubeconfig_command" { value = "gcloud container clusters get-credentials ${module.cluster.cluster_name} --region ${var.region} --project ${var.project_id}" }

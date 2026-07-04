terraform {
  required_version = ">= 1.7"
  required_providers {
    aws        = { source = "hashicorp/aws", version = "~> 5.0" }
    kubernetes = { source = "hashicorp/kubernetes", version = "~> 2.31" }
    helm       = { source = "hashicorp/helm", version = "~> 2.14" }
  }
  backend "s3" {
    # bucket, dynamodb_table, and region are supplied via -backend-config
    # flags at `terraform init` time, using the outputs from ../bootstrap/aws.
    key = "iverson/aws/terraform.tfstate"
  }
}

variable "region" {
  type    = string
  default = "us-east-1"
}

variable "cluster_name" {
  type    = string
  default = "iverson"
}

# No default — see cluster-aws's api_public_access_cidrs. The operator
# applying this root module must supply the real allow-list.
variable "api_public_access_cidrs" { type = list(string) }

provider "aws" {
  region = var.region
}

module "cluster" {
  source                  = "../modules/cluster-aws"
  cluster_name            = var.cluster_name
  region                  = var.region
  api_public_access_cidrs = var.api_public_access_cidrs
}

data "aws_eks_cluster_auth" "this" {
  name = module.cluster.cluster_name
}

provider "kubernetes" {
  host                   = module.cluster.cluster_endpoint
  cluster_ca_certificate = base64decode(module.cluster.cluster_ca_certificate)
  token                  = data.aws_eks_cluster_auth.this.token
}

provider "helm" {
  kubernetes {
    host                   = module.cluster.cluster_endpoint
    cluster_ca_certificate = base64decode(module.cluster.cluster_ca_certificate)
    token                  = data.aws_eks_cluster_auth.this.token
  }
}

module "operators" {
  source                           = "../modules/operators"
  cloud                            = "aws"
  cluster_name                     = module.cluster.cluster_name
  aws_region                       = var.region
  lb_controller_irsa_role_arn      = module.cluster.lb_controller_irsa_role_arn
  cluster_autoscaler_irsa_role_arn = module.cluster.cluster_autoscaler_irsa_role_arn
  storage_class_config = {
    provisioner = "ebs.csi.aws.com"
    parameters  = { type = "gp3" }
  }
}

output "cluster_name" { value = module.cluster.cluster_name }
output "node_pool_labels" { value = module.cluster.node_pool_labels }
output "storage_class_names" { value = module.operators.storage_class_names }
output "kubeconfig_command" { value = "aws eks update-kubeconfig --name ${module.cluster.cluster_name} --region ${var.region}" }

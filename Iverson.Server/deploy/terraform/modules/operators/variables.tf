variable "cloud" {
  type = string
  validation {
    condition     = contains(["aws", "azure", "gcp"], var.cloud)
    error_message = "cloud must be one of: aws, azure, gcp"
  }
}

variable "cluster_name" {
  type = string
}

variable "aws_region" {
  type    = string
  default = ""
}

variable "lb_controller_irsa_role_arn" {
  type    = string
  default = "" # only meaningful when cloud == "aws"
}

variable "cluster_autoscaler_irsa_role_arn" {
  type    = string
  default = "" # only meaningful when cloud == "aws"
}

variable "vpc_id" {
  type    = string
  default = null # only meaningful when cloud == "aws"
}

# CSR round 12 §3.4: the ranges the Kubernetes API server connects from and is reached at, for the
# operator-namespace policies; each cluster module's api_server_cidrs output. No default.
variable "api_server_cidrs" {
  type = list(string)
  validation {
    condition     = length(var.api_server_cidrs) > 0
    error_message = "api_server_cidrs must list at least one CIDR: it scopes the operator namespaces' API-server egress and webhook ingress."
  }
}

variable "storage_class_config" {
  type = object({
    provisioner = string
    parameters  = map(string)
  })
}

variable "cluster_name" {
  type    = string
  default = "iverson"
}

variable "region" {
  type    = string
  default = "us-east-1"
}

variable "kubernetes_version" {
  type    = string
  default = "1.35"
}

variable "vpc_cidr" {
  type    = string
  default = "10.0.0.0/16"
}

# No default: forces an explicit, conscious choice of which networks may
# reach the EKS API server, rather than silently defaulting to "the entire
# internet" (the AWS provider's own default for endpoint_public_access).
# Pass your office/VPN egress CIDR(s), or ["10.0.0.0/16"] plus
# endpoint_private_access to go private-only (see main.tf).
variable "api_public_access_cidrs" {
  type = list(string)
  validation {
    condition     = length(var.api_public_access_cidrs) > 0
    error_message = "api_public_access_cidrs must contain at least one CIDR — an empty list can be interpreted as unrestricted public access rather than no access."
  }
}

# CSR round 12 §3.1: the IAM roles of the operators, each given the Kubernetes group iverson-operators
# (the chart's admission.operatorGroups) and cluster admin through an access entry. No default, and
# never the Terraform caller's own role, which EKS gives its own entry.
variable "operator_role_arns" {
  type = list(string)
  validation {
    condition     = length(var.operator_role_arns) > 0 && alltrue([for arn in var.operator_role_arns : can(regex("^arn:aws[a-z-]*:iam::[0-9]{12}:role/", arn))])
    error_message = "operator_role_arns must list at least one IAM role ARN: only the operators group may change the api's workloads or mint its tokens."
  }
}

variable "postgres_instance_type" {
  type    = string
  default = "r7i.2xlarge"
}

variable "postgres_node_count" {
  type    = number
  default = 2
}

variable "starrocks_fe_instance_type" {
  type    = string
  default = "r7i.xlarge"
}

variable "starrocks_be_instance_type" {
  type    = string
  default = "r7i.2xlarge"
}

variable "starrocks_be_node_count" {
  type    = number
  default = 2
}

variable "qdrant_instance_type" {
  type    = string
  default = "r7i.2xlarge"
}

variable "qdrant_node_count" {
  type    = number
  default = 2
}

variable "kafka_instance_type" {
  type    = string
  default = "m5.large"
}

variable "kafka_node_count" {
  type    = number
  default = 3
}

variable "ollama_instance_type" {
  type    = string
  default = "c7i.2xlarge"
}

variable "ollama_node_count" {
  type    = number
  default = 2
}

variable "tei_instance_type" {
  type    = string
  default = "c7i.xlarge"
}

variable "tei_node_count" {
  type    = number
  default = 2
}

variable "general_instance_type" {
  type    = string
  default = "m6i.xlarge"
}

variable "general_min_size" {
  type    = number
  default = 2
}

variable "general_max_size" {
  type    = number
  default = 5
}

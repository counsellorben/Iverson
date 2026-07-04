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
  default = "1.30"
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
variable "api_public_access_cidrs" { type = list(string) }

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

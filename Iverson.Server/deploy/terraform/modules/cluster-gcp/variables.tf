variable "project_id" { type = string }

variable "cluster_name" {
  type    = string
  default = "iverson"
}

variable "region" {
  type    = string
  default = "us-east1"
}

variable "kubernetes_version" {
  type    = string
  default = "1.30"
}

# No default — same rationale as the AWS/Azure equivalents: forces an
# explicit choice of which networks may reach the GKE control plane.
variable "master_authorized_networks" {
  type = list(object({ cidr_block = string, display_name = string }))
  validation {
    condition     = length(var.master_authorized_networks) > 0
    error_message = "master_authorized_networks must contain at least one CIDR — an empty list can be interpreted as unrestricted public access rather than no access."
  }
}

variable "postgres_machine_type" {
  type    = string
  default = "n2-highmem-8"
}

variable "postgres_node_count" {
  type    = number
  default = 2
}

variable "starrocks_fe_machine_type" {
  type    = string
  default = "n2-highmem-4"
}

variable "starrocks_be_machine_type" {
  type    = string
  default = "n2-highmem-8"
}

variable "starrocks_be_node_count" {
  type    = number
  default = 2
}

variable "qdrant_machine_type" {
  type    = string
  default = "n2-highmem-8"
}

variable "qdrant_node_count" {
  type    = number
  default = 2
}

variable "kafka_machine_type" {
  type    = string
  default = "n2-standard-2"
}

variable "kafka_node_count" {
  type    = number
  default = 3
}

variable "ollama_machine_type" {
  type    = string
  default = "c2-standard-8"
}

variable "ollama_node_count" {
  type    = number
  default = 2
}

variable "general_machine_type" {
  type    = string
  default = "n2-standard-4"
}

variable "general_min_count" {
  type    = number
  default = 2
}

variable "general_max_count" {
  type    = number
  default = 5
}

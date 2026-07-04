variable "cluster_name" {
  type    = string
  default = "iverson"
}

variable "location" {
  type    = string
  default = "eastus"
}

variable "kubernetes_version" {
  type    = string
  default = "1.30"
}

# No default — same rationale as cluster-aws's api_public_access_cidrs:
# forces an explicit choice of which networks may reach the AKS API server.
variable "api_authorized_ip_ranges" {
  type = list(string)
  validation {
    condition     = length(var.api_authorized_ip_ranges) > 0
    error_message = "api_authorized_ip_ranges must contain at least one CIDR — an empty list can be interpreted as unrestricted public access rather than no access."
  }
}

variable "postgres_vm_size" {
  type    = string
  default = "Standard_E8ds_v5"
}

variable "postgres_node_count" {
  type    = number
  default = 2
}

variable "starrocks_fe_vm_size" {
  type    = string
  default = "Standard_E4ds_v5"
}

variable "starrocks_be_vm_size" {
  type    = string
  default = "Standard_E8ds_v5"
}

variable "starrocks_be_node_count" {
  type    = number
  default = 2
}

variable "qdrant_vm_size" {
  type    = string
  default = "Standard_E8ds_v5"
}

variable "qdrant_node_count" {
  type    = number
  default = 2
}

variable "kafka_vm_size" {
  type    = string
  default = "Standard_D2ds_v5"
}

variable "kafka_node_count" {
  type    = number
  default = 3
}

variable "ollama_vm_size" {
  type    = string
  default = "Standard_F8s_v2"
}

variable "ollama_node_count" {
  type    = number
  default = 2
}

variable "general_vm_size" {
  type    = string
  default = "Standard_D4ds_v5"
}

variable "general_min_count" {
  type    = number
  default = 2
}

variable "general_max_count" {
  type    = number
  default = 5
}

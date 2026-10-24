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
  default = "1.35"
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

# No default — same rationale as api_authorized_ip_ranges: forces an explicit
# choice of which public addresses may reach the data-volume Key Vault's data
# plane. Key Vault firewall rules do not apply to control-plane operations, but
# creating the key is a data-plane call, so the address `terraform apply` runs
# from must appear here.
variable "key_vault_authorized_ip_ranges" {
  type = list(string)
  validation {
    condition     = length(var.key_vault_authorized_ip_ranges) > 0
    error_message = "key_vault_authorized_ip_ranges must contain at least one CIDR — with default_action = Deny and an empty list, terraform apply cannot create the key."
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

variable "tei_vm_size" {
  type    = string
  default = "Standard_F4s_v2"
}

variable "tei_node_count" {
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

# CSR round-11 #5: the edge rate limit on Authentik's flow executor (waf.tf). The window is one of
# the two durations Application Gateway rate-limit rules accept.
variable "authentik_host" {
  type = string
  validation {
    condition     = can(regex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", var.authentik_host))
    error_message = "authentik_host must be the lowercase hostname only (no scheme, port or path), because the edge rate-limit rule compares it to the lowercased Host header."
  }
}

variable "authentik_login_rate_limit" { type = number }

variable "authentik_login_rate_window" {
  type = number
  validation {
    condition     = contains([60, 300], var.authentik_login_rate_window)
    error_message = "authentik_login_rate_window must be 60 or 300 seconds."
  }
}

# CSR round-11 #4: backend-TLS trust roots for the gateway, name -> PEM text (the provider
# base64-encodes it). Names are what the Ingress annotation appgw-trusted-root-certificate refers to.
variable "appgw_backend_trusted_roots" {
  type    = map(string)
  default = {}
}

# No default: the Entra group(s) whose members administer the cluster through Azure RBAC.
variable "cluster_admin_group_object_ids" {
  type = list(string)
  validation {
    condition     = length(var.cluster_admin_group_object_ids) > 0
    error_message = "cluster_admin_group_object_ids must name at least one Entra group."
  }
}

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

variable "storage_class_config" {
  type = object({
    provisioner = string
    parameters  = map(string)
  })
}

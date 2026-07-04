terraform {
  required_version = ">= 1.7"
  required_providers {
    google = { source = "hashicorp/google", version = "~> 5.30" }
  }
}

variable "project_id" {
  type = string
}

variable "region" {
  type    = string
  default = "us-east1"
}

variable "state_bucket_name" {
  type    = string
  default = "iverson-terraform-state"
}

provider "google" {
  project = var.project_id
  region  = var.region
}

resource "google_storage_bucket" "state" {
  name                        = var.state_bucket_name
  project                     = var.project_id
  location                    = var.region
  uniform_bucket_level_access = true

  versioning {
    enabled = true
  }
}

output "state_bucket_name" { value = google_storage_bucket.state.name }

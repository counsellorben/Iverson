terraform {
  required_providers {
    google = { source = "hashicorp/google", version = "~> 5.30" }
  }
}

resource "google_compute_network" "this" {
  name                    = "${var.cluster_name}-vpc"
  auto_create_subnetworks = false
}

resource "google_compute_subnetwork" "this" {
  name          = "${var.cluster_name}-subnet"
  network       = google_compute_network.this.id
  region        = var.region
  ip_cidr_range = "10.2.0.0/20"
}

# private_cluster_config below gives nodes no public IPs, so they need a
# Cloud NAT for egress (pulling container images, reaching external APIs)
# — without this, nodes in a private cluster have no route to the internet
# at all and Task 5's "deploy to a real cluster" verification would fail
# outright, not just be less secure.
resource "google_compute_router" "this" {
  name    = "${var.cluster_name}-router"
  network = google_compute_network.this.id
  region  = var.region
}

resource "google_compute_router_nat" "this" {
  name                               = "${var.cluster_name}-nat"
  router                             = google_compute_router.this.name
  region                             = var.region
  nat_ip_allocate_option             = "AUTO_ONLY"
  source_subnetwork_ip_ranges_to_nat = "ALL_SUBNETWORKS_ALL_IP_RANGES"
}

# Application-layer secrets encryption (GKE's equivalent of EKS's
# encryption_config) — without this, Kubernetes Secrets are only encrypted
# by GCP's default-at-rest storage encryption, not a customer-managed key.
resource "google_kms_key_ring" "gke" {
  name     = "${var.cluster_name}-gke"
  location = var.region
}

resource "google_kms_crypto_key" "gke_secrets" {
  name            = "gke-secrets"
  key_ring        = google_kms_key_ring.gke.id
  rotation_period = "7776000s" # 90 days
}

data "google_project" "this" {}

resource "google_kms_crypto_key_iam_binding" "gke_secrets" {
  crypto_key_id = google_kms_crypto_key.gke_secrets.id
  role          = "roles/cloudkms.cryptoKeyEncrypterDecrypter"
  members = [
    "serviceAccount:service-${data.google_project.this.number}@container-engine-robot.iam.gserviceaccount.com",
  ]
}

resource "google_container_cluster" "this" {
  name       = var.cluster_name
  location   = var.region
  network    = google_compute_network.this.id
  subnetwork = google_compute_subnetwork.this.id

  min_master_version = var.kubernetes_version

  # Private nodes (private_cluster_config below) require a VPC-native
  # (alias-IP) cluster — a routes-based cluster can be rejected by the GKE
  # API when enable_private_nodes = true. An empty ip_allocation_policy
  # block is valid and tells GKE to auto-allocate secondary ranges for
  # pods/services.
  networking_mode = "VPC_NATIVE"
  ip_allocation_policy {}

  # GKE requires a default pool at creation time even though we remove it
  # immediately and create explicit, taint-able pools below instead (Standard
  # mode, not Autopilot — Autopilot doesn't support per-pool machine types/taints).
  remove_default_node_pool = true
  initial_node_count       = 1

  workload_identity_config {
    workload_pool = "${var.project_id}.svc.id.goog"
  }

  database_encryption {
    state    = "ENCRYPTED"
    key_name = google_kms_crypto_key.gke_secrets.id
  }

  # Dataplane V2 (eBPF-based, GKE's current recommendation) gives NetworkPolicy
  # enforcement natively — this is what makes the companion Helm chart plan's
  # NetworkPolicy objects actually take effect; the original plan had neither
  # this nor the older network_policy{} add-on, so every NetworkPolicy would
  # have silently done nothing. Don't also set network_policy{} alongside
  # Dataplane V2 — they're redundant/conflicting per GKE's own docs.
  datapath_provider = "ADVANCED_DATAPATH"

  # Nodes get no public IPs (egress via the Cloud NAT above). The control
  # plane's endpoint stays public but restricted to
  # master_authorized_networks below, rather than enable_private_endpoint =
  # true — a fully private endpoint needs a bastion/VPN/Cloud Interconnect
  # path into the VPC for `kubectl`/CI access, which is out of this plan's
  # scope. Same public-but-restricted posture as EKS/AKS above.
  private_cluster_config {
    enable_private_nodes    = true
    enable_private_endpoint = false
    master_ipv4_cidr_block  = "172.16.0.0/28"
  }

  master_authorized_networks_config {
    dynamic "cidr_blocks" {
      for_each = var.master_authorized_networks
      content {
        cidr_block   = cidr_blocks.value.cidr_block
        display_name = cidr_blocks.value.display_name
      }
    }
  }
}

resource "google_container_node_pool" "general" {
  name       = "general"
  cluster    = google_container_cluster.this.name
  location   = var.region
  node_count = var.general_min_count

  autoscaling {
    min_node_count = var.general_min_count
    max_node_count = var.general_max_count
  }

  node_config {
    machine_type = var.general_machine_type
    workload_metadata_config {
      mode = "GKE_METADATA"
    }
  }
}

locals {
  extra_pools = {
    postgres     = { machine_type = var.postgres_machine_type, count = var.postgres_node_count }
    starrocks-fe = { machine_type = var.starrocks_fe_machine_type, count = 1 }
    starrocks-be = { machine_type = var.starrocks_be_machine_type, count = var.starrocks_be_node_count }
    qdrant       = { machine_type = var.qdrant_machine_type, count = var.qdrant_node_count }
    kafka        = { machine_type = var.kafka_machine_type, count = var.kafka_node_count }
    ollama       = { machine_type = var.ollama_machine_type, count = var.ollama_node_count }
  }
}

resource "google_container_node_pool" "pools" {
  for_each   = local.extra_pools
  name       = each.key
  cluster    = google_container_cluster.this.name
  location   = var.region
  node_count = each.value.count

  node_config {
    machine_type = each.value.machine_type

    labels = {
      "iverson.io/node-pool" = each.key
    }

    taint {
      key    = "iverson.io/node-pool"
      value  = each.key
      effect = "NO_SCHEDULE"
    }

    workload_metadata_config {
      mode = "GKE_METADATA"
    }
  }
}

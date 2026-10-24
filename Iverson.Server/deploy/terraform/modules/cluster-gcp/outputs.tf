output "cluster_name" { value = google_container_cluster.this.name }
output "cluster_endpoint" { value = google_container_cluster.this.endpoint }
output "cluster_ca_certificate" { value = google_container_cluster.this.master_auth[0].cluster_ca_certificate }

# CSR round-12 §2.6: the iss of this cluster's service-account tokens (authentik.serviceAccountIssuer).
# Google does not document the format; the runbook checks it against the cluster's
# /.well-known/openid-configuration at first deploy.
output "service_account_issuer" {
  value = "https://container.googleapis.com/v1/projects/${var.project_id}/locations/${google_container_cluster.this.location}/clusters/${google_container_cluster.this.name}"
}

output "node_pool_labels" {
  value = { for k, v in local.extra_pools : k => "iverson.io/node-pool=${k}" }
}

output "data_volumes_key_id" { value = google_kms_crypto_key.data_volumes.id }

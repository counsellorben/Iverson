output "cluster_name" { value = google_container_cluster.this.name }
output "cluster_endpoint" { value = google_container_cluster.this.endpoint }
output "cluster_ca_certificate" { value = google_container_cluster.this.master_auth[0].cluster_ca_certificate }

output "node_pool_labels" {
  value = { for k, v in local.extra_pools : k => "iverson.io/node-pool=${k}" }
}

output "data_volumes_key_id" { value = google_kms_crypto_key.data_volumes.id }

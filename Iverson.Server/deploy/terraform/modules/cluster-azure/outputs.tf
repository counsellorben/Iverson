output "cluster_name" { value = azurerm_kubernetes_cluster.this.name }

output "kube_config" {
  value     = azurerm_kubernetes_cluster.this.kube_config_raw
  sensitive = true
}

output "node_pool_labels" {
  value = { for k, v in local.extra_pools : v.label => "iverson.io/node-pool=${v.label}" }
}

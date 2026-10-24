output "cluster_name" { value = azurerm_kubernetes_cluster.this.name }

# Under Entra the kubeconfig carries no client certificate; the providers authenticate with
# kubelogin (azure/main.tf), so only the endpoint and CA are needed.
output "host" { value = azurerm_kubernetes_cluster.this.kube_config[0].host }
output "cluster_ca_certificate" { value = azurerm_kubernetes_cluster.this.kube_config[0].cluster_ca_certificate }

# CSR round-12 §2.6: the iss of this cluster's service-account tokens (authentik.serviceAccountIssuer).
# It ends with "/", and the pins compare it byte for byte.
output "service_account_issuer" { value = azurerm_kubernetes_cluster.this.oidc_issuer_url }

output "node_pool_labels" {
  value = { for k, v in local.extra_pools : v.label => "iverson.io/node-pool=${v.label}" }
}

output "data_volumes_des_id" { value = azurerm_disk_encryption_set.data_volumes.id }

output "authentik_waf_policy_id" { value = azurerm_web_application_firewall_policy.authentik.id }

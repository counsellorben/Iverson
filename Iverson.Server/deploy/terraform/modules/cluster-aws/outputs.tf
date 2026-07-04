output "cluster_name" { value = aws_eks_cluster.this.name }
output "cluster_endpoint" { value = aws_eks_cluster.this.endpoint }
output "cluster_ca_certificate" { value = aws_eks_cluster.this.certificate_authority[0].data }

output "lb_controller_irsa_role_arn" {
  value = aws_iam_role.lb_controller_irsa.arn
}

output "node_pool_labels" {
  value = { for k, v in local.node_pools : k => "iverson.io/node-pool=${k}" if k != "general" }
}

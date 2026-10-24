output "cluster_name" { value = aws_eks_cluster.this.name }
output "cluster_endpoint" { value = aws_eks_cluster.this.endpoint }
output "cluster_ca_certificate" { value = aws_eks_cluster.this.certificate_authority[0].data }

# CSR round-12 §2.6: the iss of this cluster's service-account tokens (authentik.serviceAccountIssuer).
output "service_account_issuer" { value = aws_eks_cluster.this.identity[0].oidc[0].issuer }

output "lb_controller_irsa_role_arn" {
  value = aws_iam_role.lb_controller_irsa.arn
}

output "cluster_autoscaler_irsa_role_arn" {
  value = aws_iam_role.cluster_autoscaler_irsa.arn
}

output "data_volumes_key_arn" { value = aws_kms_key.data_volumes.arn }

# CSR round 12 §3.4: as values-aws.yaml's networkPolicy.apiServerCidrs. The four cluster subnets EKS
# places its control-plane interfaces in, and the kubernetes Service ClusterIP of both service ranges
# EKS may pick: the VPC CNI's policy agent matches pod egress before the Service address is rewritten.
output "api_server_cidrs" {
  value = concat(aws_subnet.private[*].cidr_block, aws_subnet.public[*].cidr_block, ["10.100.0.1/32", "172.20.0.1/32"])
}

output "node_pool_labels" {
  value = { for k, v in local.node_pools : k => "iverson.io/node-pool=${k}" if k != "general" }
}

output "vpc_id" { value = aws_vpc.this.id }

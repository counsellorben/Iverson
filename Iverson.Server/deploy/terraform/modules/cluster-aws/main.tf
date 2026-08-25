terraform {
  required_providers {
    aws  = { source = "hashicorp/aws", version = "~> 5.0" }
    tls  = { source = "hashicorp/tls", version = "~> 4.0" }  # fetches the EKS OIDC issuer's cert thumbprint for IRSA
    http = { source = "hashicorp/http", version = "~> 3.4" } # fetches the pinned AWS Load Balancer Controller IAM policy JSON
  }
}

data "aws_availability_zones" "available" {
  state = "available"
}

data "aws_caller_identity" "current" {}

resource "aws_vpc" "this" {
  cidr_block           = var.vpc_cidr
  enable_dns_support   = true
  enable_dns_hostnames = true
  tags                 = { Name = "${var.cluster_name}-vpc" }
}

# VPC Flow Logs: retention bounded at 90 days (matches this file's pattern of
# deliberate, explicit retention/deletion windows, e.g. aws_kms_key.eks_secrets's
# deletion_window_in_days above and the companion Helm chart's Postgres backup
# retentionPolicy).
#
# No customer-managed KMS key on this log group (accepted follow-up — adding a
# CMK is extra cost/rotation surface not currently justified for flow logs).
#tfsec:ignore:aws-cloudwatch-log-group-customer-key
resource "aws_cloudwatch_log_group" "vpc_flow_logs" {
  name              = "/aws/vpc/${var.cluster_name}-flow-logs"
  retention_in_days = 90
}

resource "aws_iam_role" "vpc_flow_logs" {
  name = "${var.cluster_name}-vpc-flow-logs-role"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRole"
      Effect    = "Allow"
      Principal = { Service = "vpc-flow-logs.amazonaws.com" }
    }]
  })
}

# Standard AWS-documented policy for VPC Flow Logs delivery to CloudWatch Logs,
# scoped to the log group this module creates (aws_cloudwatch_log_group.vpc_flow_logs)
# rather than "*" — unlike cluster_autoscaler's policy below, the destination ARN is
# known to Terraform, so the delivery role never needs CreateLogGroup/DescribeLogGroups
# (Terraform itself creates the group) and the stream-level actions can be scoped to it.
# tfsec's wildcard detector still flags this: it treats any bare "*" path segment as
# unscoped, even the CloudWatch-required ":*" stream suffix on an otherwise fully
# resolved, single-log-group ARN. Confirmed false positive, not a real gap.
#tfsec:ignore:aws-iam-no-policy-wildcards
resource "aws_iam_policy" "vpc_flow_logs" {
  name = "${var.cluster_name}-vpc-flow-logs-policy"
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect = "Allow"
      Action = [
        "logs:CreateLogStream",
        "logs:PutLogEvents",
        "logs:DescribeLogStreams",
      ]
      # Trailing ":*" is required — CloudWatch Logs stream ARNs are one path
      # segment deeper than the log group ARN itself.
      Resource = "${aws_cloudwatch_log_group.vpc_flow_logs.arn}:*"
    }]
  })
}

resource "aws_iam_role_policy_attachment" "vpc_flow_logs" {
  role       = aws_iam_role.vpc_flow_logs.name
  policy_arn = aws_iam_policy.vpc_flow_logs.arn
}

resource "aws_flow_log" "this" {
  vpc_id               = aws_vpc.this.id
  traffic_type         = "ALL"
  log_destination_type = "cloud-watch-logs"
  log_destination      = aws_cloudwatch_log_group.vpc_flow_logs.arn
  iam_role_arn         = aws_iam_role.vpc_flow_logs.arn
  tags                 = { Name = "${var.cluster_name}-vpc-flow-log" }
}

resource "aws_subnet" "private" {
  count             = 2
  vpc_id            = aws_vpc.this.id
  cidr_block        = cidrsubnet(var.vpc_cidr, 4, count.index)
  availability_zone = data.aws_availability_zones.available.names[count.index]
  tags = {
    Name                              = "${var.cluster_name}-private-${count.index}"
    "kubernetes.io/role/internal-elb" = "1"
  }
}

# These are the module's designated public subnets (NAT gateway + internet
# gateway route, ELB placement) — map_public_ip_on_launch = true is the
# definition of a public subnet here, not an oversight.
#tfsec:ignore:aws-ec2-no-public-ip-subnet
resource "aws_subnet" "public" {
  count                   = 2
  vpc_id                  = aws_vpc.this.id
  cidr_block              = cidrsubnet(var.vpc_cidr, 4, count.index + 8)
  availability_zone       = data.aws_availability_zones.available.names[count.index]
  map_public_ip_on_launch = true
  tags = {
    Name                     = "${var.cluster_name}-public-${count.index}"
    "kubernetes.io/role/elb" = "1"
  }
}

resource "aws_internet_gateway" "this" {
  vpc_id = aws_vpc.this.id
}

resource "aws_eip" "nat" {
  domain = "vpc"
}

resource "aws_nat_gateway" "this" {
  allocation_id = aws_eip.nat.id
  subnet_id     = aws_subnet.public[0].id
}

resource "aws_route_table" "private" {
  vpc_id = aws_vpc.this.id
  route {
    cidr_block     = "0.0.0.0/0"
    nat_gateway_id = aws_nat_gateway.this.id
  }
}

resource "aws_route_table_association" "private" {
  count          = 2
  subnet_id      = aws_subnet.private[count.index].id
  route_table_id = aws_route_table.private.id
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.this.id
  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.this.id
  }
}

resource "aws_route_table_association" "public" {
  count          = 2
  subnet_id      = aws_subnet.public[count.index].id
  route_table_id = aws_route_table.public.id
}

resource "aws_iam_role" "cluster" {
  name = "${var.cluster_name}-eks-cluster-role"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRole"
      Effect    = "Allow"
      Principal = { Service = "eks.amazonaws.com" }
    }]
  })
}

resource "aws_iam_role_policy_attachment" "cluster_policy" {
  role       = aws_iam_role.cluster.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonEKSClusterPolicy"
}

resource "aws_kms_key" "eks_secrets" {
  description             = "${var.cluster_name} EKS Kubernetes Secrets envelope encryption"
  deletion_window_in_days = 30
  enable_key_rotation     = true
}

resource "aws_kms_key" "data_volumes" {
  description             = "${var.cluster_name} data volume encryption (PersistentVolumes and node root volumes)"
  deletion_window_in_days = 30
  enable_key_rotation     = true
}

# Root statement is mandatory: without an explicit root grant, an IAM key
# policy with no root principal makes the key unmanageable by anyone,
# including the account's own administrators. Resource = "*" in a key
# policy means "this key" (the policy is attached to and scoped by
# aws_kms_key.data_volumes.id already) — it is not an unscoped wildcard
# grant, so the tfsec ignores below are confirmed false positives, not a
# real gap. Task 3 extends this same resource with node-root-volume grants.
#tfsec:ignore:aws-iam-no-policy-wildcards
resource "aws_kms_key_policy" "data_volumes" {
  key_id = aws_kms_key.data_volumes.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "EnableIAMUserPermissions"
        Effect    = "Allow"
        Principal = { AWS = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:root" }
        Action    = "kms:*"
        Resource  = "*"
      },
      {
        Sid       = "AllowEBSCSIDriverUseOfTheKey"
        Effect    = "Allow"
        Principal = { AWS = aws_iam_role.ebs_csi_irsa.arn }
        Action    = ["kms:Encrypt", "kms:Decrypt", "kms:ReEncrypt*", "kms:GenerateDataKey*", "kms:DescribeKey"]
        Resource  = "*"
      },
      {
        Sid       = "AllowEBSCSIDriverAttachmentOfPersistentResources"
        Effect    = "Allow"
        Principal = { AWS = aws_iam_role.ebs_csi_irsa.arn }
        Action    = "kms:CreateGrant"
        Resource  = "*"
        Condition = { Bool = { "kms:GrantIsForAWSResource" = "true" } }
      },
      {
        Sid       = "AllowAutoScalingUseOfTheKey"
        Effect    = "Allow"
        Principal = { AWS = aws_iam_service_linked_role.autoscaling.arn }
        Action    = ["kms:Encrypt", "kms:Decrypt", "kms:ReEncrypt*", "kms:GenerateDataKey*", "kms:DescribeKey"]
        Resource  = "*"
      },
      {
        Sid       = "AllowAutoScalingAttachmentOfPersistentResources"
        Effect    = "Allow"
        Principal = { AWS = aws_iam_service_linked_role.autoscaling.arn }
        Action    = "kms:CreateGrant"
        Resource  = "*"
        Condition = { Bool = { "kms:GrantIsForAWSResource" = "true" } }
      }
    ]
  })
}

# Created here (rather than assumed) so the key policy above can reference
# it as a principal safely: on a greenfield account the AWS-managed
# AWSServiceRoleForAutoScaling service-linked role doesn't exist until
# Auto Scaling is first used, and this resource creates the dependency
# edge that orders the key policy after it. If the target account already
# has the role, `terraform apply` fails with `InvalidInput: Service role
# name AWSServiceRoleForAutoScaling has been taken` — remedy is
# `terraform import module.cluster.aws_iam_service_linked_role.autoscaling <role-arn>`,
# then re-apply.
resource "aws_iam_service_linked_role" "autoscaling" {
  aws_service_name = "autoscaling.amazonaws.com"
}

# tfsec's aws-eks-no-public-cluster-access check can't resolve var.api_public_access_cidrs
# statically, so it treats any endpoint_public_access = true as unrestricted. This module
# requires that variable with no default specifically to force an explicit, real CIDR
# allow-list at apply time (see variables.tf) — same accepted pattern as Azure's
# api_authorized_ip_ranges. Confirmed false positive, not an open 0.0.0.0/0 endpoint.
#tfsec:ignore:aws-eks-no-public-cluster-access
resource "aws_eks_cluster" "this" {
  name     = var.cluster_name
  role_arn = aws_iam_role.cluster.arn
  version  = var.kubernetes_version

  vpc_config {
    subnet_ids              = concat(aws_subnet.private[*].id, aws_subnet.public[*].id)
    endpoint_private_access = true
    endpoint_public_access  = true
    public_access_cidrs     = var.api_public_access_cidrs
  }

  encryption_config {
    resources = ["secrets"]
    provider {
      key_arn = aws_kms_key.eks_secrets.arn
    }
  }

  enabled_cluster_log_types = ["api", "audit", "authenticator", "controllerManager", "scheduler"]

  depends_on = [aws_iam_role_policy_attachment.cluster_policy]
}

# OIDC provider for IRSA (IAM Roles for Service Accounts) — lets specific
# ServiceAccounts assume narrowly-scoped IAM roles instead of every pod on
# every node inheriting the shared node role's permissions via the
# instance metadata service. Replaces the previous node_ebs_csi policy
# attachment below with a role scoped only to the EBS CSI controller's
# ServiceAccount.
data "tls_certificate" "eks_oidc" {
  url = aws_eks_cluster.this.identity[0].oidc[0].issuer
}

resource "aws_iam_openid_connect_provider" "eks" {
  url             = aws_eks_cluster.this.identity[0].oidc[0].issuer
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = [data.tls_certificate.eks_oidc.certificates[0].sha1_fingerprint]
}

resource "aws_iam_role" "ebs_csi_irsa" {
  name = "${var.cluster_name}-ebs-csi-irsa"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRoleWithWebIdentity"
      Effect    = "Allow"
      Principal = { Federated = aws_iam_openid_connect_provider.eks.arn }
      Condition = {
        StringEquals = {
          "${replace(aws_iam_openid_connect_provider.eks.url, "https://", "")}:sub" = "system:serviceaccount:kube-system:ebs-csi-controller-sa"
        }
      }
    }]
  })
}

resource "aws_iam_role_policy_attachment" "ebs_csi_irsa" {
  role       = aws_iam_role.ebs_csi_irsa.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonEBSCSIDriverPolicy"
}

# KMS is deny-by-default on both the key-policy side (aws_kms_key_policy.data_volumes
# above) and the IAM side — AmazonEBSCSIDriverPolicy carries no KMS actions at all, so
# the CSI role needs this grant too or every encrypted-volume attach/detach fails.
resource "aws_iam_policy" "ebs_csi_kms" {
  name = "${var.cluster_name}-ebs-csi-kms"
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["kms:Encrypt", "kms:Decrypt", "kms:ReEncrypt*", "kms:GenerateDataKey*", "kms:DescribeKey"]
        Resource = aws_kms_key.data_volumes.arn
      },
      {
        Effect    = "Allow"
        Action    = "kms:CreateGrant"
        Resource  = aws_kms_key.data_volumes.arn
        Condition = { Bool = { "kms:GrantIsForAWSResource" = "true" } }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "ebs_csi_kms" {
  role       = aws_iam_role.ebs_csi_irsa.name
  policy_arn = aws_iam_policy.ebs_csi_kms.arn
}

resource "aws_iam_role" "lb_controller_irsa" {
  name = "${var.cluster_name}-lb-controller-irsa"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRoleWithWebIdentity"
      Effect    = "Allow"
      Principal = { Federated = aws_iam_openid_connect_provider.eks.arn }
      Condition = {
        StringEquals = {
          "${replace(aws_iam_openid_connect_provider.eks.url, "https://", "")}:sub" = "system:serviceaccount:kube-system:aws-load-balancer-controller"
        }
      }
    }]
  })
}

# The original plan never attached an IAM policy for the AWS Load Balancer
# Controller at all (only cluster-autoscaler-adjacent worker policies) —
# without one, it cannot actually create ALBs/NLBs regardless of IRSA.
# The controller's own repo publishes the exact policy JSON
# (AWSLoadBalancerControllerIAMPolicy) — fetch and pin it here rather than
# reproducing a large, version-sensitive policy document inline.
data "http" "lb_controller_policy" {
  url = "https://raw.githubusercontent.com/kubernetes-sigs/aws-load-balancer-controller/v2.9.0/docs/install/iam_policy.json"
}

resource "aws_iam_policy" "lb_controller" {
  name   = "${var.cluster_name}-lb-controller-policy"
  policy = data.http.lb_controller_policy.response_body
}

resource "aws_iam_role_policy_attachment" "lb_controller_irsa" {
  role       = aws_iam_role.lb_controller_irsa.name
  policy_arn = aws_iam_policy.lb_controller.arn
}

# cluster-autoscaler was previously deployed (in modules/operators) with no
# AWS credentials path at all — no IRSA role, and the shared node role only
# carries worker/CNI/ECR policies. Without this, the autoscaler pods cannot
# call the AWS Auto Scaling / EC2 APIs. Same IRSA pattern as ebs_csi_irsa/
# lb_controller_irsa above; "cluster-autoscaler" is the Helm chart's default
# ServiceAccount name.
resource "aws_iam_role" "cluster_autoscaler_irsa" {
  name = "${var.cluster_name}-cluster-autoscaler-irsa"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRoleWithWebIdentity"
      Effect    = "Allow"
      Principal = { Federated = aws_iam_openid_connect_provider.eks.arn }
      Condition = {
        StringEquals = {
          "${replace(aws_iam_openid_connect_provider.eks.url, "https://", "")}:sub" = "system:serviceaccount:kube-system:cluster-autoscaler"
        }
      }
    }]
  })
}

# Standard AWS-documented cluster-autoscaler policy. Resource = "*" is
# intentional (not a scoping oversight) — ASG ARNs aren't known until node
# groups exist, so scoping further would be a functional regression.
#tfsec:ignore:aws-iam-no-policy-wildcards
resource "aws_iam_policy" "cluster_autoscaler" {
  name = "${var.cluster_name}-cluster-autoscaler-policy"
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect = "Allow"
      Action = [
        "autoscaling:DescribeAutoScalingGroups",
        "autoscaling:DescribeAutoScalingInstances",
        "autoscaling:DescribeLaunchConfigurations",
        "autoscaling:DescribeTags",
        "autoscaling:SetDesiredCapacity",
        "autoscaling:TerminateInstanceInAutoScalingGroup",
        "ec2:DescribeInstanceTypes",
        "ec2:DescribeLaunchTemplateVersions",
      ]
      Resource = "*"
    }]
  })
}

resource "aws_iam_role_policy_attachment" "cluster_autoscaler_irsa" {
  role       = aws_iam_role.cluster_autoscaler_irsa.name
  policy_arn = aws_iam_policy.cluster_autoscaler.arn
}

resource "aws_iam_role" "node" {
  name = "${var.cluster_name}-eks-node-role"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRole"
      Effect    = "Allow"
      Principal = { Service = "ec2.amazonaws.com" }
    }]
  })
}

resource "aws_iam_role_policy_attachment" "node_worker" {
  role       = aws_iam_role.node.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonEKSWorkerNodePolicy"
}

resource "aws_iam_role_policy_attachment" "node_cni" {
  role       = aws_iam_role.node.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonEKS_CNI_Policy"
}

resource "aws_iam_role_policy_attachment" "node_ecr" {
  role       = aws_iam_role.node.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonEC2ContainerRegistryReadOnly"
}

# Enables the VPC CNI's native NetworkPolicy enforcement — without this,
# the companion Helm chart plan's NetworkPolicy objects render and apply
# but are never actually enforced.
resource "aws_eks_addon" "vpc_cni" {
  cluster_name = aws_eks_cluster.this.name
  addon_name   = "vpc-cni"
  # Must stay compatible with the pinned var.kubernetes_version (currently
  # "1.30") — bump deliberately alongside that variable, don't let it float.
  addon_version = "v1.18.5-eksbuild.1"
  configuration_values = jsonencode({
    enableNetworkPolicy = "true"
  })

  # VPC CNI is pre-installed as a self-managed default on a fresh EKS
  # cluster. Declaring it here as an EKS-managed add-on with different
  # config (enableNetworkPolicy) than that pre-installed default fails at
  # create with a conflict error unless conflict resolution is explicit.
  resolve_conflicts_on_create = "OVERWRITE"
  resolve_conflicts_on_update = "OVERWRITE"
}

# The original plan attached the EBS CSI driver's IAM policy to the node
# role but never actually installed the driver itself — the Postgres/
# StarRocks/Qdrant/Kafka/Ollama StorageClasses this module creates (Task 2)
# would have had no provisioner to satisfy their PVCs. Installing it as an
# EKS-managed add-on with its IRSA role attached directly is both the fix
# for that gap and the least-privilege wiring from the IRSA setup above.
resource "aws_eks_addon" "ebs_csi" {
  cluster_name = aws_eks_cluster.this.name
  addon_name   = "aws-ebs-csi-driver"
  # Must stay compatible with the pinned var.kubernetes_version (currently
  # "1.30") — bump deliberately alongside that variable, don't let it float.
  addon_version               = "v1.37.0-eksbuild.1"
  service_account_role_arn    = aws_iam_role.ebs_csi_irsa.arn
  depends_on                  = [aws_iam_role_policy_attachment.ebs_csi_irsa]
  resolve_conflicts_on_create = "OVERWRITE"
  resolve_conflicts_on_update = "OVERWRITE"
}

locals {
  node_pools = {
    postgres     = { instance_type = var.postgres_instance_type, count = var.postgres_node_count }
    starrocks-fe = { instance_type = var.starrocks_fe_instance_type, count = 1 }
    starrocks-be = { instance_type = var.starrocks_be_instance_type, count = var.starrocks_be_node_count }
    qdrant       = { instance_type = var.qdrant_instance_type, count = var.qdrant_node_count }
    kafka        = { instance_type = var.kafka_instance_type, count = var.kafka_node_count }
    ollama       = { instance_type = var.ollama_instance_type, count = var.ollama_node_count }
    general      = { instance_type = var.general_instance_type, count = var.general_min_size }
  }
}

# /dev/xvda is the root device of the EKS-optimized AL2 x86_64 AMI that
# these node groups take (they set no ami_type and the cluster pins
# kubernetes_version 1.30) — naming a different device here would silently
# ADD a second volume instead of encrypting the root, leaving the root
# volume this task exists to encrypt untouched. volume_size must be set
# here rather than via the node group's disk_size argument: EKS rejects a
# disk_size on a node group once a launch template supplies block device
# mappings.
#
# IMDS hardening (metadata_options / IMDSv2) is out of this task's
# encryption scope. Before this launch template existed, these nodes
# already permitted IMDSv1 under the EKS default, so leaving it unset
# preserves the current posture rather than regressing it. Enforcing
# IMDSv2 safely needs http_put_response_hop_limit = 2 (containers reach
# IMDS across an extra network hop; the AWS default hop limit of 1 would
# cut them off) — that is its own change, not a side effect of this one.
#tfsec:ignore:aws-ec2-enforce-launch-config-http-token-imds
resource "aws_launch_template" "pools" {
  for_each    = local.node_pools
  name_prefix = "${var.cluster_name}-${each.key}-"

  block_device_mappings {
    device_name = "/dev/xvda"
    ebs {
      volume_size           = 20
      volume_type           = "gp3"
      encrypted             = true
      kms_key_id            = aws_kms_key.data_volumes.arn
      delete_on_termination = true
    }
  }
}

resource "aws_eks_node_group" "pools" {
  for_each        = local.node_pools
  cluster_name    = aws_eks_cluster.this.name
  node_group_name = "${var.cluster_name}-${each.key}-pool"
  node_role_arn   = aws_iam_role.node.arn
  subnet_ids      = aws_subnet.private[*].id
  instance_types  = [each.value.instance_type]

  launch_template {
    id      = aws_launch_template.pools[each.key].id
    version = aws_launch_template.pools[each.key].latest_version
  }

  scaling_config {
    desired_size = each.value.count
    min_size     = each.key == "general" ? var.general_min_size : each.value.count
    max_size     = each.key == "general" ? var.general_max_size : each.value.count
  }

  labels = {
    "iverson.io/node-pool" = each.key
  }

  dynamic "taint" {
    for_each = each.key == "general" ? [] : [1]
    content {
      key    = "iverson.io/node-pool"
      value  = each.key
      effect = "NO_SCHEDULE"
    }
  }

  depends_on = [
    aws_iam_role_policy_attachment.node_worker,
    aws_iam_role_policy_attachment.node_cni,
    aws_iam_role_policy_attachment.node_ecr,
    aws_eks_addon.vpc_cni,
    aws_kms_key_policy.data_volumes,
  ]
}

# Only the "general" pool needs cluster-autoscaler's ASG discovery tags —
# it's the only pool with min_size != max_size; every stateful pool is
# fixed-size (min=max=count) so cluster-autoscaler has nothing to do there.
#
# aws_eks_node_group's own `tags` argument tags the EKS NodeGroup API
# resource itself, not the underlying Auto Scaling Group — it does not
# propagate to the ASG (or its instances via propagate_at_launch), which is
# what the autoscaler chart's default autoDiscovery.clusterName config
# actually scans for. aws_autoscaling_group_tag is the mechanism that
# attaches tags to the ASG created by an EKS managed node group under AWS
# provider ~> 5.0; its name is only known via the node group's computed
# `resources[0].autoscaling_groups[0].name`.
resource "aws_autoscaling_group_tag" "cluster_autoscaler_enabled" {
  autoscaling_group_name = aws_eks_node_group.pools["general"].resources[0].autoscaling_groups[0].name

  tag {
    key                 = "k8s.io/cluster-autoscaler/enabled"
    value               = "true"
    propagate_at_launch = false
  }
}

resource "aws_autoscaling_group_tag" "cluster_autoscaler_name" {
  autoscaling_group_name = aws_eks_node_group.pools["general"].resources[0].autoscaling_groups[0].name

  tag {
    key                 = "k8s.io/cluster-autoscaler/${var.cluster_name}"
    value               = "owned"
    propagate_at_launch = false
  }
}

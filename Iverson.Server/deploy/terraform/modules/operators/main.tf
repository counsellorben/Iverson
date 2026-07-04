terraform {
  required_providers {
    kubernetes = { source = "hashicorp/kubernetes", version = "~> 2.31" }
    helm       = { source = "hashicorp/helm", version = "~> 2.14" }
  }
}

# Cloud-profile equivalent of the kind setup scripts' namespace step in the
# companion Helm chart plan — same Pod Security Admission baseline
# (`baseline`, not `restricted`; see that plan's Global Constraints for why)
# applies everywhere, not just locally.
resource "kubernetes_namespace" "iverson" {
  metadata {
    name = "iverson"
    labels = {
      "pod-security.kubernetes.io/enforce" = "baseline"
    }
  }
}

resource "helm_release" "cloudnative_pg" {
  name             = "cnpg"
  repository       = "https://cloudnative-pg.github.io/charts"
  chart            = "cloudnative-pg"
  namespace        = "cnpg-system"
  create_namespace = true
}

resource "helm_release" "strimzi" {
  name             = "strimzi"
  repository       = "https://strimzi.io/charts/"
  chart            = "strimzi-kafka-operator"
  namespace        = "kafka"
  create_namespace = true

  set {
    name  = "featureGates"
    value = "+KafkaNodePools\\,+UseKRaft"
  }
}

resource "helm_release" "starrocks_operator" {
  name             = "starrocks-operator"
  repository       = "https://starrocks.github.io/starrocks-kubernetes-operator"
  chart            = "kube-starrocks-operator"
  namespace        = "starrocks"
  create_namespace = true
}

# AWS-only: cluster-autoscaler and the AWS Load Balancer Controller (ALB ingress).
# Azure's ingress (AGIC) is provisioned as an AKS add-on inside cluster-azure, not here,
# since it's tightly coupled to the AKS resource itself. GCP needs neither — GKE's
# built-in GCE ingress controller and node auto-provisioning cover both roles.
resource "helm_release" "cluster_autoscaler" {
  count = var.cloud == "aws" ? 1 : 0

  name       = "cluster-autoscaler"
  repository = "https://kubernetes.github.io/autoscaler"
  chart      = "cluster-autoscaler"
  namespace  = "kube-system"

  set {
    name  = "autoDiscovery.clusterName"
    value = var.cluster_name
  }
  set {
    name  = "awsRegion"
    value = var.aws_region
  }
}

resource "helm_release" "aws_load_balancer_controller" {
  count = var.cloud == "aws" ? 1 : 0

  name       = "aws-load-balancer-controller"
  repository = "https://aws.github.io/eks-charts"
  chart      = "aws-load-balancer-controller"
  namespace  = "kube-system"

  set {
    name  = "clusterName"
    value = var.cluster_name
  }

  # IRSA: scopes the controller's AWS permissions to this one ServiceAccount
  # instead of the shared node role (see cluster-aws's Security baseline
  # note for why). serviceAccount.create defaults to true in this chart, so
  # this just adds the role-arn annotation to the ServiceAccount it creates.
  set {
    name  = "serviceAccount.annotations.eks\\.amazonaws\\.com/role-arn"
    value = var.lb_controller_irsa_role_arn
  }
}

resource "kubernetes_storage_class" "postgres" {
  metadata {
    name = "iverson-postgres"
  }
  storage_provisioner = var.storage_class_config.provisioner
  parameters          = var.storage_class_config.parameters
  volume_binding_mode = "WaitForFirstConsumer"
}

resource "kubernetes_storage_class" "starrocks" {
  metadata {
    name = "iverson-starrocks"
  }
  storage_provisioner = var.storage_class_config.provisioner
  parameters          = var.storage_class_config.parameters
  volume_binding_mode = "WaitForFirstConsumer"
}

resource "kubernetes_storage_class" "qdrant" {
  metadata {
    name = "iverson-qdrant"
  }
  storage_provisioner = var.storage_class_config.provisioner
  parameters          = var.storage_class_config.parameters
  volume_binding_mode = "WaitForFirstConsumer"
}

resource "kubernetes_storage_class" "kafka" {
  metadata {
    name = "iverson-kafka"
  }
  storage_provisioner = var.storage_class_config.provisioner
  parameters          = var.storage_class_config.parameters
  volume_binding_mode = "WaitForFirstConsumer"
}

resource "kubernetes_storage_class" "ollama" {
  metadata {
    name = "iverson-ollama"
  }
  storage_provisioner = var.storage_class_config.provisioner
  parameters          = var.storage_class_config.parameters
  volume_binding_mode = "WaitForFirstConsumer"
}

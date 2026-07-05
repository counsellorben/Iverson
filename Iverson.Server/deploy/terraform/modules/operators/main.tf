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
  name       = "cnpg"
  repository = "https://cloudnative-pg.github.io/charts"
  chart      = "cloudnative-pg"
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time. Verified current stable as of this fix pass.
  version          = "0.29.0"
  namespace        = "cnpg-system"
  create_namespace = true
}

resource "helm_release" "strimzi" {
  name       = "strimzi"
  repository = "https://strimzi.io/charts/"
  chart      = "strimzi-kafka-operator"
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time. Verified current stable as of this fix pass.
  #
  # No featureGates set: KafkaNodePools and UseKRaft graduated to GA in this
  # pinned operator version and their feature-gate flags were removed
  # entirely — setting them crashes the operator with "Unknown feature gate".
  # Both behaviors are already the default.
  version          = "1.1.0"
  namespace        = "kafka"
  create_namespace = true

  # By default the operator only watches its own release namespace ("kafka"),
  # but the app chart's Kafka/KafkaNodePool/KafkaUser CRs are installed into
  # the "iverson" namespace — without this, the operator never sees them and
  # the KafkaNodePool sits at 0 broker pods forever, no error, no event.
  set {
    name  = "watchNamespaces[0]"
    value = kubernetes_namespace.iverson.metadata[0].name
  }

  depends_on = [kubernetes_namespace.iverson]
}

resource "helm_release" "starrocks_operator" {
  name       = "starrocks-operator"
  repository = "https://starrocks.github.io/starrocks-kubernetes-operator"
  # The chart is published under the key "operator" in this repo's
  # index.yaml, not "kube-starrocks-operator" — confirmed live against
  # https://starrocks.github.io/starrocks-kubernetes-operator/index.yaml.
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time.
  chart            = "operator"
  version          = "1.11.5"
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
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time. Chosen to match this module's pinned EKS
  # kubernetes_version ("1.30"): chart 9.37.0 has appVersion 1.30.0.
  version   = "9.37.0"
  namespace = "kube-system"

  set {
    name  = "autoDiscovery.clusterName"
    value = var.cluster_name
  }
  set {
    name  = "awsRegion"
    value = var.aws_region
  }

  # IRSA: scopes the autoscaler's AWS permissions (ASG/EC2 describe +
  # scaling calls) to this one ServiceAccount instead of the shared node
  # role. The cluster-autoscaler chart nests its ServiceAccount under
  # `rbac.serviceAccount`, not top-level `serviceAccount` (verified against
  # the chart's values schema — differs from the aws-load-balancer-
  # controller chart below).
  set {
    name  = "rbac.serviceAccount.annotations.eks\\.amazonaws\\.com/role-arn"
    value = var.cluster_autoscaler_irsa_role_arn
  }
}

resource "helm_release" "aws_load_balancer_controller" {
  count = var.cloud == "aws" ? 1 : 0

  name       = "aws-load-balancer-controller"
  repository = "https://aws.github.io/eks-charts"
  chart      = "aws-load-balancer-controller"
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time. Chart 1.9.0 has appVersion v2.9.0, matching the
  # aws-load-balancer-controller IAM policy version already pinned in
  # cluster-aws's data.http.lb_controller_policy fetch (verified against
  # https://aws.github.io/eks-charts/index.yaml).
  version   = "1.9.0"
  namespace = "kube-system"

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

terraform {
  required_providers {
    kubernetes = { source = "hashicorp/kubernetes", version = "~> 2.31" }
    helm       = { source = "hashicorp/helm", version = "~> 2.14" }
  }
}

# CSR round-11 #19: the remote charts' repositories and versions come from versions/Chart.yaml, a
# dependencies-only stub that Dependabot's helm ecosystem keeps current. deploy/kind/setup.sh,
# setup.ps1 and .github/workflows/image-scan.yml read the same file.
locals {
  operator_charts = { for d in yamldecode(file("${path.module}/versions/Chart.yaml")).dependencies : d.name => d }
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
      # CSR round 12 §4.1: enrolled in the Istio ambient mesh from creation, so every release pod
      # starts enrolled; the chart renders its 15008 (HBONE) rules with its first install.
      "istio.io/dataplane-mode" = "ambient"
    }
  }
}

# CSR round 12 §4.1: the operator namespaces, enrolled in the mesh with iverson (all four together).
# Terraform creates them, rather than each helm_release's create_namespace, so they carry the label
# before any operator pod starts.
resource "kubernetes_namespace" "cnpg_system" {
  metadata {
    name   = "cnpg-system"
    labels = { "istio.io/dataplane-mode" = "ambient" }
  }
}

resource "kubernetes_namespace" "kafka" {
  metadata {
    name   = "kafka"
    labels = { "istio.io/dataplane-mode" = "ambient" }
  }
}

resource "kubernetes_namespace" "starrocks" {
  metadata {
    name   = "starrocks"
    labels = { "istio.io/dataplane-mode" = "ambient" }
  }
}

# CSR round 12 §4.1: Istio 1.31.2 ambient (base, istiod, istio-cni, ztunnel), installed from the charts
# vendored under charts/istio (see vendor-istio-charts.sh there for the source tarball and its SHA-256),
# with the charts' default requests. deploy/kind/setup.sh and setup.ps1 install the same four. On GKE,
# global.platform=gke selects the charts' GKE profile, which adds the ResourceQuota GKE requires for
# the node-critical istio-cni and ztunnel pods outside kube-system. The ztunnel and istio-cni default
# tolerations already admit the datastore pools' NoSchedule taints. They install into istio-system,
# kubernetes_namespace.istio_system below, which the operator-namespace policy chart also governs.
locals {
  istio_platform = var.cloud == "gcp" ? ["gke"] : []
}

resource "helm_release" "istio_base" {
  name      = "istio-base"
  chart     = "${path.module}/charts/istio/base"
  namespace = kubernetes_namespace.istio_system.metadata[0].name

  dynamic "set" {
    for_each = local.istio_platform
    content {
      name  = "global.platform"
      value = set.value
    }
  }
}

resource "helm_release" "istiod" {
  name      = "istiod"
  chart     = "${path.module}/charts/istio/istio-discovery"
  namespace = kubernetes_namespace.istio_system.metadata[0].name

  set {
    name  = "profile"
    value = "ambient"
  }
  dynamic "set" {
    for_each = local.istio_platform
    content {
      name  = "global.platform"
      value = set.value
    }
  }

  depends_on = [helm_release.istio_base]
}

resource "helm_release" "istio_cni" {
  name      = "istio-cni"
  chart     = "${path.module}/charts/istio/istio-cni"
  namespace = kubernetes_namespace.istio_system.metadata[0].name

  set {
    name  = "profile"
    value = "ambient"
  }
  dynamic "set" {
    for_each = local.istio_platform
    content {
      name  = "global.platform"
      value = set.value
    }
  }

  depends_on = [helm_release.istiod]
}

resource "helm_release" "ztunnel" {
  name      = "ztunnel"
  chart     = "${path.module}/charts/istio/ztunnel"
  namespace = kubernetes_namespace.istio_system.metadata[0].name

  dynamic "set" {
    for_each = local.istio_platform
    content {
      name  = "global.platform"
      value = set.value
    }
  }

  depends_on = [helm_release.istio_cni]
}

# CSR round 12 §3.4: the operator-namespace policy chart (below) governs istio-system, which must exist
# before it installs. The Istio releases install into this namespace.
resource "kubernetes_namespace" "istio_system" {
  metadata {
    name = "istio-system"
  }
}

resource "helm_release" "cloudnative_pg" {
  name       = "cnpg"
  repository = local.operator_charts["cloudnative-pg"].repository
  chart      = "cloudnative-pg"
  version    = local.operator_charts["cloudnative-pg"].version
  namespace  = kubernetes_namespace.cnpg_system.metadata[0].name
}

resource "helm_release" "strimzi" {
  name       = "strimzi"
  repository = local.operator_charts["strimzi-kafka-operator"].repository
  chart      = "strimzi-kafka-operator"
  # No featureGates set: KafkaNodePools and UseKRaft graduated to GA in this
  # pinned operator version and their feature-gate flags were removed
  # entirely — setting them crashes the operator with "Unknown feature gate".
  # Both behaviors are already the default.
  version   = local.operator_charts["strimzi-kafka-operator"].version
  namespace = kubernetes_namespace.kafka.metadata[0].name

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
  repository = local.operator_charts["operator"].repository
  # The chart is published under the key "operator" in this repo's
  # index.yaml, not "kube-starrocks-operator" — confirmed live against
  # https://starrocks.github.io/starrocks-kubernetes-operator/index.yaml.
  chart     = "operator"
  version   = local.operator_charts["operator"].version
  namespace = kubernetes_namespace.starrocks.metadata[0].name
}

# AWS-only: cluster-autoscaler and the AWS Load Balancer Controller (ALB ingress).
# Azure's ingress (AGIC) is provisioned as an AKS add-on inside cluster-azure, not here,
# since it's tightly coupled to the AKS resource itself. GCP needs neither — GKE's
# built-in GCE ingress controller and node auto-provisioning cover both roles.
resource "helm_release" "cluster_autoscaler" {
  count = var.cloud == "aws" ? 1 : 0

  name       = "cluster-autoscaler"
  repository = local.operator_charts["cluster-autoscaler"].repository
  chart      = "cluster-autoscaler"
  # The version must match the EKS kubernetes_version (see the note in versions/Chart.yaml).
  version   = local.operator_charts["cluster-autoscaler"].version
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
  repository = local.operator_charts["aws-load-balancer-controller"].repository
  chart      = "aws-load-balancer-controller"
  # The version must match cluster-aws's committed IAM policy (see the note in versions/Chart.yaml).
  version   = local.operator_charts["aws-load-balancer-controller"].version
  namespace = "kube-system"

  set {
    name  = "clusterName"
    value = var.cluster_name
  }

  # Without instance metadata (hop limit 1, cluster-aws) the controller cannot discover these.
  set {
    name  = "region"
    value = var.aws_region
  }
  set {
    name  = "vpcId"
    value = var.vpc_id
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

resource "kubernetes_storage_class" "prometheus" {
  metadata {
    name = "iverson-prometheus"
  }
  storage_provisioner = var.storage_class_config.provisioner
  parameters          = var.storage_class_config.parameters
  volume_binding_mode = "WaitForFirstConsumer"
}

# CSR round 12 §3.4 (#16): default-deny with explicit allows in cnpg-system, kafka, starrocks and
# istio-system. The same chart serves kind (deploy/kind/setup.sh, values-kind.yaml). Each namespace must
# exist first: Terraform creates each of them above (cnpg-system, kafka and starrocks enrolled in the mesh).
resource "helm_release" "operator_namespace_policies" {
  name      = "operator-namespace-policies"
  chart     = "${path.module}/charts/operator-namespace-policies"
  namespace = "kube-system"
  values = [yamlencode({
    apiServerCidrs = var.api_server_cidrs
    # CSR round 12 §4.1: the CNPG operator's webhook port 9443 is PERMISSIVE on the clouds, whose API
    # server is outside the mesh (templates/peerauthentication-cnpg.yaml).
    cnpgWebhookPermissive = true
  })]

  depends_on = [
    kubernetes_namespace.istio_system,
    helm_release.istio_base,
    helm_release.cloudnative_pg,
    helm_release.strimzi,
    helm_release.starrocks_operator,
  ]
}

# The Kubernetes provider cannot apply this pair: 2.x has no policy resource,
# 3.x adds a policy but no binding, and kubernetes_manifest needs API access at
# plan time, which would break the single-pass apply every cloud root relies on.
# A helm_release renders at apply, so the single pass survives.
resource "helm_release" "pvc_storageclass_policy" {
  name      = "pvc-storageclass-policy"
  chart     = "${path.module}/charts/pvc-storageclass-policy"
  namespace = kubernetes_namespace.iverson.metadata[0].name
  values = [yamlencode({
    allowedStorageClasses = [
      kubernetes_storage_class.postgres.metadata[0].name,
      kubernetes_storage_class.starrocks.metadata[0].name,
      kubernetes_storage_class.qdrant.metadata[0].name,
      kubernetes_storage_class.kafka.metadata[0].name,
      kubernetes_storage_class.prometheus.metadata[0].name,
    ]
  })]
}

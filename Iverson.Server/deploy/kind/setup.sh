#!/usr/bin/env bash
set -euo pipefail

# CSR round 12 §3.6: kind-config.yaml pins kindest/node:v1.35.8, which was run with kind v0.33.0; an older
# kind is refused before anything touches the cluster.
KIND_MIN=v0.33.0
KIND_VERSION="$(kind version | awk '{ print $2 }')"
if [ "$(printf '%s\n%s\n' "$KIND_MIN" "${KIND_VERSION%%-*}" | sort -V | head -n 1)" != "$KIND_MIN" ]; then
  echo "kind $KIND_VERSION is older than $KIND_MIN, the version kind-config.yaml's node image was run with; install kind $KIND_MIN or later and recreate the cluster" >&2
  exit 1
fi

# If your `docker` CLI is actually podman (podman-docker shim) or you set
# KIND_EXPERIMENTAL_PROVIDER=podman directly, podman's default PidsLimit (2048) on the
# kind node container becomes the *entire* node's pids budget, which systemd then divides
# across every pod/container cgroup inside it — StarRocks BE alone needs more threads than
# its resulting share (as low as ~300) allows, crash-looping on startup with
# `std::system_error: Resource temporarily unavailable`. Fix once, before creating the
# cluster, by adding `pids_limit = -1` under a `[containers]` table in
# ~/.config/containers/containers.conf (or /etc/containers/containers.conf) — this is a
# podman-wide default, not something kind-config.yaml or this script can set per-container.
# Not needed at all when using real Docker as the provider.

# CSR round-11 #19: the CNPG, Strimzi and StarRocks operator chart versions come from the stub
# Chart.yaml the cloud Terraform installs read (terraform/modules/operators/versions), which
# Dependabot keeps current, so kind and the cloud installs run the same operator builds. They are
# read before anything touches the cluster, so a missing pin stops the script here. CSR round 12 §4.2: the
# charts only kind installs (Calico, metrics-server) are pinned the same way in deploy/kind/versions.
OPERATOR_PINS="$(cd "$(dirname "${BASH_SOURCE[0]}")/../terraform/modules/operators/versions" && pwd)"
KIND_PINS="$(cd "$(dirname "${BASH_SOURCE[0]}")/versions" && pwd)"
operator_version() {   # $1 = a stub chart directory, $2 = a chart name in its Chart.yaml; prints its version
  local list
  list=$(helm dependency list "$1") ||
    { echo "helm dependency list failed for $1" >&2; return 1; }
  printf '%s\n' "$list" | awk -v chart="$2" '$1 == chart && !n++ { print $2 } END { exit !n }' ||
    { echo "no $2 pin in $1/Chart.yaml" >&2; return 1; }
}
CNPG_VERSION=$(operator_version "$OPERATOR_PINS" cloudnative-pg)
STRIMZI_VERSION=$(operator_version "$OPERATOR_PINS" strimzi-kafka-operator)
STARROCKS_OPERATOR_VERSION=$(operator_version "$OPERATOR_PINS" operator)
CALICO_VERSION=$(operator_version "$KIND_PINS" tigera-operator)
METRICS_SERVER_VERSION=$(operator_version "$KIND_PINS" metrics-server)

# CSR round 12 §5: with no argument this script runs the whole fresh install. On an existing cluster the
# upgrade runs the platform steps one at a time (docs/runbooks/csr12-platform.md §3): `setup.sh --only
# <phase>` runs that phase alone. Each phase is a function below; the fresh install calls them in the
# same order, after the operators.
PHASES="istio operator-namespaces ambient-labels gateway"
ONLY=""
if [ "$#" -gt 0 ]; then
  if [ "$#" -ne 2 ] || [ "$1" != "--only" ] || [[ "$2" == *[[:space:]]* || " $PHASES " != *" $2 "* ]]; then
    echo "usage: $0 [--only <phase>]   (phases: $PHASES)" >&2
    exit 2
  fi
  ONLY="$2"
fi

phase_istio() {
  echo "Installing the Gateway API CRDs and Istio 1.31.2 (ambient)..."
  # CSR round 12 §4.1: Istio ambient carries every in-cluster hop over mutual TLS. Its charts are vendored
  # where the cloud Terraform installs them from (terraform/modules/operators/charts/istio; the source
  # tarball and its SHA-256 are in vendor-istio-charts.sh there). kind installs them with the smaller
  # requests in deploy/kind/istio/, so the laptop profile still fits its 4-CPU host. The Gateway API
  # standard CRDs carry kind's edge Gateway and the chart's routes; nothing else installs them on kind.
  # istio-cni chains itself into Calico's CNI configuration, so Calico comes first.
  local charts values
  charts="$(cd "$(dirname "${BASH_SOURCE[0]}")/../terraform/modules/operators/charts/istio" && pwd)"
  values="$(cd "$(dirname "${BASH_SOURCE[0]}")/istio" && pwd)"
  kubectl apply --server-side -f https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.6.3/standard-install.yaml
  helm upgrade --install istio-base "$charts/base" \
    --namespace istio-system --create-namespace \
    --wait
  helm upgrade --install istiod "$charts/istio-discovery" \
    --namespace istio-system \
    -f "$values/istiod.yaml" \
    --wait
  helm upgrade --install istio-cni "$charts/istio-cni" \
    --namespace istio-system \
    -f "$values/istio-cni.yaml" \
    --wait
  helm upgrade --install ztunnel "$charts/ztunnel" \
    --namespace istio-system \
    -f "$values/ztunnel.yaml" \
    --wait
}

phase_operator_namespaces() {
  echo "Installing the operator-namespace NetworkPolicies (CSR round 12 §3.4)..."
  # Default-deny with explicit allows in cnpg-system, kafka, starrocks, istio-system, tigera-operator and
  # iverson-edge, from the chart the cloud Terraform installs too (modules/operators), with kind's
  # addresses from values-kind.yaml. Every namespace it governs must exist first: istio-system, which Istio
  # installs into, and iverson-edge, the gateway's namespace (not enrolled in the mesh).
  local ns policies
  for ns in istio-system iverson-edge; do
    kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f -
  done
  policies="$(cd "$(dirname "${BASH_SOURCE[0]}")/../terraform/modules/operators/charts/operator-namespace-policies" && pwd)"
  helm upgrade --install operator-namespace-policies "$policies" \
    --namespace kube-system \
    -f "$policies/values-kind.yaml" \
    --wait
}

# CSR round 12 §4.1, §5: the four namespaces are labelled together. An enrolled pod's egress leaves on
# the HBONE port 15008, so a release whose NetworkPolicies do not list 15008 loses every hop once its
# namespace is enrolled. They are labelled when namespace iverson has no NetworkPolicy yet (a fresh
# cluster: the chart brings its 15008 rules with its first install) or when its policies already list
# 15008 (the release was upgraded first). Otherwise nothing is labelled, and the upgrade order applies:
# helm upgrade the release, then run `--only ambient-labels`. Running pods are captured without a restart.
ambient_enroll() {
  local policies ports
  policies="$(kubectl get networkpolicy -n iverson -o name)"
  ports=" $(kubectl get networkpolicy -n iverson -o jsonpath='{..port}') "
  if [ -n "$policies" ] && [ "${ports#* 15008 }" = "$ports" ]; then
    echo "Not enrolled: the NetworkPolicies in namespace iverson do not list port 15008 yet. Run the release's helm upgrade first, then run this script with --only ambient-labels."
    return 0
  fi
  kubectl label namespace iverson cnpg-system kafka starrocks istio.io/dataplane-mode=ambient --overwrite
}

phase_ambient_labels() {
  echo "Enrolling iverson, cnpg-system, kafka and starrocks in the ambient mesh..."
  ambient_enroll
}

phase_gateway() {
  echo "Creating the edge gateway (Istio, namespace iverson-edge)..."
  # CSR round 12 §4.5: kind's edge, replacing ingress-nginx (archived 2026-03-24, no further security
  # fixes). edge-gateway.yaml gives the gateway's pod host ports 80 and 443, which kind-config.yaml maps to
  # 127.0.0.1:8080 and 8443; the chart renders the routes that attach to it. Its namespace, iverson-edge,
  # is the operator-namespace phase's. istiod creates the gateway's Deployment (edge-istio) a few seconds
  # after the Gateway, so wait for it to exist first.
  kubectl apply -f "$(dirname "${BASH_SOURCE[0]}")/edge-gateway.yaml"
  for _ in $(seq 1 30); do
    kubectl get deployment edge-istio -n iverson-edge >/dev/null 2>&1 && break
    sleep 2
  done
  kubectl rollout status deployment/edge-istio -n iverson-edge --timeout=180s
}

if [ -n "$ONLY" ]; then
  "phase_${ONLY//-/_}"
  exit 0
fi

echo "Installing Calico (kind's default CNI, kindnet, does not enforce NetworkPolicy)..."
kubectl create namespace tigera-operator --dry-run=client -o yaml | kubectl apply -f -
# The tigera-operator chart bundles both the operator and its Custom Resources
# (Installation, APIServer, ...) in one release, but the CRDs for those CRs are
# registered by the operator itself at runtime, not by Helm. On a brand-new
# cluster this makes a single-shot install always fail: Helm's manifest
# validation rejects the CR templates because their CRD kinds don't exist yet.
# Work around it with a two-pass install: first bring up just the operator
# (CRs disabled) so it can register its CRDs, then re-run with defaults
# restored (--reset-values, since `helm upgrade` otherwise keeps reusing the
# disabled values) to add the CRs now that the CRDs exist.
# CSR round-2 finding #12: pin every operator install to an explicit chart
# version rather than letting it float to whatever the repo's index.yaml
# currently calls "latest"; the version is read from deploy/kind/versions (CSR round 12 §4.2).
helm upgrade --install calico tigera-operator \
  --repo https://docs.tigera.io/calico/charts \
  --version "$CALICO_VERSION" \
  --namespace tigera-operator \
  --set installation.enabled=false \
  --set apiServer.enabled=false \
  --set goldmane.enabled=false \
  --set whisker.enabled=false \
  --wait
# The operator registers this CRD a few seconds after its pod is Ready, and `kubectl wait`
# fails immediately with NotFound on a resource that does not exist yet (under `set -e` that
# ended the first pass of this script on every fresh cluster). Poll for existence first.
for _ in $(seq 1 30); do
  kubectl get crd installations.operator.tigera.io >/dev/null 2>&1 && break
  sleep 2
done
kubectl wait --for=condition=Established crd/installations.operator.tigera.io --timeout=60s
helm upgrade --install calico tigera-operator \
  --repo https://docs.tigera.io/calico/charts \
  --version "$CALICO_VERSION" \
  --namespace tigera-operator \
  --reset-values \
  --wait

echo "Creating iverson namespace with restricted-baseline Pod Security Admission label..."
kubectl create namespace iverson --dry-run=client -o yaml | kubectl apply -f -
kubectl label namespace iverson pod-security.kubernetes.io/enforce=baseline --overwrite

if ! kubectl get secret iverson-api-tls -n iverson >/dev/null 2>&1; then
  echo "Generating a self-signed dev cert for the edge gateway's HTTPS listener (iverson.local)..."
  openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
    -keyout /tmp/iverson-api-tls.key -out /tmp/iverson-api-tls.crt \
    -subj "/CN=iverson.local" -addext "subjectAltName=DNS:iverson.local"
  kubectl create secret tls iverson-api-tls -n iverson \
    --cert=/tmp/iverson-api-tls.crt --key=/tmp/iverson-api-tls.key
  rm -f /tmp/iverson-api-tls.key /tmp/iverson-api-tls.crt
fi

echo "Installing CloudNativePG operator..."
# CSR round-2 finding #12: the same version as modules/operators/main.tf's
# cloudnative_pg helm_release (both read versions/Chart.yaml, see the top).
helm upgrade --install cnpg cloudnative-pg \
  --repo https://cloudnative-pg.github.io/charts \
  --version "$CNPG_VERSION" \
  --namespace cnpg-system --create-namespace \
  --wait

echo "Installing Strimzi operator..."
# KafkaNodePools and UseKRaft graduated to GA in the operator image this chart
# currently pulls (v1.1.0+), and their feature-gate flags were removed
# entirely — setting them now fails with "Unknown feature gate". Both
# behaviors are already the default, so no --set is needed.
#
# watchNamespaces: by default the operator only watches its own release
# namespace ("kafka"), but the app chart's Kafka/KafkaNodePool/KafkaUser CRs
# are installed into the "iverson" namespace (wherever the app release goes)
# — without this, the operator never sees them and the KafkaNodePool sits at
# 0 broker pods forever, no error, no event, nothing to grep for.
# CSR round-2 finding #12: the same version as modules/operators/main.tf's
# strimzi helm_release (both read versions/Chart.yaml, see the top).
helm upgrade --install strimzi strimzi-kafka-operator \
  --repo https://strimzi.io/charts/ \
  --version "$STRIMZI_VERSION" \
  --namespace kafka --create-namespace \
  --set watchNamespaces="{iverson}" \
  --wait

echo "Installing StarRocks operator..."
# Chart was renamed upstream from "kube-starrocks-operator" to "operator".
# CSR round-2 finding #12: the same version as modules/operators/main.tf's
# starrocks_operator helm_release (both read versions/Chart.yaml, see the top).
helm upgrade --install starrocks-operator operator \
  --repo https://starrocks.github.io/starrocks-kubernetes-operator \
  --version "$STARROCKS_OPERATOR_VERSION" \
  --namespace starrocks --create-namespace \
  --wait

echo "Installing metrics-server..."
# Without it both HPAs report cpu: <unknown> and never restore a Deployment that was
# manually scaled to 0 — which is how the worker (and with it EnrichmentConsumer) sat
# at 0 replicas during the 2026-07-27 smoke test with no error anywhere. Also makes
# `kubectl top` work, which the laptop profile's capacity numbers depend on.
# --kubelet-insecure-tls is required on kind: kubelet serves a self-signed cert that
# metrics-server will otherwise reject.
# CSR round-2 finding #12: an explicit chart version, read from deploy/kind/versions (CSR round 12 §4.2).
helm upgrade --install metrics-server metrics-server \
  --repo https://kubernetes-sigs.github.io/metrics-server/ \
  --version "$METRICS_SERVER_VERSION" \
  --namespace kube-system \
  --set 'args={--kubelet-insecure-tls}' \
  --wait

phase_istio
phase_operator_namespaces
phase_ambient_labels
phase_gateway

echo "All operators installed."
echo "Next: deploy/kind/build-and-load-image.sh to build+load the app image, then helm upgrade --install --wait iverson . -f values-local.yaml -n iverson --kube-as-user=kubernetes-admin --kube-as-group=kubeadm:cluster-admins --kube-as-group=iverson-operators (the kube-as flags: CSR round 12's admission policy admits only the iverson-operators group to install or change the api, see docs/runbooks/operators-group.md; --wait: the post-upgrade StarRocks role revoke then runs once the new pods are ready, though a previous-release pod may still be running or terminating; see docs/runbooks/csr12-server.md §1)"
echo "Note: TEI and Ollama run model images with their weights baked in. Before installing the chart, build and load them: deploy/kind/build-and-load-image.sh 0.1.0 iverson --model-images (add --values <overlay> to match the overlay you install)."

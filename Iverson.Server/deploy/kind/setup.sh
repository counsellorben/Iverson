#!/usr/bin/env bash
set -euo pipefail

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
# currently calls "latest" — verified current stable as of this fix pass
# (docs.tigera.io/calico/charts/index.yaml).
helm upgrade --install calico tigera-operator \
  --repo https://docs.tigera.io/calico/charts \
  --version v3.32.2 \
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
  --version v3.32.2 \
  --namespace tigera-operator \
  --reset-values \
  --wait

echo "Creating iverson namespace with restricted-baseline Pod Security Admission label..."
kubectl create namespace iverson --dry-run=client -o yaml | kubectl apply -f -
kubectl label namespace iverson pod-security.kubernetes.io/enforce=baseline --overwrite

echo "Installing ingress-nginx..."
# allow-snippet-annotations is disabled by default since ingress-nginx v1.9; the
# api and admin-ui charts' Ingress templates emit
# nginx.ingress.kubernetes.io/configuration-snippet on this className, which the
# validating admission webhook rejects (or the controller silently drops) without
# this setting. This install is this repo's only source of the controller for the
# only ingress class the kind profile actually runs, so this is the one place that
# setting can be turned on.
#
# SECURITY TRADE-OFF (CSR round-2 finding #13): upstream disables snippet annotations
# by default because they let anyone who can create/edit an Ingress inject arbitrary
# nginx.conf directives (Lua, arbitrary proxying, config-level RCE surface) into the
# shared controller — a real risk on a multi-tenant cluster where Ingress creation isn't
# trusted. Here it is acceptable: this is a single-tenant kind/local cluster, every
# Ingress in the namespace is rendered from this repo's own chart templates (not
# arbitrary user input), and the only annotation actually emitted is the fixed
# security-header snippet those templates hardcode — there is no untrusted path that can
# reach configuration-snippet. This setting is scoped to the `nginx` ingress class used
# only by kind/local; production/cloud profiles use alb/gce/azure-application-gateway
# (see values-aws/azure/gcp.yaml) and never enable this flag — azure wires the same
# security headers through AGIC's own rewrite-rule-set mechanism instead (see
# charts/api/templates/ingress.yaml's `securityHeadersRuleSet`), and none of the cloud
# ingress classes support or need nginx's snippet-annotation feature at all.
# CSR round-3 finding #7: chart 4.11.3 shipped controller v1.11.3, which predates the
# March-2025 "IngressNightmare" CVE fixes (CVE-2025-1097, CVE-2025-1098, CVE-2025-1974,
# CVE-2025-24513, CVE-2025-24514), landed in controller v1.11.5 / v1.12.1. Bumped to
# chart 4.12.8 (controller v1.12.8, confirmed via the ingress-nginx Helm repo's
# index.yaml chart-version-to-appVersion mapping as of this fix), well past the fixed
# versions. Re-verified `helm template` output with this repo's exact --set flags
# renders cleanly at this chart version.
helm upgrade --install ingress-nginx ingress-nginx \
  --repo https://kubernetes.github.io/ingress-nginx \
  --version 4.12.8 \
  --namespace ingress-nginx --create-namespace \
  --set controller.hostPort.enabled=true \
  --set controller.service.type=ClusterIP \
  --set controller.allowSnippetAnnotations=true \
  --wait

echo "Installing CloudNativePG operator..."
# CSR round-2 finding #12: pinned to the same version as
# modules/operators/main.tf's cloudnative_pg helm_release, so kind and the
# cloud Terraform installs run the identical operator build.
helm upgrade --install cnpg cloudnative-pg \
  --repo https://cloudnative-pg.github.io/charts \
  --version 0.29.0 \
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
# CSR round-2 finding #12: pinned to the same version as
# modules/operators/main.tf's strimzi helm_release, so kind and the cloud
# Terraform installs run the identical operator build.
helm upgrade --install strimzi strimzi-kafka-operator \
  --repo https://strimzi.io/charts/ \
  --version 1.1.0 \
  --namespace kafka --create-namespace \
  --set watchNamespaces="{iverson}" \
  --wait

echo "Installing StarRocks operator..."
# Chart was renamed upstream from "kube-starrocks-operator" to "operator".
# CSR round-2 finding #12: pinned to the same version as
# modules/operators/main.tf's starrocks_operator helm_release, so kind and
# the cloud Terraform installs run the identical operator build.
helm upgrade --install starrocks-operator operator \
  --repo https://starrocks.github.io/starrocks-kubernetes-operator \
  --version 1.11.5 \
  --namespace starrocks --create-namespace \
  --wait

echo "Installing metrics-server..."
# Without it both HPAs report cpu: <unknown> and never restore a Deployment that was
# manually scaled to 0 — which is how the worker (and with it EnrichmentConsumer) sat
# at 0 replicas during the 2026-07-27 smoke test with no error anywhere. Also makes
# `kubectl top` work, which the laptop profile's capacity numbers depend on.
# --kubelet-insecure-tls is required on kind: kubelet serves a self-signed cert that
# metrics-server will otherwise reject.
# CSR round-2 finding #12: verified current stable as of this fix pass
# (kubernetes-sigs.github.io/metrics-server/index.yaml).
helm upgrade --install metrics-server metrics-server \
  --repo https://kubernetes-sigs.github.io/metrics-server/ \
  --version 3.9.0 \
  --namespace kube-system \
  --set 'args={--kubelet-insecure-tls}' \
  --wait

echo "All operators installed."
echo "Next: deploy/kind/build-and-load-image.sh to build+load the app image, then helm upgrade --install iverson . -f values-local.yaml -n iverson"
echo "Note: if you later raise tei.storageSize (or ollama.storageSize while ollama is deployed) on an existing cluster, 'helm upgrade' will fail (StatefulSet volumeClaimTemplates are immutable) for iverson-tei-bge-base (or iverson-ollama) - see the comment next to storageSize in values-local.yaml."

#!/usr/bin/env bash
set -euo pipefail

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
helm upgrade --install calico tigera-operator \
  --repo https://docs.tigera.io/calico/charts \
  --namespace tigera-operator \
  --set installation.enabled=false \
  --set apiServer.enabled=false \
  --set goldmane.enabled=false \
  --set whisker.enabled=false \
  --wait
kubectl wait --for=condition=Established crd/installations.operator.tigera.io --timeout=60s
helm upgrade --install calico tigera-operator \
  --repo https://docs.tigera.io/calico/charts \
  --namespace tigera-operator \
  --reset-values \
  --wait

echo "Creating iverson namespace with restricted-baseline Pod Security Admission label..."
kubectl create namespace iverson --dry-run=client -o yaml | kubectl apply -f -
kubectl label namespace iverson pod-security.kubernetes.io/enforce=baseline --overwrite

echo "Installing ingress-nginx..."
helm upgrade --install ingress-nginx ingress-nginx \
  --repo https://kubernetes.github.io/ingress-nginx \
  --namespace ingress-nginx --create-namespace \
  --set controller.hostPort.enabled=true \
  --set controller.service.type=ClusterIP \
  --wait

echo "Installing CloudNativePG operator..."
helm upgrade --install cnpg cloudnative-pg \
  --repo https://cloudnative-pg.github.io/charts \
  --namespace cnpg-system --create-namespace \
  --wait

echo "Installing Strimzi operator..."
# KafkaNodePools and UseKRaft graduated to GA in the operator image this chart
# currently pulls (v1.1.0+), and their feature-gate flags were removed
# entirely — setting them now fails with "Unknown feature gate". Both
# behaviors are already the default, so no --set is needed.
helm upgrade --install strimzi strimzi-kafka-operator \
  --repo https://strimzi.io/charts/ \
  --namespace kafka --create-namespace \
  --wait

echo "Installing StarRocks operator..."
# Chart was renamed upstream from "kube-starrocks-operator" to "operator".
helm upgrade --install starrocks-operator operator \
  --repo https://starrocks.github.io/starrocks-kubernetes-operator \
  --namespace starrocks --create-namespace \
  --wait

echo "All operators installed."

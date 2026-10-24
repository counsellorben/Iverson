$ErrorActionPreference = "Stop"

# CSR round 12 §3.6: kind-config.yaml pins kindest/node:v1.35.8, which was run with kind v0.33.0; an older
# kind is refused before anything touches the cluster.
$KindMin = [version]"0.33.0"
$KindVersion = ("$(kind version)" -split '\s+')[1]
if ([version](($KindVersion.TrimStart('v') -split '-')[0]) -lt $KindMin) {
  throw "kind $KindVersion is older than v$KindMin, the version kind-config.yaml's node image was run with; install kind v$KindMin or later and recreate the cluster"
}

# If running with KIND_EXPERIMENTAL_PROVIDER=podman (e.g. podman on WSL2), podman's default
# PidsLimit (2048) on the kind node container becomes the *entire* node's pids budget, which
# systemd then divides across every pod/container cgroup inside it — StarRocks BE alone needs
# more threads than its resulting share (as low as ~300) allows, crash-looping on startup with
# `std::system_error: Resource temporarily unavailable`. Fix once, before creating the cluster,
# by adding `pids_limit = -1` under a `[containers]` table in the podman containers.conf used
# by that environment — this is a podman-wide default, not something this script can set
# per-container. Not needed when using real Docker Desktop as the provider.

# CSR round-11 #19: the CNPG, Strimzi and StarRocks operator chart versions come from the stub
# Chart.yaml the cloud Terraform installs read (terraform/modules/operators/versions), which
# Dependabot keeps current, so kind and the cloud installs run the same operator builds. They are
# read before anything touches the cluster, so a missing pin stops the script here.
$OperatorPins = Join-Path $PSScriptRoot "../terraform/modules/operators/versions"
function Get-OperatorVersion([string]$Chart) {
  $list = helm dependency list $OperatorPins
  if ($LASTEXITCODE -ne 0) { throw "helm dependency list failed for $OperatorPins" }
  foreach ($line in $list) {
    $cols = "$line".Trim() -split '\s+'
    if ($cols[0] -eq $Chart) { return $cols[1] }
  }
  throw "no $Chart pin in $(Join-Path $OperatorPins 'Chart.yaml')"
}
$CnpgVersion = Get-OperatorVersion "cloudnative-pg"
$StrimziVersion = Get-OperatorVersion "strimzi-kafka-operator"
$StarRocksOperatorVersion = Get-OperatorVersion "operator"

# CSR round 12 §5: with no argument this script runs the whole fresh install. On an existing cluster the
# upgrade runs the platform steps one at a time (docs/runbooks/csr12-platform.md §3): `setup.ps1 --only
# <phase>` runs that phase alone. Each phase is a function below; the fresh install calls them in the
# same order, after the operators. The script declares no parameters, so PowerShell hands both words to
# $args ("-only" is accepted too).
$Phases = @("istio", "operator-namespaces", "ambient-labels")
$Only = ""
if ($args.Count -gt 0) {
  if ($args.Count -ne 2 -or $args[0] -notin @('--only', '-only') -or $Phases -notcontains $args[1]) {
    throw "usage: setup.ps1 [--only <phase>]   (phases: $($Phases -join ' '))"
  }
  $Only = $args[1]
}

function Install-Istio {
  Write-Host "Installing the Gateway API CRDs and Istio 1.31.2 (ambient)..."
  # CSR round 12 §4.1: Istio ambient carries every in-cluster hop over mutual TLS. Its charts are vendored
  # where the cloud Terraform installs them from (terraform/modules/operators/charts/istio; the source
  # tarball and its SHA-256 are in vendor-istio-charts.sh there). kind installs them with the smaller
  # requests in deploy/kind/istio/, so the laptop profile still fits its 4-CPU host. The Gateway API
  # standard CRDs carry kind's edge Gateway and the chart's routes; nothing else installs them on kind.
  # istio-cni chains itself into Calico's CNI configuration, so Calico comes first.
  $IstioCharts = Join-Path $PSScriptRoot "../terraform/modules/operators/charts/istio"
  $IstioValues = Join-Path $PSScriptRoot "istio"
  kubectl apply --server-side -f https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.6.3/standard-install.yaml
  helm upgrade --install istio-base (Join-Path $IstioCharts "base") `
    --namespace istio-system --create-namespace `
    --wait
  helm upgrade --install istiod (Join-Path $IstioCharts "istio-discovery") `
    --namespace istio-system `
    -f (Join-Path $IstioValues "istiod.yaml") `
    --wait
  helm upgrade --install istio-cni (Join-Path $IstioCharts "istio-cni") `
    --namespace istio-system `
    -f (Join-Path $IstioValues "istio-cni.yaml") `
    --wait
  helm upgrade --install ztunnel (Join-Path $IstioCharts "ztunnel") `
    --namespace istio-system `
    -f (Join-Path $IstioValues "ztunnel.yaml") `
    --wait
}

function Install-OperatorNamespacePolicies {
  Write-Host "Installing the operator-namespace NetworkPolicies (CSR round 12 §3.4)..."
  # Default-deny with explicit allows in cnpg-system, kafka, starrocks, istio-system, tigera-operator and
  # iverson-edge, from the chart the cloud Terraform installs too (modules/operators), with kind's
  # addresses from values-kind.yaml. Every namespace it governs must exist first: istio-system, which Istio
  # installs into, and iverson-edge, the gateway's namespace (not enrolled in the mesh).
  foreach ($ns in @("istio-system", "iverson-edge")) {
    kubectl create namespace $ns --dry-run=client -o yaml | kubectl apply -f -
  }
  $OperatorPolicies = Join-Path $PSScriptRoot "../terraform/modules/operators/charts/operator-namespace-policies"
  helm upgrade --install operator-namespace-policies $OperatorPolicies `
    --namespace kube-system `
    -f (Join-Path $OperatorPolicies "values-kind.yaml") `
    --wait
}

function Add-AmbientLabels {
  Write-Host "Enrolling iverson, cnpg-system, kafka and starrocks in the ambient mesh..."
  # CSR round 12 §4.1, §5: the four namespaces are labelled together. An enrolled pod's egress leaves on
  # the HBONE port 15008, so a release whose NetworkPolicies do not list 15008 loses every hop once its
  # namespace is enrolled. They are labelled when namespace iverson has no NetworkPolicy yet (a fresh
  # cluster: the chart brings its 15008 rules with its first install) or when its policies already list
  # 15008 (the release was upgraded first). Otherwise nothing is labelled, and the upgrade order applies:
  # helm upgrade the release, then run `--only ambient-labels`. Running pods are captured without a restart.
  $Policies = kubectl get networkpolicy -n iverson -o name
  $Ports = "$(kubectl get networkpolicy -n iverson -o jsonpath='{..port}')" -split '\s+'
  if ($Policies -and -not ($Ports -contains '15008')) {
    Write-Host "Not enrolled: the NetworkPolicies in namespace iverson do not list port 15008 yet. Run the release's helm upgrade first, then run this script with --only ambient-labels."
  } else {
    kubectl label namespace iverson cnpg-system kafka starrocks istio.io/dataplane-mode=ambient --overwrite
  }
}

if ($Only) {
  switch ($Only) {
    "istio" { Install-Istio }
    "operator-namespaces" { Install-OperatorNamespacePolicies }
    "ambient-labels" { Add-AmbientLabels }
  }
  exit 0
}

Write-Host "Installing Calico (kind's default CNI, kindnet, does not enforce NetworkPolicy)..."
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
helm upgrade --install calico tigera-operator `
  --repo https://docs.tigera.io/calico/charts `
  --version v3.32.2 `
  --namespace tigera-operator `
  --set installation.enabled=false `
  --set apiServer.enabled=false `
  --set goldmane.enabled=false `
  --set whisker.enabled=false `
  --wait
# The operator registers this CRD a few seconds after its pod is Ready, and `kubectl wait`
# fails immediately with NotFound on a resource that does not exist yet. Poll for existence first.
for ($i = 0; $i -lt 30; $i++) {
  kubectl get crd installations.operator.tigera.io *> $null
  if ($LASTEXITCODE -eq 0) { break }
  Start-Sleep -Seconds 2
}
kubectl wait --for=condition=Established crd/installations.operator.tigera.io --timeout=60s
helm upgrade --install calico tigera-operator `
  --repo https://docs.tigera.io/calico/charts `
  --version v3.32.2 `
  --namespace tigera-operator `
  --reset-values `
  --wait

Write-Host "Creating iverson namespace with restricted-baseline Pod Security Admission label..."
kubectl create namespace iverson --dry-run=client -o yaml | kubectl apply -f -
kubectl label namespace iverson pod-security.kubernetes.io/enforce=baseline --overwrite

Write-Host "Installing ingress-nginx..."
helm upgrade --install ingress-nginx ingress-nginx `
  --repo https://kubernetes.github.io/ingress-nginx `
  --version 4.12.8 `
  --namespace ingress-nginx --create-namespace `
  --set controller.hostPort.enabled=true `
  --set controller.service.type=ClusterIP `
  --wait

Write-Host "Installing CloudNativePG operator..."
helm upgrade --install cnpg cloudnative-pg `
  --repo https://cloudnative-pg.github.io/charts `
  --version $CnpgVersion `
  --namespace cnpg-system --create-namespace `
  --wait

Write-Host "Installing Strimzi operator..."
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
helm upgrade --install strimzi strimzi-kafka-operator `
  --repo https://strimzi.io/charts/ `
  --version $StrimziVersion `
  --namespace kafka --create-namespace `
  --set watchNamespaces="{iverson}" `
  --wait

Write-Host "Installing StarRocks operator..."
# Chart was renamed upstream from "kube-starrocks-operator" to "operator".
helm upgrade --install starrocks-operator operator `
  --repo https://starrocks.github.io/starrocks-kubernetes-operator `
  --version $StarRocksOperatorVersion `
  --namespace starrocks --create-namespace `
  --wait

Write-Host "Installing metrics-server..."
# Without it both HPAs report cpu: <unknown> and never restore a Deployment that was
# manually scaled to 0 — which is how the worker (and with it EnrichmentConsumer) sat
# at 0 replicas during the 2026-07-27 smoke test with no error anywhere. Also makes
# `kubectl top` work, which the laptop profile's capacity numbers depend on.
# --kubelet-insecure-tls is required on kind: kubelet serves a self-signed cert that
# metrics-server will otherwise reject.
helm upgrade --install metrics-server metrics-server `
  --repo https://kubernetes-sigs.github.io/metrics-server/ `
  --version 3.9.0 `
  --namespace kube-system `
  --set 'args={--kubelet-insecure-tls}' `
  --wait

Install-Istio
Install-OperatorNamespacePolicies
Add-AmbientLabels

Write-Host "All operators installed."
Write-Host "Next: deploy/kind/build-and-load-image.ps1 to build+load the app image, then helm upgrade --install --wait iverson . -f values-local.yaml -n iverson --kube-as-user=kubernetes-admin --kube-as-group=kubeadm:cluster-admins --kube-as-group=iverson-operators (the kube-as flags: CSR round 12's admission policy admits only the iverson-operators group to install or change the api, see docs/runbooks/operators-group.md; --wait: the post-upgrade StarRocks role revoke then runs once the new pods are ready, though a previous-release pod may still be running or terminating; see docs/runbooks/csr12-server.md §1)"
Write-Host "Note: TEI and Ollama run model images with their weights baked in. Before installing the chart, build and load them: deploy/kind/build-and-load-image.ps1 -ModelImages (optionally -Values <overlay>; -ClusterName applies)."

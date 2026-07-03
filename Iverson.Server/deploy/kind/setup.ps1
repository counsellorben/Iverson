$ErrorActionPreference = "Stop"

Write-Host "Installing Calico (kind's default CNI, kindnet, does not enforce NetworkPolicy)..."
kubectl create namespace tigera-operator --dry-run=client -o yaml | kubectl apply -f -
helm upgrade --install calico tigera-operator `
  --repo https://docs.tigera.io/calico/charts `
  --namespace tigera-operator `
  --wait

Write-Host "Creating iverson namespace with restricted-baseline Pod Security Admission label..."
kubectl create namespace iverson --dry-run=client -o yaml | kubectl apply -f -
kubectl label namespace iverson pod-security.kubernetes.io/enforce=baseline --overwrite

Write-Host "Installing ingress-nginx..."
helm upgrade --install ingress-nginx ingress-nginx `
  --repo https://kubernetes.github.io/ingress-nginx `
  --namespace ingress-nginx --create-namespace `
  --set controller.hostPort.enabled=true `
  --set controller.service.type=ClusterIP `
  --wait

Write-Host "Installing CloudNativePG operator..."
helm upgrade --install cnpg cloudnative-pg `
  --repo https://cloudnative-pg.github.io/charts `
  --namespace cnpg-system --create-namespace `
  --wait

Write-Host "Installing Strimzi operator..."
helm upgrade --install strimzi strimzi-kafka-operator `
  --repo https://strimzi.io/charts/ `
  --namespace kafka --create-namespace `
  --set featureGates="+KafkaNodePools\,+UseKRaft" `
  --wait

Write-Host "Installing StarRocks operator..."
helm upgrade --install starrocks-operator kube-starrocks-operator `
  --repo https://starrocks.github.io/starrocks-kubernetes-operator `
  --namespace starrocks --create-namespace `
  --wait

Write-Host "All operators installed."

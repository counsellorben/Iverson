{{/*
The rules every governed namespace gets (CSR round 12 §3.4, "all" row): DNS to kube-dns and the API
server on 443/6443 from apiServerCidrs. 15008, Istio's HBONE port, sits beside every listed port
(§4.2): in ambient, a pod's egress to an in-mesh destination leaves for <dst>:15008.
*/}}
{{- define "onp.commonEgress" -}}
- to: [{ namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: kube-system } }, podSelector: { matchLabels: { k8s-app: kube-dns } } }]   # DNS
  ports: [{ protocol: UDP, port: 53 }, { protocol: TCP, port: 53 }, { protocol: TCP, port: 15008 }]
- to:   # Kubernetes API server
  {{- range .Values.apiServerCidrs }}
  - ipBlock: { cidr: {{ . }} }
  {{- end }}
  ports: [{ protocol: TCP, port: 443 }, { protocol: TCP, port: 6443 }, { protocol: TCP, port: 15008 }]
{{- end -}}

{{/*
kube-score matches a NetworkPolicy against the pods in the same render; this chart renders none (the
operators' own charts do), so it reports every policy as matching nothing.
*/}}
{{- define "onp.metadata" -}}
namespace: {{ .ns }}
annotations:
  kube-score/ignore: networkpolicy-targets-pod
{{- end -}}

{{/*
Resolve the active embedding-model entry from global.embeddingModels, as YAML for `fromYaml`.
One helper rather than an inline lookup per subchart: api and worker must not be able to
implement the resolution differently. A name matching no entry emits nothing, which fromYaml
yields as an empty dict — handled by the `default` on Embeddings__ModelId at the call sites.
*/}}
{{- define "iverson.activeEmbeddingModel" -}}
{{- $name := .Values.global.activeEmbeddingModel -}}
{{- range .Values.global.embeddingModels -}}
{{- if eq .name $name -}}
{{- toYaml . -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
Emit the Embeddings__* env entries for the active embedding model, resolved via
iverson.activeEmbeddingModel. One helper rather than a copy per subchart: api and worker must
not be able to implement the emission differently. Callers pipe this through nindent, so the
body carries no leading indentation of its own.
*/}}
{{- define "iverson.embeddingEnv" -}}
{{- $active := include "iverson.activeEmbeddingModel" . | fromYaml -}}
- name: Embeddings__ModelId
  value: {{ $active.name | default .Values.global.activeEmbeddingModel | quote }}
{{- if hasKey $active "documentPrefix" }}
- name: Embeddings__DocumentPrefix
  value: {{ $active.documentPrefix | quote }}
{{- end }}
{{- if hasKey $active "queryPrefix" }}
- name: Embeddings__QueryPrefix
  value: {{ $active.queryPrefix | quote }}
{{- end }}
{{- range $i, $m := .Values.global.embeddingModels }}
- name: Embeddings__Models__{{ $i }}__Name
  value: {{ $m.name | quote }}
- name: Embeddings__Models__{{ $i }}__BaseUrl
  value: "http://{{ $.Release.Name }}-tei-{{ $m.slug }}:8080"
{{- end }}
{{- end -}}

{{/*
The global embedding fallback: the first tei entry's headless Service, on the container port.
*/}}
{{- define "iverson.embeddingBaseUrl" -}}
{{- $first := index .Values.global.embeddingModels 0 -}}
http://{{ .Release.Name }}-tei-{{ $first.slug }}:8080
{{- end -}}

{{/*
The DNS egress rule every NetworkPolicy with policyTypes Egress needs: kube-dns pods only, on
53. Matched by the kube-dns Service's own selector (k8s-app=kube-dns), which EKS's network-policy
agent requires for traffic sent to a Service address. networkPolicy.dnsAnyDestination is the
first-deploy fallback: any destination on 53.
*/}}
{{- define "iverson.dnsEgress" -}}
{{- if .Values.networkPolicy.dnsAnyDestination -}}
- to: []   # DNS (fallback: networkPolicy.dnsAnyDestination)
{{- else -}}
- to: [{ namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: kube-system } }, podSelector: { matchLabels: { k8s-app: kube-dns } } }]   # DNS
{{- end }}
  ports: [{ protocol: UDP, port: 53 }, { protocol: TCP, port: 53 }, { protocol: TCP, port: 15008 }]
{{- end -}}

{{/*
The Kubernetes API server egress rule for operator-managed pods that call it directly (CNPG,
Strimzi): networkPolicy.apiServerCidrs on 443 and 6443. Both ports: kind/kubeadm serve the API on
6443, and engines that match after the Service address is rewritten see the endpoint's port.
networkPolicy.apiServerAnyDestination is the first-deploy fallback: any destination.
*/}}
{{- define "iverson.apiServerEgress" -}}
{{- if .Values.networkPolicy.apiServerAnyDestination -}}
- to: []   # Kubernetes API server (fallback: networkPolicy.apiServerAnyDestination)
{{- else -}}
- to:   # Kubernetes API server
  {{- range .Values.networkPolicy.apiServerCidrs }}
  - ipBlock: { cidr: {{ . }} }
  {{- end }}
{{- end }}
  ports: [{ protocol: TCP, port: 443 }, { protocol: TCP, port: 6443 }, { protocol: TCP, port: 15008 }]
{{- end -}}

{{/*
CSR round-11 #4: whether the edge reaches a pod over TLS ("backendTls"), from the pod's Ingress
class. Called with (dict "className" <ingress class> "trustedRoot" <appgwTrustedRootCertificate>) by
the api and Authentik subcharts and by the parent's Prometheus config; prints "true" or nothing.
nginx, alb and gce encrypt to any backend certificate. Application Gateway verifies it, so it needs
the chart CA installed as a trusted root first: true only once the root's name is set. Any other
class gets no backend-protocol settings from this chart, so its edge would keep speaking plaintext
to a TLS port: false, and the hop stays as before.
*/}}
{{- define "iverson.backendTls" -}}
{{- if has .className (list "nginx" "alb" "gce") -}}
true
{{- else if and (eq .className "azure-application-gateway") .trustedRoot -}}
true
{{- end -}}
{{- end -}}

{{/*
CSR round-11 #4, #6: the Ingress annotations this chart sets per class, as YAML for `fromYaml`;
callers merge them over the profile's own annotations, so a key appears once and the chart's value
wins. Argument: (dict "className" "backendTls" "grpc" "trustedRoot" "host" "probePort" "probePath").
- Port 80, on every call: gce turns HTTP off; Application Gateway answers it only with a redirect.
- backendTls (iverson.backendTls of the pod behind the Ingress) sets the edge → pod protocol and,
  where the health check does not follow it, the health check: on alb the protocol, and the path
  except for gRPC (ALB's gRPC check has its own default); on Application Gateway the trusted root,
  the host name the pod certificate carries (host), and the probe port and path.
- grpc marks the API's gRPC Ingress: nginx speaks GRPC to it, GRPCS once backendTls is on.
*/}}
{{- define "iverson.edgeAnnotations" -}}
{{- $a := dict -}}
{{- if eq .className "gce" -}}
{{- $_ := set $a "kubernetes.io/ingress.allow-http" "false" -}}
{{- else if eq .className "azure-application-gateway" -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/ssl-redirect" "true" -}}
{{- end -}}
{{- if and (eq .className "nginx") .grpc -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/backend-protocol" (ternary "GRPCS" "GRPC" .backendTls) -}}
{{- else if .backendTls -}}
{{- if eq .className "nginx" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/backend-protocol" "HTTPS" -}}
{{- else if eq .className "alb" -}}
{{- $_ := set $a "alb.ingress.kubernetes.io/backend-protocol" "HTTPS" -}}
{{- $_ := set $a "alb.ingress.kubernetes.io/healthcheck-protocol" "HTTPS" -}}
{{- if not .grpc -}}
{{- $_ := set $a "alb.ingress.kubernetes.io/healthcheck-path" .probePath -}}
{{- end -}}
{{- else if eq .className "azure-application-gateway" -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/backend-protocol" "https" -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/appgw-trusted-root-certificate" .trustedRoot -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/backend-hostname" .host -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/health-probe-hostname" .host -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/health-probe-port" (toString .probePort) -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/health-probe-path" .probePath -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
CSR round 12 §4.5: kind's edge, the Istio Gateway that deploy/kind/edge-gateway.yaml creates (setup.sh,
setup.ps1). meshEdge prints "true" where global.gateway.class is istio: the edge is then a mesh client,
so it reaches the release over mutual TLS as the principal edgeGatewayPrincipal, the service account
Istio generates for the Gateway (<name>-istio).
*/}}
{{- define "iverson.meshEdge" -}}
{{- if eq (dig "gateway" "class" "" .Values.global) "istio" -}}
true
{{- end -}}
{{- end -}}

{{- define "iverson.edgeGatewayNamespace" -}}
iverson-edge
{{- end -}}

{{- define "iverson.edgeGatewayName" -}}
edge
{{- end -}}

{{- define "iverson.edgeGatewayPrincipal" -}}
cluster.local/ns/{{ include "iverson.edgeGatewayNamespace" . }}/sa/{{ include "iverson.edgeGatewayName" . }}-istio
{{- end -}}

{{- /* iverson.backendTls for this chart's own Ingress class and trusted-root name, and the edge gateway. */ -}}
{{- define "api.backendTls" -}}
{{- include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate "gatewayClass" (dig "gateway" "class" "" .Values.global)) -}}
{{- end -}}

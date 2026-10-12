{{- /* iverson.backendTls for this chart's own Ingress class and trusted-root name. */ -}}
{{- define "authentik.backendTls" -}}
{{- include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate) -}}
{{- end -}}

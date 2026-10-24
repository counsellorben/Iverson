{{- /* iverson.backendTls for this chart's own Ingress class and trusted-root name. */ -}}
{{- define "authentik.backendTls" -}}
{{- include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate) -}}
{{- end -}}
{{- /* CSR round-12 §2.6: the iss of this cluster's service-account tokens, which the orchestrator and
   admin-automation pins compare byte for byte. One required check for both templates that use it. */ -}}
{{- define "authentik.serviceAccountIssuer" -}}
{{- required "authentik.serviceAccountIssuer is required: the iss of this cluster's service-account tokens (https://kubernetes.default.svc.cluster.local on kind; on a cloud, the Terraform output service_account_issuer)" .Values.serviceAccountIssuer -}}
{{- end -}}

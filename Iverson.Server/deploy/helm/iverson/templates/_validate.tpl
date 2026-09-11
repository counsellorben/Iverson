{{/*
Fatal guard against deploying a cloud/https profile with an unedited placeholder
sentinel still in place (CSR remediation round 2, finding #3).

Scope: this fires ONLY when global.externalScheme is "https" — that is, on
values-aws.yaml / values-azure.yaml / values-gcp.yaml or a custom cloud overlay
that sets externalScheme to https. values.yaml, values-local.yaml and
values-laptop.yaml all default to externalScheme "http" and legitimately use
the "iverson.local"/"iverson.example.com"-shaped hostnames for local dev, so
they must never trip this guard — gating on http vs. https, not on the
hostname string alone, is what keeps that distinction correct.

Two sentinels are checked:
  1. global.ingressHost still equal to the shipped placeholder
     "iverson.example.com" — a cloud profile that never replaced it.
  2. Any ingress annotation on api/authentik/adminUi shaped like
     "<SOME_PLACEHOLDER>" (e.g. the ALB "<ACM_CERT_ARN>" sentinel in
     values-aws.yaml) — a cert reference that was never filled in.
A cert placeholder or example hostname left in place on an https profile is a
deploy with no working edge TLS, so both cases fail the render rather than
silently producing a broken Ingress.
*/}}
{{- define "iverson.validateNoPlaceholders" -}}
{{- if eq (dig "externalScheme" "http" .Values.global) "https" }}
{{- if eq (dig "ingressHost" "" .Values.global) "iverson.example.com" }}
{{- fail (printf "global.ingressHost is still the shipped placeholder %q — set it to the real external hostname before deploying a cloud/https profile (CSR round-2 finding #3)." (dig "ingressHost" "" .Values.global)) }}
{{- end }}
{{- $placeholderRe := "^<.*>$" }}
{{/*
dig's final argument must be a plain map[string]interface{} — .Values itself is Helm's
chartutil.Values wrapper type, and passing it straight to dig panics with an "interface
conversion" error (confirmed empirically). .Values.api/.Values.authentik/.Values.adminUi
are already plain maps one level down, so extract each (defaulting a missing/disabled
subchart to an empty dict) before digging into it.
*/}}
{{- $ingressBlocks := dict "api.ingress" (dig "ingress" dict (default dict .Values.api)) "authentik.ingress" (dig "ingress" dict (default dict .Values.authentik)) "adminUi.ingress" (dig "ingress" dict (default dict .Values.adminUi)) }}
{{- range $label, $ing := $ingressBlocks }}
{{- range $k, $v := (dig "annotations" dict $ing) }}
{{- if and (kindIs "string" $v) (regexMatch $placeholderRe $v) }}
{{- fail (printf "%s.annotations[%s] is still the placeholder %q — replace it with a real value before deploying a cloud/https profile (CSR round-2 finding #3)." $label $k $v) }}
{{- end }}
{{- end }}
{{- end }}
{{- end }}
{{- end -}}

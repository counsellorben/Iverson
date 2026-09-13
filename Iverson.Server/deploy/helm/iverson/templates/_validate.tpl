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
{{- /*
CSR round-3 finding #11 (part 1): api.ingress.host used to duplicate
global.ingressHost out of lockstep with no enforcement. Fixed by removing the
duplicate value entirely — charts/api/templates/ingress.yaml now templates
directly off global.ingressHost (see that file's comment), the same
single-source-of-truth pattern admin-ui's Ingress already used — so there is
nothing left to drift and no equality check is needed here.
*/}}
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
{{- /*
CSR round-3 finding #11 (part 2): azure/gcp both set a real tlsSecretName (e.g.
"iverson-api-tls") that this chart never creates — it must already exist as a Secret
in the release namespace (provisioned out-of-band, e.g. cert-manager or a manual
cert import) or the rendered Ingress's `tls:` block references nothing and TLS
silently doesn't work. Reuses $ingressBlocks from part 2 above rather than
re-deriving api/authentik/adminUi.ingress a second time.

lookup only queries a live API server: `helm template`/`--dry-run` has no cluster to
ask and ALWAYS returns an empty result regardless of the real state, which would
otherwise make this check fail every valid values-azure.yaml/values-gcp.yaml render
(both legitimately set a real tlsSecretName). `.Release.IsInstall` does NOT
distinguish this — confirmed empirically it is `true` under plain `helm template`
too (Helm simulates a fresh install by default), so it cannot gate this check.
Instead, probe for live cluster access the same way `lookup` itself is documented
to behave: look up a namespace ("kube-system") that exists on every real
Kubernetes cluster but which `lookup` — per Helm's own docs — always resolves to
an empty map under `helm template`/`--dry-run` regardless of target, confirmed
empirically in this environment. The placeholder checks above need no such gate:
they only ever inspect rendered .Values content, never a live lookup, so they
behave identically under `helm template` and a real install/upgrade.
*/}}
{{- if lookup "v1" "Namespace" "" "kube-system" }}
{{- range $label, $ing := $ingressBlocks }}
{{- $tlsName := dig "tlsSecretName" "" $ing }}
{{- if $tlsName }}
{{- if not (lookup "v1" "Secret" $.Release.Namespace $tlsName) }}
{{- fail (printf "%s.tlsSecretName %q does not resolve to an existing Secret in namespace %q — create it (e.g. via cert-manager or your cloud's certificate-import flow) before this install/upgrade completes, or the Ingress has no working TLS (CSR round-3 finding #11)." $label $tlsName $.Release.Namespace) }}
{{- end }}
{{- end }}
{{- end }}
{{- end }}
{{- end }}
{{- end -}}

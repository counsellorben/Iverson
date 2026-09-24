# Fix the api Ingress on nginx profiles — design

## Goal

Make main's api Ingress (`charts/api/templates/ingress.yaml`) actually work on `local`/`laptop` (ingress-nginx), instead of 400ing every request, and remove the one route on it that no longer needs to be there.

## Background (measured)

Confirmed live on a fresh kind cluster running the `local` profile: every request through this Ingress — the six gRPC service paths and the legacy `/v1/traces` path alike — gets `400 An HTTP/1.x request was sent to an HTTP/2 only endpoint` from Kestrel. Port 8080 is `Http2`-only (h2c); no chart value sets `nginx.ingress.kubernetes.io/backend-protocol: GRPC` outside AWS's `values-aws.yaml:91` (`backend-protocol-version: GRPC`, the ALB-specific annotation), so ingress-nginx forwards plain HTTP/1.1 upstream, which Kestrel rejects outright.

This Ingress's purpose, per the NetworkPolicy design (`templates/networkpolicies.yaml:21-37`) and the threat model (`docs/security/tma.md:32-39`), is external gRPC reachability for the SDK clients (Python/TS/Go/Java/.NET) from outside the cluster, without kubeconfig/port-forward access. Of the six paths, four (`ObjectMappingService`, `ObjectPersistenceService`, `ObjectRetrievalService`, `ObjectSearchService`) are native-gRPC-only; two (`TenantLifecycleGrpcService`, `TenantAdminGrpcService`) also enable grpc-web (`Program.cs:721-722`), though nothing currently calls them that way — `TenantAdminPage.tsx` is a `Coming soon` stub, and the one real caller of `TenantLifecycleGrpcService` (`Iverson.LoadTest`) uses a native channel. No live azure or gcp deployment exists today, so nothing currently depends on external gRPC access working on either.

The seventh path, `/v1/traces`, is dead weight: the traces-relay-admin-listener design (`docs/specs/2026-09-23-traces-relay-admin-listener-design.md`) already moved the admin console's span export onto the admin-api Ingress (port 8081, `Http1`), on every profile the `adminConsoleEnabled` gate allows. Nothing needs `/v1/traces` on this Ingress anymore.

**Why the fix is more than one annotation.** Probed directly (see Verified assumptions): `backend-protocol: GRPC` only governs the nginx→backend leg. On a plaintext (no-TLS) Ingress, ingress-nginx's client-facing listener never accepts a real gRPC client's raw HTTP/2 connection preface at all — it gets rejected as unparseable before any routing happens, regardless of the annotation. A real gRPC client needs TLS with ALPN negotiating `h2` at the ingress. `local`/`laptop` currently have no TLS on this Ingress at all (`values-local.yaml:4-8`, explicit "local dev has no cert-manager/ACME issuer" comment); `azure`/`gcp` already do (`tlsSecretName: "iverson-api-tls"`).

## Decision

- **`local`/`laptop`:** provision a self-signed dev TLS cert (CN/SAN = `global.ingressHost`, i.e. `iverson.local`) for this Ingress, and add `nginx.ingress.kubernetes.io/backend-protocol: "GRPC"`. External gRPC clients connect over `https://iverson.local` with certificate verification disabled, matching normal local-dev practice.
- **Remove `/v1/traces`** from this Ingress on every profile (it's a plain JSON route; mixing it into a `backend-protocol: GRPC` Ingress was never verified as safe, and it's unnecessary now regardless).
- **`azure`/`gcp`:** leave the gRPC brokenness as a documented known issue — no live consumer, and AGIC/GCE's gRPC-passthrough mechanisms can't be tested in this environment. A one-line comment points at this design for whoever stands one up.
- **AWS:** untouched; its ALB GRPC target-group mode already works.

## Design

1. **`charts/api/templates/ingress.yaml`:**
   - Add a second annotation inside the existing `{{- if eq .Values.ingress.className "nginx" }}` branch (alongside the existing `configuration-snippet`), at the same level: `nginx.ingress.kubernetes.io/backend-protocol: "GRPC"`.
   - Delete the `/v1/traces` path block (currently `:95-101`) entirely.
2. **`values-local.yaml`** (`api.ingress`, `:116`) **and `values-laptop.yaml`** (`api.ingress`, `:97`): `tlsSecretName: ""` → `tlsSecretName: "iverson-api-tls"` — the same name `values-azure.yaml`/`values-gcp.yaml` already use for this Ingress.
3. **`values-local.yaml`** header comment (`:4-8`), which currently asserts this Ingress "renders without a `tls:` block" and is reached over plain HTTP: rewritten to say the api Ingress now has a self-signed dev cert for gRPC clients specifically, while everything else on this profile stays plain HTTP as before — `admin-api`/`authentik` because they're on different hostnames with no certificate, `admin-ui` because of the new `ssl-redirect: "false"` annotation on its own Ingress (item 6) despite sharing `iverson.local`'s certificate — and `global.externalScheme` is untouched.
4. **`values-azure.yaml` / `values-gcp.yaml`:** one-line comment next to their existing `tlsSecretName: "iverson-api-tls"` noting the gRPC paths still 400 pending an AGIC/GCE `backend-protocol` equivalent, pointing at this design.
5. **`deploy/kind/setup.sh`:** immediately after the `iverson` namespace is created (`:55`), idempotently generate the self-signed cert and create the `iverson-api-tls` Secret in that namespace, before `helm upgrade --install iverson` ever runs:

   ```bash
   if ! kubectl get secret iverson-api-tls -n iverson >/dev/null 2>&1; then
     echo "Generating a self-signed dev cert for the api Ingress (iverson.local)..."
     openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
       -keyout /tmp/iverson-api-tls.key -out /tmp/iverson-api-tls.crt \
       -subj "/CN=iverson.local" -addext "subjectAltName=DNS:iverson.local"
     kubectl create secret tls iverson-api-tls -n iverson \
       --cert=/tmp/iverson-api-tls.crt --key=/tmp/iverson-api-tls.key
     rm -f /tmp/iverson-api-tls.key /tmp/iverson-api-tls.crt
   fi
   ```

   `deploy/kind/setup.ps1` is left untouched — it's already separately drifted (no version pins, no snippet-annotation setting) and outside what either investigation covered.
6. **`charts/admin-ui/templates/ingress.yaml`:** inside its existing `{{- if eq .Values.ingress.className "nginx" }}` branch (the same one guarding its security-header `configuration-snippet`), add one more conditional annotation:

   ```yaml
   {{- if ne .Values.global.externalScheme "https" }}
   nginx.ingress.kubernetes.io/ssl-redirect: "false"
   {{- end }}
   ```

   ingress-nginx decides the HTTP→HTTPS redirect per *hostname*, not per Ingress. The admin-ui Ingress shares `iverson.local` with the api Ingress and has no `tls:` block of its own; once the api Ingress carries a certificate for that host, ingress-nginx would otherwise 308-redirect every plain-HTTP request to `iverson.local/admin…` to `https`, which breaks the console (Authentik's registered callback URL is `http://…/admin/callback`, strict-matched; the API's CORS allow-list and `API_BASE_URL` are both `http://`). This annotation renders only where the profile's scheme isn't already https, so it's a no-op on `azure`/`gcp`/`aws` and on `laptop` (which doesn't render admin-ui at all).

## Out of scope

- No change to `global.externalScheme` (stays `"http"` on `local`/`laptop`), or to `admin-api`'s or `authentik`'s Ingress (different hostnames, no certificate, so ingress-nginx's host-based TLS redirect never reaches them) — they're plain HTTP/JSON. `admin-ui`'s Ingress gets one new annotation (Design item 6) purely to stay off the redirect the api Ingress's new certificate would otherwise trigger on their shared hostname; nothing else about its behavior changes.
- No AGIC/GCE-specific fix for `azure`/`gcp` — deferred, documented, no live consumer.
- No change to which of the six gRPC services enable grpc-web — orthogonal to this fix.

## Testing and verification

1. **Render check** (all five profiles, kubeconform): `local`/`laptop`'s rendered api Ingress carries `nginx.ingress.kubernetes.io/backend-protocol: "GRPC"` and a `tls:` block referencing `iverson-api-tls`, and `local`'s rendered `iverson-admin-ui` Ingress carries `ssl-redirect: "false"` (absent on every other profile); no profile's rendered `iverson-api` Ingress contains a `/v1/traces` path (`iverson-admin-api`'s render is untouched and keeps it wherever it already renders). `azure`/`gcp`'s rendered `iverson-api` Ingress differs from HEAD only by the removed `/v1/traces` path plus the new comment (comments don't render).
2. **Live gRPC check on kind:** deploy the `local` profile, confirm the api pod reaches its listening state, then run a real external gRPC call (`grpcurl` or equivalent) against `https://iverson.local` with certificate verification disabled and confirm a genuine `grpc-status`/response — not a 400. Also confirm `http://iverson.local/admin` still returns the console (`200`), not a redirect to `https` (`308`), after the api Ingress gains its certificate.

## Known issues / accepted as out of scope

- **`azure`/`gcp` gRPC paths remain broken** on this Ingress. TLS already exists there; AGIC/GCE need their own gRPC-passthrough configuration, not researched or verified here. No live deployment depends on it today.
- **External SDK consumers of `local`/`laptop` now need `https://` with verification disabled**, where they previously (in principle, once fixed) would have used plain HTTP. This is a real behavior change for anyone relying on this Ingress externally, called out here rather than silently introduced.
- **AWS ALB's GRPC target-group behavior for a non-gRPC POST** was already moot before this design (the traces-relay design's own known-issue note) and stays moot now that `/v1/traces` is removed from every profile's api Ingress, AWS included.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | `backend-protocol: GRPC` alone, without TLS, does not make ingress-nginx forward a real external gRPC client's request | Probed live: a real gRPC client (grpcurl) against a minimal h2c-only Kestrel service behind ingress-nginx 4.12.8/controller 1.12.8 with `backend-protocol: GRPC` set, plain HTTP — the raw HTTP/2 client preface is rejected before any request-line parsing (`"-" 400`, empty upstream fields in the access log) |
| 2 | TLS + ALPN `h2` at the ingress, plus `backend-protocol: GRPC`, correctly proxies real gRPC to an h2c backend | Probed live: `curl --http2` with matching SNI, `content-type: application/grpc`, through the same setup, TLS added — got back `HTTP/2 200`, `content-type: application/grpc`, a genuine `grpc-status`/`grpc-message` from the Kestrel backend |
| 3 | `local`/`laptop` currently have no TLS on this Ingress; `azure`/`gcp` already do, under the name `iverson-api-tls` | `values-local.yaml:116`, `values-laptop.yaml:97` (`tlsSecretName: ""`); `values-azure.yaml:94`, `values-gcp.yaml:102` (`tlsSecretName: "iverson-api-tls"`) |
| 4 | The Ingress template's `tls:` block derives its host from `global.ingressHost` automatically — no separate hosts list to keep in sync | `charts/api/templates/ingress.yaml`: `{{- if .Values.ingress.tlsSecretName }} tls: - hosts: [{{ .Values.global.ingressHost \| quote }}] secretName: ... {{- end }}` |
| 5 | Neither `local` nor `laptop` overrides `global.ingressHost` away from `values.yaml`'s `"iverson.local"` default | `command grep -n ingressHost values-local.yaml values-laptop.yaml` — no hits in either file |
| 6 | Setting `tlsSecretName` on local/laptop doesn't trip the chart's https-only validation guard | `templates/_validate.tpl:58`: `{{- if eq (dig "externalScheme" "http" .Values.global) "https" }}` — the whole placeholder/cert-sentinel guard is scoped to `externalScheme == "https"`, which `local`/`laptop` never set; no inverse check exists that fires on a real `tlsSecretName` alone |
| 7 | Removing `/v1/traces` from this Ingress breaks no existing automated test | `command grep -rln 'v1/traces'` across the repo: hits are `Iverson.Api/Program.cs` and three C# test files (`AuthenticationPipelineTests.cs`, `AdminConsoleCorsPipelineTests.cs`, `TracesRelayEndpointTests.cs`) — all server-side tests of the relay endpoint itself, none assert this Helm template's rendered path list |
| 8 | No other Ingress/values comment describes this Ingress's `/v1/traces` path in a way that would go stale | `charts/api/values.yaml:43-46`'s `/v1/traces` mention is about the *admin-api* Ingress gate (`adminConsoleEnabled`), a different template — it stays correct and untouched |
| 9 | The nginx-specific `backend-protocol` annotation doesn't collide with anything already set | `command grep -rn backend-protocol`: only hit is AWS's distinct ALB annotation `backend-protocol-version` (`values-aws.yaml:91`) — no existing use of the nginx-specific key anywhere |
| 10 | `openssl` is a new dependency for `deploy/kind/setup.sh` (not already assumed) | `command grep -rn openssl deploy/kind/*.sh deploy/scripts/*.sh docs/runbooks/*.md` — no existing hits; confirmed present in this dev environment, but this is genuinely a new prerequisite the script will require |
| 11 | A self-signed cert needs a SAN, not just a CN, to satisfy modern TLS clients | Confirmed empirically during probing: a CN-only cert triggered ingress-nginx's "does not contain a Common Name or Subject Alternative Name" warning and fell back to its own default certificate; adding `-addext "subjectAltName=DNS:..."` resolved it |
| 12 | Kubernetes accepts an Ingress whose `tls.secretName` doesn't yet exist at apply time (ordering safety net) | Standard, well-established Kubernetes API admission behavior for core `Ingress` objects — no admission webhook validates Secret existence; only the TLS handshake for that host fails until the Secret appears. Design already sequences secret creation before `helm upgrade --install` regardless |
| 13 | ingress-nginx's HTTP→HTTPS redirect is decided per hostname, not per Ingress — so putting a certificate on the api Ingress alone would redirect admin-ui's plain-HTTP traffic too unless `ssl-redirect: "false"` is added there | Probed live and by render: applying `nginx.ingress.kubernetes.io/ssl-redirect: "false"` to `local`'s admin-ui Ingress restored `/admin` from a `308` redirect back to `200` while the api Ingress's own paths stayed on `https`; render-confirmed the annotation appears only on `local`'s admin-ui Ingress (absent on `laptop`, which has no admin-ui, and on `aws`/`azure`/`gcp`, whose admin-ui Ingress isn't nginx-class) |

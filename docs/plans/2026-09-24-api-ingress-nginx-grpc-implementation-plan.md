# Fix the api Ingress on nginx profiles — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-24-api-ingress-nginx-grpc-design.md` (commit SHA: `74da51c354bee2ce26db0180d282690225288a68`)

**Goal:** Make main's api Ingress (`charts/api/templates/ingress.yaml`) actually work on `local`/`laptop` (ingress-nginx) instead of 400ing every request, and remove the one route on it that no longer needs to be there.

**Architecture:** Two chart-level changes (the api Ingress's annotation + path list, and a one-annotation fix to admin-ui's Ingress so it doesn't get swept into the same host's new TLS redirect) plus one dev-tooling change (a self-signed cert generated in `deploy/kind/setup.sh`). No server code changes.

**Tech stack:** Helm 3, ingress-nginx 4.12.8/controller 1.12.8, kind, kubeconform, `openssl`, `grpcurl`.

---

## Global Constraints

- **Branch from LOCAL `main`, not `origin/main`.** Local `main` is well ahead of `origin/main`.
- **Never push, and never merge into `main`.** Integration is the user's decision.
- **Commits:** subjects are lowercase imperative with no type prefix, the body says why, and each ends with the executing agent's `Co-Authored-By:` trailer. Stage files by name, never `git add -A`.
- **Before every `helm template`,** run `helm dependency update .` in `Iverson.Server/deploy/helm/iverson`; otherwise packaged `.tgz` subcharts render stale.

## File Structure

- **Modify** `Iverson.Server/deploy/helm/iverson/charts/api/templates/ingress.yaml`: the nginx annotation block (`:25-26`) and the `/v1/traces` path block (`:95-101`).
- **Modify** `Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates/ingress.yaml`: its nginx annotation block (`:37-38`).
- **Modify** `Iverson.Server/deploy/helm/iverson/values-local.yaml`: header comment (`:4-8`) and `api.ingress.tlsSecretName` (`:116`).
- **Modify** `Iverson.Server/deploy/helm/iverson/values-laptop.yaml`: `api.ingress.tlsSecretName` (`:97`).
- **Modify** `Iverson.Server/deploy/helm/iverson/values-azure.yaml` (`:94`) and `values-gcp.yaml` (`:102`): one-line known-issue comment.
- **Modify** `Iverson.Server/deploy/kind/setup.sh`: after namespace creation (`:55-56`), add the cert-generation block.

## Inherited from spec

The following were verified by `thorough-brainstorming`/critical-design-review at spec-write time and are NOT re-verified here:

1. `backend-protocol: GRPC` alone, without TLS, does not make ingress-nginx forward a real external gRPC client's request (probed live against a minimal h2c Kestrel service).
2. TLS + ALPN `h2` at the ingress, plus `backend-protocol: GRPC`, correctly proxies real gRPC to an h2c backend (probed live, real `grpc-status`/`grpc-message` returned).
3. `local`/`laptop` currently have no TLS on the api Ingress; `azure`/`gcp` already do, under the name `iverson-api-tls`.
4. The Ingress template's `tls:` block derives its host from `global.ingressHost` automatically.
5. Neither `local` nor `laptop` overrides `global.ingressHost` away from `"iverson.local"`.
6. Setting `tlsSecretName` on local/laptop doesn't trip the chart's https-only validation guard (`_validate.tpl:58`, scoped to `externalScheme == "https"`).
7. Removing `/v1/traces` from this Ingress breaks no existing automated test.
8. No other Ingress/values comment describes this Ingress's `/v1/traces` path in a way that would go stale.
9. The nginx-specific `backend-protocol` annotation doesn't collide with anything already set.
10. `openssl` is a new dependency for `deploy/kind/setup.sh` (not already assumed).
11. A self-signed cert needs a SAN, not just a CN, to satisfy modern TLS clients.
12. Kubernetes accepts an Ingress whose `tls.secretName` doesn't yet exist at apply time.
13. ingress-nginx's HTTP→HTTPS redirect is decided per hostname, not per Ingress — probed and render-confirmed that `ssl-redirect: "false"` on admin-ui's Ingress fixes it, scoped to `local` only (absent on `laptop`, which has no admin-ui, and on `aws`/`azure`/`gcp`, whose admin-ui Ingress isn't nginx-class).

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Path / text | `ingress.yaml`'s nginx annotation block (`:25-26`) and `/v1/traces` block (`:95-101`) match exactly | Read at HEAD `74da51c3` |
| 2 | Path / text | `admin-ui/templates/ingress.yaml`'s nginx branch opens at `:37` | `command grep -n 'className "nginx"'` → `:37` |
| 3 | Path / text | `values-local.yaml` header comment (`:4-8`) and `tlsSecretName: ""` (`:116`) match exactly | Read at HEAD |
| 4 | Path / text | `values-laptop.yaml` `tlsSecretName: ""` at `:97` | Read at HEAD |
| 5 | Path / text | `values-azure.yaml:94` / `values-gcp.yaml:102` both `tlsSecretName: "iverson-api-tls"` | Read at HEAD |
| 6 | Path / text | `setup.sh` namespace-creation lines are at exactly `:55-56` | Read at HEAD |
| 7 | Command | `deploy/kind/kind-config.yaml` maps host 8080→container 80, 8443→container 443; `build-and-load-image.sh` accepts `--dockerfile PATH --image-name NAME` (defaults `Iverson.Server/Iverson.Api/Dockerfile` / `iverson-api`) | Read both files |
| 8 | Code validity | `kubectl create secret tls NAME --cert=… --key=…` is the correct flag shape | `kubectl create secret tls --help` |
| 9 | Command | `helm dependency update .` is the right refresh command for this chart | `Chart.yaml:7` has a `dependencies:` block |
| 10 | Command | `openssl req -x509 -newkey rsa:2048 -nodes -days N -keyout … -out … -subj … -addext "subjectAltName=DNS:…"` runs cleanly | Ran it: exit 0, valid cert produced |
| 11 | Consumer impact (totality) | Ollama/tei-embed are StatefulSets; Prometheus/Jaeger are Deployments, for the scale-down step | `command grep -rln 'kind: StatefulSet'` → `charts/ollama/…`, `charts/tei/…`; `command grep -rln 'kind: Deployment'` → `charts/jaeger/…`, `charts/prometheus/…` |
| 12 | Consumer impact (totality) | Nothing else in the chart enumerates `/v1/traces` or the six gRPC service paths | `command grep -rln 'v1/traces\|ObjectMappingService\|TenantAdminGrpcService'` across `deploy/helm/iverson/`: only `ingress.yaml` (being edited), `admin-api-ingress.yaml` (different template, correctly untouched), `charts/api/values.yaml` (comment about the admin-api gate, correctly untouched) |
| 13 | Consumer impact (totality) | The new `ssl-redirect` annotation doesn't collide with any overlay's admin-ui annotations | `command grep -rn ssl-redirect deploy/helm/iverson/`: only AWS's distinct `alb.ingress.kubernetes.io/ssl-redirect` (different prefix, different Ingress) |
| 14 | Path / symbol | `Iverson.AdminUI/Dockerfile` exists; `charts/admin-ui/values.yaml` resolves image to `repository: "iverson-admin-ui"`, `tag: "0.1.0"` | Read both |
| 15 | Integration | All five profiles render, all pass kubeconform, and every annotation/path cell matches the spec's Testing criteria, with Task 1's edits applied together | Ran the full render loop in a throwaway worktree: `local` — `t-api` `backend-protocol=GRPC`, `tls=True`, no `/v1/traces`; `t-admin-ui` `ssl-redirect=false`; `t-admin-api` unaffected (keeps `/v1/traces`). `laptop` — `t-api` same fix, no admin-ui/admin-api rendered. `aws`/`azure`/`gcp` — `t-api` loses `/v1/traces` only (`backend-protocol`/`ssl-redirect` correctly absent, non-nginx classes); `t-admin-api` unaffected. kubeconform: `Invalid: 0` on all five (local 92/0/0/6, laptop 72/0/0/5, aws/azure/gcp 98/0/0/6 each) |

## Tasks

### Task 1: Fix the api and admin-ui Ingress templates and values

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/ingress.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates/ingress.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/values-local.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/values-laptop.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/values-azure.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/values-gcp.yaml`

- [ ] **Step 1: Add the annotation and remove the dead route in `charts/api/templates/ingress.yaml`**

  Replace:
  ```yaml
      {{- if eq .Values.ingress.className "nginx" }}
      nginx.ingress.kubernetes.io/configuration-snippet: |
  ```
  with:
  ```yaml
      {{- if eq .Values.ingress.className "nginx" }}
      nginx.ingress.kubernetes.io/backend-protocol: "GRPC"
      nginx.ingress.kubernetes.io/configuration-snippet: |
  ```

  Then delete this block entirely (currently the last path entry, `:95-101`):
  ```yaml
            - path: /v1/traces
              pathType: Prefix
              backend:
                service:
                  name: {{ .Release.Name }}-api
                  port:
                    number: 8080
  ```
  The file must still end with the `TenantAdminGrpcService` path block as its last entry.

- [ ] **Step 2: Add the redirect guard in `charts/admin-ui/templates/ingress.yaml`**

  Replace:
  ```yaml
      {{- if eq .Values.ingress.className "nginx" }}
      nginx.ingress.kubernetes.io/configuration-snippet: |
  ```
  with:
  ```yaml
      {{- if eq .Values.ingress.className "nginx" }}
      {{- if ne .Values.global.externalScheme "https" }}
      nginx.ingress.kubernetes.io/ssl-redirect: "false"
      {{- end }}
      nginx.ingress.kubernetes.io/configuration-snippet: |
  ```

- [ ] **Step 3: `values-local.yaml`**

  Replace the header comment (`:4-8`):
  ```yaml
  # tlsSecretName is intentionally left empty here: local dev has no
  # cert-manager/ACME issuer, so the api Ingress renders without a tls: block
  # and is reached over plain HTTP at https://iverson.local — acceptable only
  # because this profile never leaves localhost. Every cloud overlay in the
  # companion Terraform plan sets a real tlsSecretName.
  ```
  with:
  ```yaml
  # tlsSecretName is now a real self-signed dev cert (deploy/kind/setup.sh
  # generates it), because external gRPC clients need genuine HTTP/2 from the
  # first byte, which ingress-nginx never offers over a plaintext connection —
  # see docs/specs/2026-09-24-api-ingress-nginx-grpc-design.md. Everything else
  # on this profile still stays plain HTTP: admin-api/authentik are on
  # different hostnames with no certificate, and admin-ui's Ingress carries its
  # own ssl-redirect: "false" annotation to stay off the redirect this
  # certificate would otherwise trigger on their shared iverson.local host.
  # global.externalScheme is untouched, still "http".
  ```

  Change `api.ingress.tlsSecretName` (`:116`):
  ```yaml
      tlsSecretName: ""
  ```
  to:
  ```yaml
      tlsSecretName: "iverson-api-tls"
  ```

- [ ] **Step 4: `values-laptop.yaml`** — change `api.ingress.tlsSecretName` (`:97`) the same way, `""` → `"iverson-api-tls"`.

- [ ] **Step 5: `values-azure.yaml` / `values-gcp.yaml`**

  In each, immediately above their existing `tlsSecretName: "iverson-api-tls"` line, add:
  ```yaml
      # The gRPC paths on this Ingress still 400 here — AGIC/GCE need their own
      # gRPC-passthrough configuration, not yet done. See
      # docs/specs/2026-09-24-api-ingress-nginx-grpc-design.md.
  ```

- [ ] **Step 6: Render and verify**

  ```bash
  cd Iverson.Server/deploy/helm/iverson && helm dependency update . >/dev/null
  for p in local laptop aws azure gcp; do
    extra=""; [ -f "values-$p.ci-override.yaml" ] && extra="-f values-$p.ci-override.yaml"
    helm template t . -f "values-$p.yaml" $extra > "/tmp/api-ingress-render-$p.yaml" || { echo "RENDER FAILED: $p"; exit 1; }
    printf '%s kubeconform %s\n' "$p" "$(kubeconform -ignore-missing-schemas -summary /tmp/api-ingress-render-$p.yaml 2>&1 | grep -o 'Invalid: [0-9]*')"
  done
  python3 - <<'PY'
  import yaml
  for p in ["local", "laptop", "aws", "azure", "gcp"]:
      for d in yaml.safe_load_all(open(f"/tmp/api-ingress-render-{p}.yaml")):
          if not d or d.get("kind") != "Ingress": continue
          name = d["metadata"]["name"]
          if name not in ("t-api", "t-admin-ui", "t-admin-api"): continue
          ann = d["metadata"].get("annotations") or {}
          paths = [x["path"] for r in (d["spec"].get("rules") or []) for x in r["http"]["paths"]]
          print(p, name,
                "backend-protocol=" + ann.get("nginx.ingress.kubernetes.io/backend-protocol", "-"),
                "ssl-redirect=" + ann.get("nginx.ingress.kubernetes.io/ssl-redirect", "-"),
                "tls=" + str("tls" in d["spec"]),
                "/v1/traces=" + str("/v1/traces" in paths))
  PY
  ```

  Expected exactly (this is what "Verified plan-level assumptions" row 15 already confirmed running):
  - kubeconform `Invalid: 0` on all five profiles.
  - `local`: `t-api` `backend-protocol=GRPC ssl-redirect=- tls=True /v1/traces=False`; `t-admin-ui` `backend-protocol=- ssl-redirect=false tls=False /v1/traces=False`; `t-admin-api` `backend-protocol=- ssl-redirect=- tls=False /v1/traces=True`.
  - `laptop`: `t-api` same as local's `t-api`; no `t-admin-ui`/`t-admin-api` rows (neither renders).
  - `aws`: `t-api` `backend-protocol=- ssl-redirect=- tls=False /v1/traces=False`; `t-admin-ui`/`t-admin-api` unchanged from HEAD except `t-admin-api` still shows `/v1/traces=True`.
  - `azure`/`gcp`: `t-api` `backend-protocol=- ssl-redirect=- tls=True /v1/traces=False`; `t-admin-api` `/v1/traces=True`.

- [ ] **Step 7: Commit**

  ```bash
  git add Iverson.Server/deploy/helm/iverson/charts/api/templates/ingress.yaml \
          Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates/ingress.yaml \
          Iverson.Server/deploy/helm/iverson/values-local.yaml \
          Iverson.Server/deploy/helm/iverson/values-laptop.yaml \
          Iverson.Server/deploy/helm/iverson/values-azure.yaml \
          Iverson.Server/deploy/helm/iverson/values-gcp.yaml
  git commit -F - <<'EOF'
  fix the api ingress on nginx profiles, drop the dead traces route

  The api Ingress 400ed every request on local/laptop: Kestrel's 8080 is
  HTTP/2-only, and ingress-nginx never accepts a real gRPC client's HTTP/2
  preface over a plaintext connection, regardless of backend-protocol. Adding
  a self-signed dev cert plus backend-protocol: GRPC fixes it, but that
  certificate would otherwise 308-redirect admin-ui's plain-HTTP traffic too
  (ingress-nginx's redirect is host-based, shared with iverson.local) --
  admin-ui gets its own ssl-redirect: "false" to stay off it. /v1/traces is
  removed from this Ingress on every profile; the admin-api Ingress already
  serves it on 8081.

  Co-Authored-By: <executing agent's trailer>
  EOF
  ```

### Task 2: Provision the dev TLS cert in kind, and verify live

**Files:**
- Modify: `Iverson.Server/deploy/kind/setup.sh` (`:55-56`)

- [ ] **Step 1: Add the cert-generation block to `setup.sh`**

  Immediately after:
  ```bash
  kubectl create namespace iverson --dry-run=client -o yaml | kubectl apply -f -
  kubectl label namespace iverson pod-security.kubernetes.io/enforce=baseline --overwrite
  ```
  insert:
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

- [ ] **Step 2: Live verification on a fresh kind cluster**

  ```bash
  kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
  bash Iverson.Server/deploy/kind/setup.sh
  bash Iverson.Server/deploy/kind/build-and-load-image.sh
  bash Iverson.Server/deploy/kind/build-and-load-image.sh 0.1.0 iverson \
    --dockerfile Iverson.AdminUI/Dockerfile --image-name iverson-admin-ui
  cd Iverson.Server/deploy/helm/iverson
  helm upgrade --install iverson . -f values-local.yaml -n iverson --timeout 15m
  ```

  This box's resources don't fit the full profile at once (established during design-phase live verification). Scale down what isn't on the path being tested:
  ```bash
  kubectl scale statefulset -n iverson iverson-ollama --replicas=0
  kubectl scale statefulset -n iverson iverson-tei-bge-base --replicas=0
  kubectl scale deployment -n iverson iverson-prometheus --replicas=0
  kubectl scale deployment -n iverson iverson-jaeger --replicas=0
  ```

  Wait for `iverson-api` to be Ready (`kubectl wait --for=condition=Ready pod -l app=api -n iverson --timeout=10m`), confirming Kestrel is listening on both 8080 and 8081 (this depends on Postgres/StarRocks/Kafka/Authentik/tei-embed all coming up first, per the bootstrap-DDL comment in `docker-compose.yml`).

  Install `grpcurl` and make a real external call over the new TLS listener:
  ```bash
  GOBIN=/tmp/grpcurl-bin go install github.com/fullstorydev/grpcurl/cmd/grpcurl@latest
  /tmp/grpcurl-bin/grpcurl -insecure -authority iverson.local -H 'Host: iverson.local' \
    localhost:8443 list
  ```
  Expected: a real gRPC reflection-or-error response (a genuine `grpc-status`), not `400 An HTTP/1.x request was sent to an HTTP/2 only endpoint`.

  Confirm admin-ui didn't get redirected:
  ```bash
  curl -sk -D - -o /dev/null -H 'Host: iverson.local' https://localhost:8443/admin/
  curl -s -D - -o /dev/null -H 'Host: iverson.local' http://localhost:8080/admin/
  ```
  Expected: the plain-HTTP request returns `200`, not `308`.

  Tear down:
  ```bash
  kind delete cluster --name iverson
  ```

  Any failure is a defect to report, not a flake to re-run away.

- [ ] **Step 3: Commit**

  ```bash
  git add Iverson.Server/deploy/kind/setup.sh
  git commit -F - <<'EOF'
  generate a self-signed dev cert for the api ingress in kind setup

  local/laptop's api Ingress now needs TLS for external gRPC clients (see the
  matching Helm-chart commit). Idempotent: skips generation if the Secret
  already exists, so re-running setup.sh against a live cluster is a no-op.

  Co-Authored-By: <executing agent's trailer>
  EOF
  ```

## Tasks NOT in this plan

- No change to `global.externalScheme` (stays `"http"` on `local`/`laptop`), or to `admin-api`'s or `authentik`'s Ingress (different hostnames, no certificate, so ingress-nginx's host-based TLS redirect never reaches them) — they're plain HTTP/JSON. `admin-ui`'s Ingress gets one new annotation (Design item 6) purely to stay off the redirect the api Ingress's new certificate would otherwise trigger on their shared hostname; nothing else about its behavior changes.
- No AGIC/GCE-specific fix for `azure`/`gcp` — deferred, documented, no live consumer.
- No change to which of the six gRPC services enable grpc-web — orthogonal to this fix.

## Known issues inherited from spec

- **`azure`/`gcp` gRPC paths remain broken** on this Ingress. TLS already exists there; AGIC/GCE need their own gRPC-passthrough configuration, not researched or verified here. No live deployment depends on it today.
- **External SDK consumers of `local`/`laptop` now need `https://` with verification disabled**, where they previously (in principle, once fixed) would have used plain HTTP. This is a real behavior change for anyone relying on this Ingress externally, called out here rather than silently introduced.
- **AWS ALB's GRPC target-group behavior for a non-gRPC POST** was already moot before this design (the traces-relay design's own known-issue note) and stays moot now that `/v1/traces` is removed from every profile's api Ingress, AWS included.

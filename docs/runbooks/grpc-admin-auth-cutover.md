# gRPC + Admin Auth — First-Install / Cutover Runbook

> **CSR round 12 §3.1.** Commands written `kubectl $OPS …` or `helm … $HOPS` run as the operators group: set `OPS` and `HOPS` as `docs/runbooks/operators-group.md` §1 says (both empty on a cloud).

**Why:** authentication is a hard cutover the moment this ships — there is no permissive/warn-only
rollout window (see `docs/superpowers/plans/2026-07-11-grpc-and-admin-authentication-implementation-plan.md`'s
Global Constraints). Every existing caller must already hold a valid token before this deploys.
That precondition, plus a few operational gotchas, is captured here so it outlives the plan document.

## Precondition: every existing caller needs a token before you deploy

Once this ships, the API's `FallbackPolicy` rejects every request without a valid bearer token —
including the 4 gRPC services (any authenticated caller) and the 3 `/admin/*` routes (operator tier).
There is no grace period. Confirm before deploying:
- Every service/automation caller (loadtest, webtest, admin-automation, and any future caller) has its
  `client_id`/`client_secret` and is wired to fetch a token via its SDK's `IversonClientCredentials`
  (or per-language equivalent) — not just deployed with the old unauthenticated call construction.
- Any human operator who needs `/admin/*` access has their `sub` on the release's operator list,
  `global.operatorSubjects` (`docs/runbooks/operator-access-onboarding.md` — this part has no automated
  coverage; see below).

## FIXED (CSR round-3 finding #15): a fresh install no longer needs a two-pass `helm upgrade`

Historically, on a genuinely fresh install, the 10 OAuth2-client/user/token Secrets
(`charts/authentik/templates/secret-service-clients.yaml`) and the templated blueprint carrying
the same 10 credentials into Authentik (formerly a separate
`blueprints-secret-service-clients.yaml` file) rendered in the **same** Helm pass, each with its
own independent `{{ if $existing }}...{{ else }}{{ randAlphaNum ... }}{{ end }}` fallback. Neither
side's `lookup` could see a Secret the other side was creating in that same pass, so each minted
its own independent random value for what was supposed to be one shared credential — the `aud`
claim Authentik issued (from the blueprint's fallback) didn't match what the API validated against
(from the Secret's fallback) until a second `helm upgrade` converged both sides via `lookup` now
finding the real, already-created Secret.

This is fixed: the two files were merged into one (`secret-service-clients.yaml`), which computes
each of the 10 credential values exactly once and has both the real Secrets and the blueprint's
embedded YAML reference that same computed value — there is no longer a second independent
`randAlphaNum` draw to diverge from the first. Verified by rendering the merged template (both the
"no existing Secret" first-install branch and the "Secret already exists" branch, the latter against
a live cluster) and confirming all 13 credential values (10 Secrets, 3 of which carry both a
client-id and a client-secret) are byte-identical between the emitted Secrets and the blueprint's
`stringData` on the very first `helm install` — no second pass needed for these credentials
specifically. Since CSR round 11 the admin-automation Secret carries only its `client-id`; its
client authenticates with a service-account token (`docs/runbooks/csr11-identity-plane.md` §4).
(This was verified at the template-rendering level, not via a full live multi-service `helm install`
against a fully-provisioned cluster with CNPG/Strimzi/StarRocks operators installed — the bug and
its fix both live entirely in what value a template computes, which template-level verification
covers directly.)

A single `helm upgrade --install` now converges on the first pass:

```bash
helm upgrade --install iverson . -f values-<env>.yaml -n iverson --create-namespace $HOPS
```

Note: the upgrade moves the Authentik Ingress to Service port 9080 immediately, while the old
single pod (which has no 9080 listener) remains the only endpoint until the new pod is Ready, so
public logins return 502 for that short window.

## Confirming the blueprint actually applied

Authentik applies blueprint changes **asynchronously** via a worker task queue — confirmed live
(Task 10) this can take a couple of minutes, occasionally longer under load. Verify before assuming
something's broken:

```bash
LOADTEST_SECRET_ID=$(kubectl -n <ns> get secret <release>-authentik-loadtest-client -o jsonpath='{.data.client-id}' | base64 -d)
kubectl $OPS -n <ns> exec deploy/<release>-authentik-worker -- env EXPECTED_CLIENT_ID="$LOADTEST_SECRET_ID" ak shell -c "
import os
from authentik.providers.oauth2.models import OAuth2Provider
print('matches secret:', OAuth2Provider.objects.get(name='iverson-loadtest').client_id == os.environ['EXPECTED_CLIENT_ID'])
"
```

The check reads the provider inside the worker pod, so it needs no Authentik API token (the chart no longer provisions the bootstrap token, CSR round-10 #4).

If still `False` after ~2 minutes, force a re-scan rather than waiting indefinitely:
```bash
kubectl -n <ns> rollout restart deployment/<release>-authentik-worker
```

## A Deployment's pods may need a restart if a Secret's value changes after pods exist

`secretKeyRef`-sourced env vars (like `Authentication:ValidAudiences`) are resolved **once, at pod
creation** — Kubernetes does not live-update a running container's environment when the backing
Secret's data changes later (see `docs/runbooks/kind-cluster-troubleshooting.md`'s §5.2 for the full
mechanics). This no longer happens on a fresh install for the 10 credentials covered by the fix
above (their value is stable from the first `helm install`), but it still applies any time one of
those Secrets is rotated by hand after the `iverson-api` Deployment's pods already exist — updating
the Secret's content alone is not enough:

```bash
kubectl $OPS -n <ns> rollout restart deployment/<release>-api
```

## Known gap: the human/browser OIDC path has no automated verification

The interactive Authorization Code + PKCE + MFA flow a human operator uses to sign in to the console
can't be scripted meaningfully. After any deploy where operator access matters, manually confirm:
1. The operator's `sub` is on `global.operatorSubjects`, the api pods have rolled with it, and
   service-clients blueprint has been re-applied (`docs/runbooks/csr11-identity-plane.md` §2).
2. Complete a browser-based OIDC login against the `iverson-api` application (the console).
3. Confirm `GET /admin/console/roles` answers `"operator": true` for the resulting token and that
   `/admin/*` accepts it.

## Related

- `docs/runbooks/kind-cluster-troubleshooting.md` §5.1 — if a manually-minted test token gets a bare
  401 with no useful log line, check the issuer/Host-header mismatch trap before assuming the auth
  pipeline itself is broken.

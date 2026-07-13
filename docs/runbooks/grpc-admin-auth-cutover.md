# gRPC + Admin Auth — First-Install / Cutover Runbook

**Why:** authentication is a hard cutover the moment this ships — there is no permissive/warn-only
rollout window (see `docs/superpowers/plans/2026-07-11-grpc-and-admin-authentication-implementation-plan.md`'s
Global Constraints). Every existing caller must already hold a valid token before this deploys, and
a fresh install has a real two-pass convergence requirement that isn't obvious from the chart alone.
Both are captured here so they outlive the plan document.

## Precondition: every existing caller needs a token before you deploy

Once this ships, the API's `FallbackPolicy` rejects every request without a valid bearer token —
including the 4 gRPC services (any authenticated caller) and the 3 `/admin/*` routes (operator tier).
There is no grace period. Confirm before deploying:
- Every service/automation caller (loadtest, webtest, admin-automation, and any future caller) has its
  `client_id`/`client_secret` and is wired to fetch a token via its SDK's `IversonClientCredentials`
  (or per-language equivalent) — not just deployed with the old unauthenticated call construction.
- Any human operator who needs `/admin/*` access is already a member of Authentik's `operators` Group
  (Task 10 Step 9 — this part has no automated coverage; see below).

## The two-pass `helm upgrade` requirement on a fresh install

On a genuinely fresh install, the 4 new OAuth2-client Secrets
(`secret-service-clients.yaml`) and the templated blueprint ConfigMap
(`blueprints-configmap-service-clients.yaml`) render in the **same** Helm pass. The blueprint's
`{{ if $secret }}...{{ else }}{{ randAlphaNum ... }}{{ end }}` fallback can't see a Secret that's
being created in the same pass — `lookup` returns empty — so it mints its own random
`client_id`/`client_secret`, independent from (and different from) what actually lands in the
Secrets. Since the API's `Authentication:ValidAudiences` env vars are sourced from those same
Secrets, the `aud` claim Authentik issues (from the blueprint's fallback) won't match what the API
validates against, until a second pass converges them:

```bash
helm upgrade --install iverson . -f values-<env>.yaml -n iverson --create-namespace   # pass 1
helm upgrade iverson . -f values-<env>.yaml -n iverson                                # pass 2
```

Pass 1 is *expected* to leave the deployment in a mismatched state — this is not a failure, don't
debug it, just run pass 2.

## Confirming the blueprint actually applied

Authentik applies blueprint changes **asynchronously** via a worker task queue — confirmed live
(Task 10) this can take a couple of minutes, occasionally longer under load. Verify before assuming
something's broken:

```bash
TOKEN=$(kubectl -n <ns> get secret <release>-authentik-app -o jsonpath='{.data.bootstrap-token}' | base64 -d)
LOADTEST_SECRET_ID=$(kubectl -n <ns> get secret <release>-authentik-loadtest-client -o jsonpath='{.data.client-id}' | base64 -d)
curl -s -H "Authorization: Bearer $TOKEN" "http://<authentik-host>:9000/api/v3/providers/oauth2/" \
  | python3 -c "
import json,sys
d = json.load(sys.stdin)
p = next(x for x in d['results'] if x['name']=='iverson-loadtest')
print('matches secret:', p['client_id'] == '$LOADTEST_SECRET_ID')
"
```

If still `False` after ~2 minutes, force a re-scan rather than waiting indefinitely:
```bash
kubectl -n <ns> rollout restart deployment/<release>-authentik-worker
```

## A Deployment's pods may need a restart even after the Secrets converge

`secretKeyRef`-sourced env vars (like `Authentication:ValidAudiences`) are resolved **once, at pod
creation** — Kubernetes does not live-update a running container's environment when the backing
Secret's data changes later (see `docs/runbooks/kind-cluster-troubleshooting.md`'s §5.2 for the full
mechanics). If the `iverson-api` Deployment's pods were created during pass 1 (before the Secrets held
their final converged values), pass 2 updating the Secret content alone is not enough:

```bash
kubectl -n <ns> rollout restart deployment/<release>-api
```

## Known gap: the human/browser OIDC path has no automated verification

The interactive Authorization Code + PKCE + MFA flow a human operator uses to log in and pick up the
`operators` group membership can't be scripted meaningfully. After any deploy where operator access
matters, manually confirm:
1. Log into Authentik's UI as the operator.
2. Add the operator's user to the `operators` Group (if not already a member).
3. Complete a browser-based OIDC login against the `iverson-api` application.
4. Confirm the resulting token's `groups` claim contains `operators` and that it's accepted on
   `/admin/*`.

## Related

- `docs/runbooks/kind-cluster-troubleshooting.md` §5.1 — if a manually-minted test token gets a bare
  401 with no useful log line, check the issuer/Host-header mismatch trap before assuming the auth
  pipeline itself is broken.

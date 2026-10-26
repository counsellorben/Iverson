# CSR Round-11 Identity Plane — Operator Runbook

> **CSR round 12 §3.1.** Commands written `kubectl $OPS …` or `helm … $HOPS` run as the operators group: set `OPS` and `HOPS` as `docs/runbooks/operators-group.md` §1 says (both empty on a cloud).

**Why:** this change makes Authentik record the real client address and throttle failed logins. It
enrols a second factor during recovery, refuses superusers on the public listener, and replaces the
`akadmin` password with a named break-glass admin. Admin-automation callers authenticate with a
short-lived service-account token instead of a client secret. The login path is also rate-limited
at the cloud edge. Compose keeps `akadmin`, its bootstrap credentials and its client secrets.

## 1. Edge rate limits (cloud profiles)

Terraform creates the limit, and Helm attaches it to the Authentik Ingress. Terraform does not
feed Helm values, so copy the output across by hand. A cloud profile's render fails until you do.

| Cloud | Terraform output | Values key (`values-<cloud>.yaml`) |
|---|---|---|
| AWS | `authentik_waf_acl_arn` | `authentik.ingress.wafAclArn` |
| GCP | `authentik_security_policy_name` | `authentik.ingress.securityPolicy` |
| Azure | `authentik_waf_policy_id` | `authentik.ingress.wafPolicyId` |

1. Optionally set `authentik_login_rate_limit` (default 100 requests) and
   `authentik_login_rate_window` (default 300 s) on the root. Azure accepts only 60 or 300 s.
   Azure also requires `authentik_host`, the Authentik host: `authentik.<global.ingressHost>`.
   The edge limit counts every flow-executor request, and one interactive login is roughly 8-12 of
   them, so the default 100 per 300 s is about ten logins per client address per five minutes (on
   GCP, exceeding it bans the address for 10 minutes). Size `authentik_login_rate_limit` for users
   behind a shared NAT.
2. `terraform apply`, then `terraform output -raw <output>` and put the value under the key in the
   table.
3. `helm upgrade`. The limit counts requests per client address to `/api/v3/flows/executor/` on
   the Authentik host, and blocks (GCP: bans for 10 minutes) over the limit.

On Azure the Application Gateway is now Terraform's own `WAF_v2` gateway in `10.1.16.0/24`. The
add-on is pointed at it and granted Network Contributor on the resource group. This assumes a fresh
install: no install with the add-on's own gateway exists. On the Authentik host the gateway runs
the OWASP rule set in Prevention, so a managed-rule false positive can refuse a login there. The
other hosts run it in Detection.

## 2. After an install or upgrade: apply the changed blueprints

Authentik re-applies a blueprint whose file changed, through a file watcher and an hourly
schedule. On a fresh install, its first automatic apply can also run before the stock login flow
exists, which fails `reputation.yaml` and `superusers.yaml` until the hourly re-apply
(`reputation.yaml` also waits for `recovery-flow.yaml`, which defines the throttle it binds).
`mfa-enforcement.yaml` refers to the stock TOTP and WebAuthn setup stages, and until it applies the
stock MFA validation stage lets a user with no device sign in without MFA, whatever the profile
renders (spec §7, fresh-install MFA window). To have
everything in force at once, apply the files yourself after the install or upgrade, static files
first. `superusers.yaml` refers to flows defined in them, and a missing referent fails the
whole file.

1. Wait until the new worker pod is the only one:
   `kubectl -n <ns> rollout status deploy/<release>-authentik-worker`, then repeat
   `kubectl -n <ns> get pods -l app=<release>-authentik-worker` until it lists one pod.
2. On a fresh install, wait until Authentik's own blueprints have created the login flow and the
   TOTP and WebAuthn setup stages the static files refer to, and the out-of-box `initial-setup` flow that
   `service-clients.yaml` retitles (up to 5 minutes):

   ```bash
   ok=no; for i in $(seq 1 30); do [ "$(kubectl $OPS -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c "from authentik.flows.models import Flow; from authentik.stages.authenticator_totp.models import AuthenticatorTOTPStage; from authentik.stages.authenticator_webauthn.models import AuthenticatorWebAuthnStage; print(Flow.objects.filter(slug='default-authentication-flow').count(), AuthenticatorTOTPStage.objects.filter(name='default-authenticator-totp-setup').count(), AuthenticatorWebAuthnStage.objects.filter(name='default-authenticator-webauthn-setup').count(), Flow.objects.filter(slug='initial-setup').count())" 2>/dev/null | tail -1)" = "1 1 1 1" ] && { ok=yes; break; }; sleep 10; done; echo "stock objects present: $ok"
   ```

   Continue when it prints `yes`. If it prints `no`, Authentik's own blueprints have not applied yet:
   check the worker's log for blueprint errors and repeat this step before applying.
3. Apply:

   ```bash
   kubectl $OPS -n <ns> exec deploy/<release>-authentik-worker -c worker -- \
     ak apply_blueprint custom/recovery-flow.yaml custom/reputation.yaml custom/mfa-enforcement.yaml
   kubectl $OPS -n <ns> exec deploy/<release>-authentik-worker -c worker -- \
     ak apply_blueprint custom/superusers.yaml custom/orchestrator-federation.yaml \
       custom/service-clients/service-clients.yaml custom/service-clients/admin-automation.yaml \
       custom/initial-setup.yaml
   ```

   Each command exits non-zero if a blueprint fails. On a worker at the laptop profile's memory limits the command can be killed for memory (exit 137); re-running it is safe because applying a blueprint is idempotent. `admin-automation.yaml` and
   `orchestrator-federation.yaml` fetch the cluster's keys from the worker's JWKS relay when they
   apply. If the relay can't reach the API server, both fail and wait for the next re-apply; the
   message names `oidc_jwks_url`.

## 3. Break-glass admin

`akadmin` is deactivated, and nobody has an Authentik superuser password. The superuser is
`iverson-breakglass`, which has no password until you activate it. Superusers are refused on the
public listener, at login and in recovery, so everything here uses the in-cluster route.

1. Forward the in-cluster listener: `kubectl $OPS -n <ns> port-forward svc/<release>-authentik 9000:9000`.
2. Generate a recovery link in the worker. This builds the same link as the admin API's user
   recovery action; it is valid for one hour and one use:

   ```bash
   kubectl $OPS -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c '
   from django.test import RequestFactory
   from rest_framework.request import Request
   from authentik.brands.utils import get_brand_for_request
   from authentik.core.api.users import UserViewSet
   from authentik.core.models import User
   req = RequestFactory().get("/", HTTP_HOST="localhost:9000")
   req.brand = get_brand_for_request(req)
   view = UserViewSet()
   view.request = Request(req)
   view.get_object = lambda: User.objects.get(username="iverson-breakglass")
   link, _ = view._create_recovery_link("hours=1")
   print(link)'
   ```

3. Open the printed `http://localhost:9000/if/flow/iverson-recovery/?flow_token=…` link. Set a
   password, then scan the TOTP code shown, enter a code, and you are signed in. The flow ends on
   the console URL; the admin interface is at `http://localhost:9000/if/admin/`.
4. Later sign-ins: `http://localhost:9000/if/flow/default-authentication-flow/` through the same
   port-forward, with the password and a TOTP code. If the device is lost, generate a new link (step 2).

Do not use `ak create_recovery_key`: its link signs the user straight in and skips the recovery flow,
so no second factor is checked or enrolled.

## 4. Admin-automation callers

The `iverson-admin-automation` client accepts only a token of the `<release>-admin-automation`
ServiceAccount, with audience `iverson-authentik-admin-automation` and a lifetime of at most 600 s.
Its former client secret stops working at the upgrade. The token buys a 1-hour Operator token.

```bash
kubectl $OPS -n <ns> create token <release>-admin-automation --audience iverson-authentik-admin-automation \
  --duration 10m > /path/to/sa-token
export IVERSON_CLIENT_ID="$(kubectl -n <ns> get secret <release>-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
export IVERSON_CLIENT_ASSERTION_FILE=/path/to/sa-token
export IVERSON_CLIENT_SCOPE="admin schema_admin tenant_id_admin"
export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/   # with the port-forward from §3
export IVERSON_ACTING_USER_HOST_HEADER=<release>-authentik:9000             # the issuer the API accepts
```

LoadTest re-reads the file for every Operator token it requests, about 59 minutes after the previous
one, so the token in the file must still be valid then; it lives 10 minutes. For a run longer than
an hour, rewrite the file with a freshly minted token every 5 minutes for the length of the run. A
caller of its own sends the token as a `client_credentials` request with
`client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer` and
`client_assertion=<token>`.

## 5. Login throttling

Authentik keeps one reputation row per (client address, identifier), floored at −5 and capped at +5. A
wrong password lowers its row by one, a wrong MFA code by two, and an unknown username by two per attempt
(its identification scores too); a successful login raises the username's row by one. Since CSR round 12
(`blueprints/recovery-flow.yaml`, bound to the login flow by `reputation.yaml`) a new login plan is refused, after the username is entered and before any
password is asked, when either rule holds for the address:

- **pair:** that address's rows for the username, or the account's email, sum to −5 or less;
- **address:** that address's failing rows, across every identifier, sum to −50 or less.

So five wrong passwords for an account, one per plan, refuse its next plan, and an unknown username's
third plan is refused. Each sign-in by the account's owner from the same address allows one more failure
before the refusal. The address rule needs failures against at least ten identifiers, so a shared NAT is
not refused because one account failed; failures concentrated on fewer accounts meet the pair rule first.
The rules refuse new plans, not attempts inside a plan: there the password stage ends a plan after five
wrong passwords when the plan started with the pair at 0, and on the cloud profiles the edge rate limit
(§1) bounds the rest.

The same check runs first in the recovery flow, so a recovery link is refused too while its account's
pair or its address is over a limit. A link minted before this throttle was installed skips it until
it expires.

A row expires an hour after it is created (`AUTHENTIK_REPUTATION__EXPIRY=3600`, install-wide), whether
by a failure or a successful login, and a cleanup that runs every five minutes removes expired rows;
until it runs, new failures for that pair are not counted. The address is the one Authentik records:
the client itself, selected from the load balancer's `X-Forwarded-For` by `global.trustedProxies`.

The in-cluster route is one shared address: only the tls-proxy (connecting from 127.0.0.2) is trusted,
so every `kubectl port-forward` caller (operators, the break-glass admin, LoadTest on Helm) is recorded
as `::ffff:127.0.0.1`. Five failures for one username there refuse that username's logins and recovery
links through the in-cluster route for up to an hour; other usernames are refused only once the shared
address reaches −50.

To clear rows before they expire, delete them in the worker, which needs no admin session:

```bash
kubectl $OPS -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c '
from authentik.policies.reputation.models import Reputation
print(Reputation.objects.filter(ip="<address>").delete())'
```

Add `identifier="<username>"` to the filter to clear one pair only. The admin API lists and deletes the
same rows at `/api/v3/policies/reputation/scores/`.

## 6. Namespace access

The orchestrator and admin-automation trust Kubernetes service-account tokens, so whoever can mint
those tokens or run code as those accounts in the release namespace can obtain the tokens' rights.
Restrict these verbs in the release namespace to the deploy identity: create on `pods`,
`pods/exec` and `serviceaccounts/token`. Check a principal with
`kubectl auth can-i create serviceaccounts/token -n <ns> --as <user>`.

## 7. Service-account issuer

Both workload pins, the orchestrator's (`orchestrator-federation.yaml`) and admin-automation's
(`admin-automation.yaml`), compare a token's `iss` byte for byte with `authentik.serviceAccountIssuer`,
and the render fails while it is empty.

| Profile | Value of `authentik.serviceAccountIssuer` |
|---|---|
| kind (local, laptop) | `https://kubernetes.default.svc.cluster.local`, set in the overlay |
| AWS | Terraform output `service_account_issuer`: the EKS OIDC issuer |
| Azure | Terraform output `service_account_issuer`: the AKS OIDC issuer, which ends with `/` |
| GCP | Terraform output `service_account_issuer`, computed from the project, location and cluster name |

Copy the output into `values-<cloud>.yaml` as for §1 (`terraform output -raw service_account_issuer`).
On AKS, `oidc_issuer_enabled = true` turns the issuer on; it cannot be turned off afterwards. Google does
not document the GKE format, so at first deploy compare the value with the `issuer` in
`kubectl get --raw /.well-known/openid-configuration`.

## 8. Rotating `AUTHENTIK_SECRET_KEY`

The key signs Authentik's cookies, its signed file URLs, and the embedded outpost's and metrics' bearer.
It encrypts no database field, and issued OAuth tokens are signed with the providers' certificate, so
they stay valid. Rotating it ends every browser session and every flow in progress. Pods holding
different keys reject each other's cookies, so the server pods and the worker restart together, with a
short outage of the login pages.

1. Replace the Secret's value (the chart keeps an existing `secret-key` on upgrade):

   ```bash
   NEW_KEY="$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 50)"
   kubectl -n <ns> patch secret <release>-authentik-app --type merge \
     -p "{\"data\":{\"secret-key\":\"$(printf %s "$NEW_KEY" | base64 -w0)\"}}"
   unset NEW_KEY
   ```

2. Delete every Authentik pod at once, so that none keeps the old key, and wait for the new ones:

   ```bash
   kubectl -n <ns> delete pod -l 'app in (<release>-authentik-server,<release>-authentik-worker)'
   kubectl -n <ns> rollout status deploy/<release>-authentik-worker
   kubectl -n <ns> rollout status deploy/<release>-authentik-server
   ```

# CSR Round-11 Identity Plane — Operator Runbook

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
exists, which fails `reputation.yaml` and `superusers.yaml` until the hourly re-apply. To have
everything in force at once, apply the files yourself after the install or upgrade, static files
first. `superusers.yaml` refers to flows defined in them, and a missing referent fails the
whole file.

1. Wait until the new worker pod is the only one:
   `kubectl -n <ns> rollout status deploy/<release>-authentik-worker`, then repeat
   `kubectl -n <ns> get pods -l app=<release>-authentik-worker` until it lists one pod.
2. On a fresh install, wait until Authentik's own blueprints have created the login flow and the
   TOTP setup stage the static files refer to (up to 5 minutes):

   ```bash
   ok=no; for i in $(seq 1 30); do [ "$(kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c "from authentik.flows.models import Flow; from authentik.stages.authenticator_totp.models import AuthenticatorTOTPStage; print(Flow.objects.filter(slug='default-authentication-flow').count(), AuthenticatorTOTPStage.objects.filter(name='default-authenticator-totp-setup').count())" 2>/dev/null | tail -1)" = "1 1" ] && { ok=yes; break; }; sleep 10; done; echo "stock objects present: $ok"
   ```

   Continue when it prints `yes`. If it prints `no`, Authentik's own blueprints have not applied yet:
   check the worker's log for blueprint errors and repeat this step before applying.
3. Apply:

   ```bash
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- \
     ak apply_blueprint custom/recovery-flow.yaml custom/reputation.yaml
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- \
     ak apply_blueprint custom/superusers.yaml custom/orchestrator-federation.yaml \
       custom/service-clients/service-clients.yaml custom/service-clients/admin-automation.yaml
   ```

   Each command exits non-zero if a blueprint fails. `admin-automation.yaml` and
   `orchestrator-federation.yaml` fetch the cluster's keys from the worker's JWKS relay when they
   apply. If the relay can't reach the API server, both fail and wait for the next re-apply; the
   message names `oidc_jwks_url`.

## 3. Break-glass admin

`akadmin` is deactivated, and nobody has an Authentik superuser password. The superuser is
`iverson-breakglass`, which has no password until you activate it. Superusers are refused on the
public listener, at login and in recovery, so everything here uses the in-cluster route.

1. Forward the in-cluster listener: `kubectl -n <ns> port-forward svc/<release>-authentik 9000:9000`.
2. Generate a recovery link in the worker. This builds the same link as the admin API's user
   recovery action; it is valid for one hour and one use:

   ```bash
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c '
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
kubectl -n <ns> create token <release>-admin-automation --audience iverson-authentik-admin-automation \
  --duration 10m > /path/to/sa-token
export IVERSON_CLIENT_ID="$(kubectl -n <ns> get secret <release>-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
export IVERSON_CLIENT_ASSERTION_FILE=/path/to/sa-token
export IVERSON_CLIENT_SCOPE="admin schema_admin tenant_id_admin"
export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/   # with the port-forward from §3
export IVERSON_ACTING_USER_HOST_HEADER=<release>-authentik:9000             # the issuer the API accepts
```

LoadTest re-reads the file for every Operator token it requests, about 59 minutes after the previous
one, so the token in the file must still be valid then; it lives 10 minutes. For a run longer than an
hour, rewrite the file with a freshly minted token every 5 minutes for the length of the run. A caller of its own sends the token as
a `client_credentials` request with `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer`
and `client_assertion=<token>`.

## 5. Login throttling

Authentik refuses a client address, before it asks for a username, once its failed attempts sum to
five. A failed password costs one and a failed MFA code two, and successful logins do not offset
failures. The address is the one Authentik records: the client itself on every profile, selected
from the load balancer's `X-Forwarded-For` by `global.trustedProxies`. A shared NAT can therefore be
refused for up to 24 hours, counted from the first failure.

To clear an address before then, delete its scores in the admin interface (in-cluster, §3) or with
the API:

```bash
curl -s -H "Authorization: Bearer <admin token>" "http://localhost:9000/api/v3/policies/reputation/scores/?ip=<address>"
curl -s -X DELETE -H "Authorization: Bearer <admin token>" "http://localhost:9000/api/v3/policies/reputation/scores/<pk>/"   # pk from the listing
```

## 6. Namespace access

The orchestrator and admin-automation trust Kubernetes service-account tokens, so whoever can mint
those tokens or run code as those accounts in the release namespace can obtain the tokens' rights.
Restrict these verbs in the release namespace to the deploy identity: create on `pods`,
`pods/exec` and `serviceaccounts/token`. Check a principal with
`kubectl auth can-i create serviceaccounts/token -n <ns> --as <user>`.

# Operator Access Onboarding

**Why:** the API grants Operator (the `/admin/*` HTTP endpoints, the console's operator cards and the
TenantLifecycle RPCs) through `OperatorAuthorizationPolicy`
(`Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs`), not through an Authentik group. Since CSR
round 12 a principal is an Operator when its `sub` claim is on the release's operator list, the Helm value
`global.operatorSubjects`, or when its token's `scope` contains `admin` (admin-automation). Helm renders
the list into the api Deployment (`Authorization__OperatorSubjects__N`) and into the console sign-in
policy (in the service-clients blueprint, `charts/authentik/templates/secret-service-clients.yaml`), which admits
the operators and the members of `tenant-admins` and refuses everyone else. No Authentik group yields
Operator on a Helm install, and the upgrade deletes the old `operators` group.

A `sub` is `sha256(<user id>-<install id>)`: stable within one Authentik install, different in every
other, and not predictable from a username. Who holds operator rights is an operational decision kept
in the profile's values file, never something a deployment's blueprints seed.

## Compose (local dev)

Compose keeps the `operators` group as a dev-only fallback: `docker-compose.yml` sets
`Authorization__OperatorGroupFallback=operators`, so a member of that group is an Operator there (the
API logs a warning at startup), and compose's copy of the console sign-in policy admits it. Nothing to
do — `blueprints/compose-only/service-clients.yaml` seeds `iverson-loadtest-bypass-user` into
`operators` at blueprint-apply time, so that identity already satisfies the policy for local
admin-console testing. To grant a different local user access, add them to `operators` (Directory →
Groups) in the admin UI of the compose Authentik instance (`http://localhost:9000`, bootstrap login
`admin@iverson.local` / the `AUTHENTIK_BOOTSTRAP_PASSWORD` value in `Iverson.Server/.env`).

## Kind / production

Onboarding an operator is four steps: create the account, send it a recovery link, read its `sub`, and
add the `sub` to the operator list. The admin interface and the rest of the admin API are reachable only
in-cluster, and the cloud profiles refuse a sign-in by an account with no second factor, so the new
operator sets a password and enrols TOTP through the recovery link before their first sign-in.

**The first install on a cloud.** The chart refuses an empty `global.operatorSubjects` on the cloud
profiles, and no `sub` exists until the release's Authentik database does. For the first install, list
one entry that is not a `sub`, such as `"none-yet"`: a `sub` is 64 hexadecimal characters, so the entry
makes nobody an Operator, and admin-automation keeps Operator through its `admin` scope. Once the release
is up, onboard the first operator with the steps below and replace that entry with their `sub`.

1. Create the account as the break-glass admin, `iverson-breakglass`, over the in-cluster route
   (`docs/runbooks/csr11-identity-plane.md` §3; `akadmin` is deactivated since CSR round 11): run
   `kubectl -n <ns> port-forward svc/<release>-authentik 9000:9000`, open
   `http://localhost:9000/if/admin/`, then Directory → Users → Create. An operator needs no group.
2. Mint a one-hour recovery link for the account in the worker. A link carries the host it was requested
   on, so this asks with the public Authentik host (`authentik.<global.ingressHost>`); on a cloud profile
   keep `secure=True`, on kind (`global.externalScheme: http`) set `secure=False`:

   ```bash
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c '
   from django.test import RequestFactory
   from rest_framework.request import Request
   from authentik.brands.utils import get_brand_for_request
   from authentik.core.api.users import UserViewSet
   from authentik.core.models import User
   req = RequestFactory().get("/", HTTP_HOST="authentik.<global.ingressHost>", secure=True)
   req.brand = get_brand_for_request(req)
   view = UserViewSet()
   view.request = Request(req)
   view.get_object = lambda: User.objects.get(username="<operator username>")
   link, _ = view._create_recovery_link("hours=1")
   print(link)'
   ```

   Send the printed `…/if/flow/iverson-recovery/?flow_token=…` link to the operator. It sets a password,
   enrols TOTP, and ends on the console, which refuses the sign-in until step 4 is done.
3. Read the account's `sub` (Authentik's `uid` for it):

   ```bash
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c '
   from authentik.core.models import User
   print(User.objects.get(username="<operator username>").uid)'
   ```

4. Add it to the profile's values file and upgrade:

   ```yaml
   global:
     operatorSubjects:
       - "<the printed uid>"
   ```

   Then `helm upgrade` the release with that file. The api pods roll, because their template changes, and
   take the new list. Authentik's console sign-in policy changes when the worker re-applies the
   service-clients blueprint; apply it at once instead of waiting for that:
   `kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak apply_blueprint custom/service-clients/service-clients.yaml`.
5. Have the operator sign in to the console and confirm `GET /admin/console/roles` answers
   `"operator": true` for their token (see `docs/runbooks/grpc-admin-auth-cutover.md`'s "Known gap"
   section for the equivalent check during a cutover deploy).

To remove an operator, delete the entry and repeat step 4. The api refuses Operator to that `sub` as soon
as its pods have rolled.

## Rebuilding the operator list after an Authentik database recreation

Every `sub` embeds the install's id, so a recreated Authentik database (a dropped `authentik` database,
or a restore into a new install) changes every operator's `sub`, and the list then admits nobody.
Admin-automation keeps Operator through its `admin` scope, and tenant admins keep the console through
`tenant-admins`.

1. Recreate each operator's account (step 1 above), or confirm it exists after a restore, and send it a
   recovery link (step 2) if it has no password in the new database.
2. Print every operator's new `sub` at once:

   ```bash
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c '
   from authentik.core.models import User
   for u in User.objects.filter(username__in=["<operator 1>", "<operator 2>"]):
       print(u.username, u.uid)'
   ```

3. Replace `global.operatorSubjects` with exactly the printed values, then `helm upgrade` and apply
   the service-clients blueprint as in step 4 above.

## Related

- `docs/runbooks/grpc-admin-auth-cutover.md` — the deploy-time precondition that every operator
  is already on the operator list before a fresh install or cutover ships (a cloud's first install
  cannot list one yet: see "The first install on a cloud" above).
- `docs/user-management-and-security.md#creating-a-human-user-and-granting-operator-access` — the
  fuller reference doc this runbook's steps are drawn from.

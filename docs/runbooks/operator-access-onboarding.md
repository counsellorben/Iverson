# Operator Access Onboarding

**Why:** `OperatorAuthorizationPolicy` (`Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs`)
gates the 3 `/admin/*` HTTP endpoints and the admin console's Tenants nav item on a token whose
`groups` claim contains `operators`. The `operators` Authentik group itself is now created
automatically (`Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/operators-group.yaml`,
a top-level blueprint applied in both compose and kind/Helm targets), but **membership in it is
deliberately not blueprinted for any real deployment.** Who holds operator rights is an
operational decision, not something to hardcode into version-controlled config — seeding a user
into an operator group in a production values file would be a privilege-escalation defect.

This runbook covers the one step that stays manual: granting an individual human operator access
in a real deployment.

## Compose (local dev)

Nothing to do — `blueprints/compose-only/service-clients.yaml` seeds `iverson-loadtest-bypass-user`
into `operators` at blueprint-apply time, so that identity already satisfies the policy for local
admin-console testing. To grant a different local user access, follow the kind/production steps
below against the compose Authentik instance (`http://localhost:9000`, bootstrap login
`admin@iverson.local` / the `AUTHENTIK_BOOTSTRAP_PASSWORD` value in `Iverson.Server/.env`).

## Kind / production

The `operators` group already exists (blueprinted) — do not create it. Only membership is manual:

1. Log into the Authentik admin UI with the deployment's bootstrap credentials (kind: email from
   `AUTHENTIK_BOOTSTRAP_EMAIL`, password from the `<release>-authentik-app` Secret's
   `bootstrap-password` key).
2. Create the user (Directory → Users → Create) if they don't already have one, or locate their
   existing account.
3. Directory → Groups → `operators` → add the user.
4. Have the user complete a browser login through the `iverson-oidc-default` application
   (Authorization Code + PKCE, MFA-enforced) and confirm the resulting token's `groups` claim
   contains `operators` and is accepted on `/admin/*` (see
   `docs/runbooks/grpc-admin-auth-cutover.md`'s "Known gap" section for the equivalent
   verification during a cutover deploy).

## Related

- `docs/runbooks/grpc-admin-auth-cutover.md` — the deploy-time precondition that every operator
  is already a group member before a fresh install or cutover ships.
- `docs/user-management-and-security.md#creating-a-human-user-and-granting-operator-access` — the
  fuller reference doc this runbook's steps are drawn from.

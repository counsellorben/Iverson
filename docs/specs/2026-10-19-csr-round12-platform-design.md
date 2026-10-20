# CSR round 12, sub-project 2: Platform — design

**Source review:** `docs/criticalreviews/2026-10-16-iverson-critical-security-review-12.md` (at `f7eeba79`), and `docs/security/tma.md` §6 F1, F2, F4, F5 and F6 (read only; this sub-project does not edit the TMA).
**Findings in scope:** #3 (TenantAdmin directory scan), #4 (`/v1/traces` and the console), #5 (Strimzi policy), #6 (`/metrics` and `/build` on the admin listener), #11 (TMA F1), #12 (F2), #14 (F4), #15 (F5), #16 (F6), and §7's two Requires-Verification items (Authentik 2026.8.4 superuser refusal; the `:9080` flow slugs). Side fixes found by the prototype are in §4.3.
**Base:** `main` @ `bc1be994` (sub-project 1, Server, merged).
**Sibling sub-projects:** 1, Server (merged); 3, Supply chain (#10, #17).

## Decisions (user, 2026-10-16 to 2026-10-19)

1. Platform is **one spec and one branch**, planned as four task groups in this order: identity and admin plane (§2), cluster privilege and reach (§3), transport (§4), proof (§6).
2. **Every round-11 accepted residual in scope is reopened:** the copied pod-token window, the orchestrator's global `add_user`, the address-only login throttle (NAT lockout), edge → pod certificate verification, compose plaintext, and `OPERATE ON SYSTEM`. Operator namespaces and the AKS fallback are #16.
3. **The console is for admins only** (operators and tenant admins), although it serves ordinary users by design today.
4. **Operator rights leave Authentik groups.** The API grants Operator from a per-install list of subjects; tenant admin stays an Authentik group.
5. **Istio ambient on every profile now,** including AKS and GKE, which have no vendor support statement (known risk).
6. **kind's edge moves from ingress-nginx to an Istio gateway; GCP's four Ingresses move to GKE Gateway.** Azure (AGIC) and AWS (ALB) edges stay.
7. Cloud profiles are verified by render and static checks only; kind is the live profile.

## 1. Goals and non-goals

**Goals.**
- #3: no TenantAdmin call reads more of the directory than the caller's tenant; admin RPCs and invites are bounded; Authentik is no longer a single server replica.
- #4: only admins sign in to the console or post traces; Jaeger's memory is capped.
- #14: a revocation or a tenant-status change reaches every api replica within seconds and ends open streams at their next message.
- #11, #15: no namespace-RBAC holder can mint or copy the api's or admin-automation's workload token, or exec into the api, worker, Authentik or Postgres instance pods, without being in the operators group; no IdP write can create an Operator on a Helm install; the orchestrator pin rejects a token whose pod is gone; logins are throttled per (address, account) pair as well as per address.
- #5, #6, #16: every listener is reachable only from its intended peers, including in the operator namespaces.
- #12: every in-cluster hop is mutually authenticated by the mesh; every edge verifies the api's and Authentik's pod certificates except AWS ALB; the edge → admin-ui hop serves static files and stays plaintext (round 11); compose's authentication-carrying hops are TLS.

**Non-goals.**
- Stopping a compromised api or worker process from reaching every tenant (sub-project 1, §10).
- Anything in sub-project 3.
- Bumping Strimzi from 1.1.0, unless a task requires it.

## 2. Identity and admin plane

### 2.1 TenantAdmin directory scan and capacity (#3)

**IdP client.**
- `ListUsersByTenantAsync` requests `GET /api/v3/core/users/?attributes=<JSON {"tenant_id": t}>` on **every** page. Today the loop rebuilds the next page as `?page=N` (`IdpAdminClient.cs:198`) and would drop the filter.
- The client keeps its own check that each returned user's `attributes.tenant_id` equals the tenant, because Authentik returns the unfiltered list when filtering raises a `ValueError` (Verified assumption 1).
- A new `GetUserAsync(pk)` calls `GET /api/v3/core/users/{pk}/` and returns `uid`, `username`, `email`, `is_active`, `groups` and `attributes.tenant_id`. 404 maps to "not in this tenant".
- `RequireUserInTenantAsync` (RemoveUser, SetTenantAdmin) uses `GetUserAsync` and compares `tenant_id`; it no longer lists. ListUsers and DeleteTenant's deactivation loop use the filtered list. These four are the only callers (Verified assumption 3).
- `RequireUserInTenantAsync` first requires `user_id` to be a canonical positive decimal integer; any other value is "not in this tenant" (`PermissionDenied`) with no IdP call. `GetUserAsync` also requires the returned `pk` to equal the requested pk. RemoveUser and SetTenantAdmin pass the checked record's pk, not the raw request string, to `DeactivateUserAsync`, `AddGroupAsync` and `RemoveGroupAsync`.

**Limits.**
- `RateLimitInterceptor` keeps its 50,000/min per-`sub` limiter for every RPC except the `TenantAdminGrpcService` and `TenantLifecycleGrpcService` methods. Those go to a second limiter at **30 calls per minute**, partitioned by `(service, sub)`, so each of the two services has its own 30/min bucket per caller. Over the limit is `ResourceExhausted`, as today.
- The Operator HTTP admin routes get an ASP.NET named policy `"admin"`, 30 per minute per `sub`, with a no-op twin on the pre-auth options, the pattern of `"traces"` (`Program.cs:121-130, 539`). It covers `/admin/reconcile/{type}`, `/admin/dlq`, `/admin/dlq/{id}/replay` and every `/admin/console/*` route. `/health` is not covered.
- Every admin-automation caller maps to the single user `iverson-admin-automation-workload`, so all automation shares **one** bucket per service (and one `"admin"` bucket). That includes LoadTest's tenant provisioning. The console's steady load is about four `"admin"` requests per minute per open tab (metrics and Qdrant poll every 30 s; the other reads run on mount), so several tabs fit in one operator's bucket; a refused poll shows as a failed card and retries.

**Invite quota.**
- `InviteUser` is limited to **100 per tenant per UTC day**.
- New plumbing table `"IversonInviteQuota" ("TenantId" text, "Day" date, "Count" int, PRIMARY KEY ("TenantId", "Day"))`, created by the worker in `WorkerStoreBootstrap`. The api reserves a slot before calling Authentik with `INSERT … VALUES (@TenantId, @Day, 1) ON CONFLICT ("TenantId", "Day") DO UPDATE SET "Count" = "IversonInviteQuota"."Count" + 1 WHERE "IversonInviteQuota"."Count" < 100 RETURNING "Count"`. No row returned means over quota: `ResourceExhausted` ("the tenant's daily invitation limit is reached"). A reserved slot is not returned if the IdP call then fails.
- Grants: `GRANT SELECT, INSERT, UPDATE ON "IversonInviteQuota" TO iverson_api` is added to `ApiLoginGrants.Statements`, and the table to `ApiLoginGrants.Tables`.
- `ApiLoginGrants.ReadinessProbeSql` moves to `has_table_privilege('"IversonInviteQuota"', 'UPDATE')`, as its own comment requires (`ApiLoginGrants.cs:73-81`): otherwise a login granted by an older worker reads as ready without the new grant.
- `ApiLoginGrantsPostgresIntegrationTests.cs:144` gains the three privileges.
- Rows are one per tenant per active day and are not pruned.

**Authentik capacity.**
- **Migrations move to the worker pod.** The `migrate` initContainer (`python -m lifecycle.migrate`) moves from `deployment-server.yaml:35-59` to the worker Deployment, which stays at one replica and gains `strategy: Recreate`, so two worker pods never overlap. Server and worker containers both keep `AUTHENTIK_SKIP_MIGRATIONS=true`: by default both `ak server` and `ak worker` migrate on start (Verified assumption 7), and an init container has no liveness probe that could restart it mid-ALTER.
- **Servers wait for the migration and never migrate.** Each server pod gets an init container that waits until the database is migrated for its image's version. Neither the migrator's advisory lock nor the `authentik_version_history` marker shows completion on its own: the lock is free until a migrator takes it (on a fresh install, before the worker starts), and `version_history_update` commits the new version's marker before `migrate_schemas` runs (Verified assumption 72). The plan picks a wait that observes the end of the pass. Whatever it picks must hold on a fresh install (worker not yet started) and on an upgrade (old servers still serving), and is exercised by both in the kind run.
- **Replicas.** A new value `authentik.server.replicas`: 2 on the AWS, Azure and GCP overlays, 1 on local and laptop. A PodDisruptionBudget (`maxUnavailable: 1`) and preferred host anti-affinity render only when replicas > 1, following `charts/admin-ui/templates/pdb.yaml`.
- **tls-proxy CPU limit** 100m → 500m (`charts/authentik/values.yaml:22`).
- **HttpClient timeout.** The `iverson.authentik` named client sets `Timeout = 10 s` (`Program.cs:427-445`; today the 100 s default). It spans the orchestrator token exchange, which runs inside the same `SendAsync` (Verified assumption 9).

### 2.2 Traces and the console (#4)

**Authentik.** The `iverson-api` application (the console's `iverson-oidc-default` provider) gets one expression policy binding. It admits members of `tenant-admins` and subjects on the operator list (§2.4); everyone else gets Authentik's "Permission denied" at authorize. How the policy knows the list is in §2.4.

**API.**
- A new assertion policy `ConsoleAdmin` = Operator (§2.4) or TenantAdmin.
- `/v1/traces` requires `ConsoleClient` **and** `ConsoleAdmin` (`Program.cs:882-914`).
- `/admin/console/schema` and `/admin/console/data-volume` move from any authenticated user to `ConsoleAdmin` (`AdminConsoleEndpoints.cs:60-79`).
- The `"traces"` policy becomes a chained limiter: 60 per minute per `sub` as today, plus a shared ceiling of **600 per minute across all callers**.

**Jaeger.** The Deployment gains `args:` passing `--memory.max-traces={{ .Values.maxTraces }}` (today it passes none; Verified assumption 86), a new value with chart default 50,000 (the cloud profiles keep it). Local and laptop may set a smaller value sized to their memory limit.

**Console.**
- The console reads the caller's roles from a new endpoint `GET /admin/console/roles` on the admin listener: `{ "operator": bool, "tenantAdmin": bool }`, computed with the same functions as the server policies. It requires `ConsoleClient` and has no active-tenant filter (operators carry no `tenant_id`).
- `RequireGroup` and `hasGroup` (`router.tsx:35`, `Sidebar.tsx:23`, `auth/groups.ts:57-62`) are replaced by a role guard over that response.
- The landing page requires either role and shows the cards the caller's roles allow. The "every signed-in user, degrading card by card" behaviour (`router.tsx:24-26`) and its ordinary-user tests are removed. The `performance` and `storage` routes, which render only "Coming soon", are removed.

### 2.3 Revocation and tenant-status lag (#14)

**Shared invalidation.**
- Channel `iverson_auth_invalidation`. Every revocation write and every tenant-status write issues `pg_notify` **in the same transaction** as the write. The writers are RemoveUser (both revokes), SetTenantAdmin's demotion (both revokes), DeleteTenant, Suspend and Reactivate, and also CreateTenant's insert and its rollback delete, and the worker's legacy-tenant seed (`SeedIfMissingAsync`, `Program.cs:803-807`), which notifies in the same transaction as its insert. That seed is the only worker write to either table (Verified assumption 15).
- Each api replica holds one dedicated listener connection, opened from its own Postgres connection string with `Keepalive=30`, never returned to the pool. It waits with `NpgsqlConnection.WaitAsync`. On every notification it invalidates both `TokenRevocationCache` and `TenantStatusCache`. The payload is only a hint.
- On every connect and reconnect it issues `LISTEN` first, then invalidates both caches. A notification sent while it was disconnected is not replayed. Reconnects back off from 1 s to 30 s.
- The 30 s TTL stays as the backstop. Readiness does not depend on the listener; a lost listener is logged and counted.
- Suspend and Delete rely on this shared tenant-status invalidation; they write no revocation rows.

**Skew.** A token is revoked when `iat <= revoked_at + 5 s`. `revoked_at` is Postgres's `now()` and `iat` is Authentik's clock. A fresh login within 5 s of a revocation is therefore refused, and the user logs in again. The class comment (`TokenRevocationCache.cs:16-17`) changes, `TokenRevocationCacheTests.cs:50-52` moves from +1 s to +6 s, and a +5 s boundary test is added.

**Streams.**
- A new interceptor, registered after `ActingUserInterceptor`, wraps the `IServerStreamWriter<T>` of every server-streaming RPC. Before each message it re-checks, from the in-memory caches:
  - the revocation of the primary principal's `sub` and of the acting user's `sub`;
  - the status of the effective tenant.
- A revoked principal ends the stream with `Unauthenticated`. A suspended or deleted tenant ends it with `PermissionDenied` "Tenant '…' is not active.", as the unary paths do.
- The wrapper implements both `WriteAsync(T)` and `WriteAsync(T, CancellationToken)` and passes `WriteOptions` through: the interface default of the two-argument form throws when the token can be cancelled (Verified assumption 17).

**Refresh tokens.** `iverson-acting-user`'s `grant_types` becomes `["authorization_code"]` (`secret-service-clients.yaml:363`). No client uses that provider; LoadTest's own test-only client keeps `refresh_token`.

### 2.4 Operators outside Authentik groups (closes global `add_user`)

**The list.**
- Operator is granted by the API to a principal whose `sub` is on a fixed per-install list, the chart value `global.operatorSubjects` (a list of strings). Render fails when it is empty on the AWS, Azure and GCP overlays; their CI override files supply a dummy subject. On kind and laptop it may be empty (admin-automation still has Operator).
- A `sub` is `sha256(user.id + "-" + installID)`: stable within one Authentik install, different in every other, and not predictable from a username (Verified assumption 26). Onboarding reads the new operator's `uid` from the Authentik admin API (`GET /api/v3/core/users/?username=…`), or from the claims of a first token, before adding it. A recreated Authentik database changes every `sub`, so the list must then be rebuilt (runbook).
- Admin-automation keeps Operator through its `admin` scope. Its tokens can be minted only by the operators group (§3.1).

**One function.** `OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)` is true when the principal's `sub` is on the list or its `scope` contains `admin`. It is used by:
- the `Operator` named policy (`Program.cs:231-235`);
- the three hand-written acting-user checks (`Program.cs:654, 681, 717`);
- `SchemaAdminAuthorizationPolicy`: `OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)` (the list, the `admin` scope, or the compose fallback), or scope `schema_admin`, which keeps "Operator implies SchemaAdmin" (`SchemaAdminAuthorizationPolicy.cs:5-9`);
- `ConsoleAdmin` and the roles endpoint (§2.2).

Policy names are unchanged, so `AuditingAuthorizationMiddlewareResultHandler` is unaffected. The `groups` claim no longer yields Operator anywhere on Helm.

**How Authentik knows the list.** Helm renders `global.operatorSubjects` into two places: the api Deployment's `Authorization__OperatorSubjects__N` env, and the console sign-in expression in a Helm-rendered blueprint file (`blueprints-configmap.yaml`), as the orchestrator pin already renders release values. The expression is `ak_is_group_member(request.user, name="tenant-admins") or request.user.uid in [<list>]`. The release values are the single source. A change is a `helm upgrade`: the api pods roll because their template changes, and the policy changes when the worker re-applies the changed file (file watcher, or the hourly schedule). The runbook applies it at once.

**Compose.** Compose's dev operator `iverson-loadtest-bypass-user` gets its `sub` at first database init, so no static compose list can name it. Compose therefore keeps the `operators` group as a dev-only fallback:
- API setting `Authorization:OperatorGroupFallback` (unset by default) adds "or `groups` contains this name" to the function above. Only `docker-compose.yml` sets it (`operators`). The API logs a warning at startup when it is set. A render assertion on all five overlays checks Helm never sets it.
- The `operators` group and the bypass user's membership stay in `compose-only/service-clients.yaml`, together with a compose copy of the console sign-in policy that reads that group.
- `blueprints/operators-group.yaml` is removed from the shared directory. On Helm a rendered entry marks the `operators` group `state: absent`, so no stale membership looks meaningful on an upgraded install.

**Group membership writes.**
- `AddGroupAsync` and `RemoveGroupAsync` call `POST /api/v3/core/groups/{pk}/add_user/` and `/remove_user/`, which honour object-level `add_user_to_group` and `remove_user_from_group`. The user-PATCH path (`IdpAdminClient.cs:267-311`) ignores object permissions and is removed (Verified assumption 28).
- The only group the orchestrator manages is `tenant-admins` (`TenantAdminGrpcService.cs:82, 90`). The orchestrator role gets those two permissions **on that group object only**, through the blueprint entry-level `permissions:` field on the `tenant-admins` group entry (2026.8.4's importer assigns them per object: Verified assumption 29), in both carriers: the Helm `secret-service-clients.yaml` entry and the compose copy in `compose-only/service-clients.yaml`, which compose's api and the Api.Tests fixture apply. In each, the `tenant-admins` entry carrying the field comes after the `iverson-admin-orchestrator` role entry: on a fresh install the importer resolves the role with `!Find` when it reaches the entry and, if the role does not exist yet, skips the grant without error (Verified assumption 73). If the live test shows the importer does not assign them, the fallback is the API assignment `POST /api/v3/rbac/permissions/assigned_by_roles/<role>/assign/` that the probe used, run by the post-install runbook step.
- Authentik's `view_user` is global, so the orchestrator could add another tenant's user to `tenant-admins`. The client-side tenant check (`RequireUserInTenantAsync`) before every add stays.
- The orchestrator keeps `change_user` on users it created (deactivation) and global `add_user` (invites). CreateTenant still creates its admin directly into `tenant-admins`. With these the orchestrator can still place a user in any non-superuser Authentik group, but no group yields Operator on Helm any more (§7).
- `secret-service-clients.yaml:521-522` claims `groups.py`'s `add_user` has no `enable_group_superuser` check. That was true on 2026.5.3 and is stale on 2026.8.4, which refuses superuser groups there; the comment is corrected.

**Everything else that names the group** is updated:
- `operators-group.yaml` and its compose duplicate (see Compose above);
- runbooks `operator-access-onboarding.md`, `admin-console-landing-page-usage.md` and `grpc-admin-auth-cutover.md`;
- `docs/user-management-and-security.md` and `Iverson.AdminUI/README.md`;
- the ten Api.Tests files that pin the derivation (Verified assumption 27);
- the console tests (`RequireGroup.test.tsx`, `Sidebar.test.tsx`, `router.test.tsx`, the widget and landing tests);
- `test_dialogue_patterns.py`'s synthetic `groups`.

### 2.5 Login and onboarding (#15)

**MFA.**
- The stock stage `default-authentication-mfa-validation` gets `not_configured_action: deny` on every Helm profile where `global.provisionTestIdentities` is false (AWS, Azure, GCP). A user with no device is refused; a user with one is challenged. The override survives a worker restart and a re-apply of the stock blueprint (Verified assumption 19).
- Profiles that provision test identities (compose, kind local, laptop) keep `configure`. Their scripted identities enrol TOTP at first login (`mint_acting_user_token.py:310-315`, `AuthentikFlowExecutorClient.cs:248-250`), so first-come enrolment remains on dev profiles only (§7).
- Helm renders the value into `mfa-enforcement.yaml` the way it already rewrites `recovery-flow.yaml` (`blueprints-configmap.yaml:13-17`). The static file keeps `configure` for compose and the test fixture.
- **Operator onboarding** switches to a recovery link: the operator's account is created in the admin UI over the in-cluster route, and a link is minted with `POST /api/v3/core/users/{pk}/recovery/` (or the worker's `_create_recovery_link`, `csr11-identity-plane.md:84-99`). Its host is rewritten to the public `https://authentik.<ingressHost>`, because the link carries the host it was requested on. Recovery enrols TOTP before the password is set.

**Throttle (replaces address-only).**
- One expression policy, `iverson-login-throttle`, returns **true when the attempt must be refused**:
  - **pair:** the rows for the client address whose identifier is the pending user's username or email have a summed score ≤ −5;
  - **address:** the sum of all negative row scores for the client address is ≤ −50.
- It is bound to a new deny stage, `iverson-login-throttled`, following `superusers.yaml`'s pattern (`blueprints-configmap.yaml:170-230`), with `evaluate_on_plan: false` and `re_evaluate_policies: true`:
  - `default-authentication-flow` at order 12, after identification (10) and before the superuser refusal (15) and the password (20);
  - `iverson-recovery` at order 0, before any prompt, device or password write.
- It is never a policy on the password stage's binding. A failed policy there drops the stage from the plan, and the login stage does not check that a password stage ran (Verified assumption 21).
- It is not a flow-level binding on `iverson-recovery` either: a recovery link carries a pre-built plan, which skips flow-level policies. Stage-level bindings apply to links minted after the change; **links minted before it skip the throttle** until they expire (15 minutes).
- The old flow-level binding of `iverson-login-failures-by-address` on the login flow, and that policy, are marked `state: absent`.

**How the counts work** (Verified assumption 22):
- Each (address, identifier) row is clamped at −5 by Authentik's default limits. The deny stage runs once per login plan, when identification completes, so the pair rule refuses **new login plans** once the pair's rows reach −5: after 5 failed attempts for a known username. An unknown one scores −2 per attempt (identification and login both fail), and its identification's −1 is written before order 12 runs, so its 3rd plan is refused after 2 failed attempts (Verified assumption 90). Password retries inside a plan that has passed order 12 are bounded only by the password stage's own cancel rule (`failed_attempts_before_cancel`, default 5, counted as the score drop since the plan started) and, on the cloud profiles, the edge's per-address login rate limit (`authentik_login_rate_limit`, round 11). A plan that starts with the pair at 0 is cancelled at its 5th wrong password; one that starts with the pair between −1 and −4 is never cancelled by the password stage, because the clamp keeps the drop below 5 (Verified assumption 65).
- The address cap is a sum over rows, each floored at −5, so it is reached only once failures span at least 10 identifiers. Failures concentrated on fewer accounts are stopped first by the pair rule.
- **Expiry** is 1 h, set install-wide by `AUTHENTIK_REPUTATION__EXPIRY=3600` in `<rel>-authentik-config` (both server and worker read it) and in compose. It is not a brand or API setting. A row's expiry is fixed at its first failure. The expired-row cleanup runs every 5 minutes, and until it runs, new failures for that pair are invisible (§7).

**Port-forward address.**
- The tls-proxy's two server blocks set `proxy_bind 127.0.0.2`.
- `AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS` becomes `127.0.0.2/32`; `127.0.0.1/32` and `::1/128` are dropped (`charts/authentik/templates/configmap.yaml:18`, `docker-compose.yml:385`).
- A `kubectl port-forward` caller reaches 9000 from 127.0.0.1, so it is untrusted and its `X-Forwarded-For` is ignored; it is recorded as 127.0.0.1. Every port-forward caller therefore shares one address for the throttle.
- No new listener: the issuer `http://<rel>-authentik:9000/` and every `:9000` consumer stay unchanged (Verified assumption 24).
- Compose's `deploy/compose-tls-proxy.conf` gains `proxy_set_header X-Forwarded-For $remote_addr` and the same `proxy_bind`. Today it passes the client's header through.

### 2.6 Workload identities and Authentik secrets (#11, Authentik half)

**TokenReview on the orchestrator pin.**
- The Authentik server pod gets a native sidecar `tokenreview-relay`, the worker's `jwks-relay` pattern. It runs `kubectl proxy --address=127.0.0.1 --port=8001 --accept-paths='^/apis/authentication.k8s.io/v1/tokenreviews$' --reject-methods='^(GET|PUT|PATCH|DELETE)$'`, on its own projected token, with no `--www` and no startup probe.
- RBAC: a ClusterRole `<rel>-<ns>-authentik-tokenreview` whose only rule is `create` on `tokenreviews`, bound by a ClusterRoleBinding to the existing `<rel>-authentik` ServiceAccount. The server and worker share that account.
- NetworkPolicy: the server pods get API-server egress (`iverson.apiServerEgress`), as the worker already has (`networkpolicies.yaml:616-626`).
- The orchestrator pin keeps its claim checks and adds a TokenReview of the presented assertion, with `timeout=2` and audience `iverson-authentik-orchestrator`. It requires `status.authenticated` true and `status.user.username` = `system:serviceaccount:<ns>:<rel>-api`. It refuses (`invalid_grant`) on any exception, on any response status outside 2xx (a successful review answers 201), and on a body whose `status.authenticated` is not true or whose `status.user.username` differs.
- It keys on `context["oauth_jwt"]` and the request, not `request.user`, which is anonymous on a first exchange.
- The raw assertion is read from the token request's form body. The plan's live test confirms the field is reachable from the policy.
- Effect: a token copied from a pod stops working about 10–12 s after the pod is deleted, instead of up to 600 s. The Authentik access token from an exchange made before then stays valid for its own 5-minute lifetime (§7).

**`iss`.**
- Both pins (orchestrator and admin-automation) compare `iss` exactly with a new required value `authentik.serviceAccountIssuer`. Render fails when it is empty.
- kind and laptop: `https://kubernetes.default.svc.cluster.local`.
- EKS: a new Terraform output from `aws_eks_cluster.this.identity[0].oidc[0].issuer`.
- GKE: an output computed from the project, location and cluster name. The format is not confirmed by Google's documentation, so the runbook checks it against the cluster's `/.well-known/openid-configuration` at first deploy.
- AKS: `oidc_issuer_enabled = true` and an output of `oidc_issuer_url`. Standard clusters below 1.34 need the issuer enabled by hand, and it cannot be disabled afterwards. The AKS issuer ends with `/`, so the comparison is byte-exact.
- The CI override files fill the value.

**Bootstrap password.**
- `AUTHENTIK_BOOTSTRAP_PASSWORD` leaves the worker (`deployment-worker.yaml:103-105`), and the `bootstrap-password` key leaves `<rel>-authentik-app` (`secret.yaml:12, 15`). The worker restarts cleanly without it, and an existing `akadmin` is not touched.
- On a fresh install no bootstrap password or token reaches either pod, so Authentik's Setup flag stays False. While it does, `/setup` plans the stock `initial-setup` flow with the context its stock binding requires, and `/` on the in-cluster route redirects there. The flow's default `any` engine mode lets that binding admit it (Verified assumption 62). Two independent closures cover it:
  - a Helm-rendered blueprint entry for the `initial-setup` flow sets `policy_engine_mode: all` and binds a policy that always returns false at flow level, so the flow is denied whatever the stock binding returns. `/setup`, and `/` on the in-cluster route, then answer 500, Authentik's unhandled denial;
  - `:9080` forwards neither `/setup` nor the flow's slug, and redirects `/` to the console (§2.7).
- Once `superusers.yaml` makes `akadmin` inactive, the bootstrap API token is refused (403). Nothing in Helm uses it; the chart already deletes it (`secret-service-clients.yaml:294-299`).

**`AUTHENTIK_SECRET_KEY` rotation** (runbook): replace the Secret value, then restart the server and worker together. Consequences:
- every browser session and in-flight flow ends;
- signed file URLs and the embedded-outpost and metrics bearer change.

Issued OAuth tokens are not affected: they are signed with the provider's certificate. No database field is encrypted with the key (Verified assumption 32). Mixed keys across pods break cookie validation, which matters with two server replicas.

### 2.7 Authentik 2026.8.4: public listener and re-verification

**`:9080` allow-list.** The public server block forwards only these, and answers 404 for everything else:
- `/if/flow/<slug>/` and `/api/v3/flows/executor/<slug>/` for exactly:
  - `default-authentication-flow`, `iverson-recovery`;
  - `default-provider-authorization-implicit-consent`, which every shipped provider uses;
  - `default-invalidation-flow`, the console provider's;
  - `default-provider-invalidation-flow`, the other providers'.
- `/flows/-/default/authentication/`, `/flows/-/default/invalidation/`, `/flows/-/cancel/`.
- `/static/`, `/media/`.
- `/ws/client/`, with WebSocket upgrade headers.
- The console's OIDC endpoints: `/application/o/authorize/`, `/token/`, `/userinfo/`, `/revoke/`, and `/application/o/iverson-api/{end-session,jwks}/` and `/.well-known/openid-configuration`.
- `= /-/health/live/`, for the GCP health check.

The two invariants:
- **`= /` redirects (302) to the console URL** instead of forwarding to Authentik's root. A login that starts without `?next` therefore returns to the console, which starts a fresh authorize, and `/if/user/` and its API calls stay closed.
- **Refused:** `initial-setup`, `default-provider-authorization-explicit-consent` (no provider uses it), the source flows, `default-user-settings-flow`, `default-password-change`, the authenticator setup flows, `/if/user/`, `/if/admin/` and the rest of `/api/`. No separate enrolment slug is needed: MFA configuration stages run inside the calling flow's plan (recovery's TOTP enrolment runs under `iverson-recovery`), and login no longer offers enrolment where MFA is `deny`.

**Re-verification on 2026.8.4.**
- Round 11's public-listener superuser refusal holds live on 2026.8.4 (Verified assumption 33). The probes are committed as `deploy/scripts/authentik-probes.sh` (§6).
- The test fixture moves to `ghcr.io/goauthentik/server:2026.8.4@sha256:e7226ae1…` (`AuthentikContainerFixture.cs:43`), the digest compose pins, and its stale `EnrollTotpDeviceAsync` comment is corrected.
- Any OAuth2 provider created through the API sets `grant_types` explicitly: 2026.8 gives `[]`, which refuses every grant.

## 3. Cluster privilege and reach

### 3.1 Admission policy (#11, cluster half)

**What renders.** The chart renders one `ValidatingAdmissionPolicy` and one binding, `<rel>-<ns>-sensitive-identities`, on every profile including kind:
- `failurePolicy: Fail`, `validationActions: [Deny]`;
- bound with a `namespaceSelector` on the release namespace;
- a new value `admission.operatorGroups` (non-empty, render-checked) names the group(s) that count as operators. On kind and laptop it is `iverson-operators`, which the tooling impersonates.

**Rules.** Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else. Cluster-admin is denied too, by design (Verified assumption 37):
- **(a) Token minting.** `CREATE` on `serviceaccounts/token` for `<rel>-api` and `<rel>-admin-automation`. The kubelet (`system:nodes`) is also admitted, so projected tokens keep mounting.
- **(b) Pod connect.** `CONNECT` on `pods/exec`, `pods/attach` and `pods/portforward` for pods named `<rel>-api-*`, `<rel>-worker-*`, `<rel>-authentik-*` and `<rel>-postgres-*` (the CNPG instances, which hold Authentik's database, and CNPG's join and initdb Job pods). It matches the pod name because a CONNECT request carries no service account. The CNPG operator needs no admission: CNPG 1.30 replaces instance pods rather than exec'ing into them or editing their images (Verified assumption 84).
- **(c) Bare pods.** Pod `CREATE` with `serviceAccountName` `<rel>-api` or `<rel>-admin-automation`. Also admitted: `system:serviceaccount:kube-system:replicaset-controller`, `…:statefulset-controller`, `…:job-controller` and `…:daemon-set-controller`.
- **(d) Workload templates.** `CREATE` and `UPDATE` of Deployments, ReplicaSets, StatefulSets, DaemonSets, Jobs and CronJobs whose pod template (old or new) uses either of those service accounts. Also admitted: the kube-system controllers that maintain those objects (`deployment-controller`, `replicaset-controller`, `cronjob-controller`, `job-controller`, `generic-garbage-collector`). Without (d), anyone who can create a Job could run a pod as `<rel>-admin-automation` through the job controller, which (c) admits.
- **(e) Ephemeral containers.** `UPDATE` on `pods/ephemeralcontainers` for the pod names in (b). An ephemeral container shares the target pod's process namespace and so its mounted tokens.
- **(f) In-place image changes.** `UPDATE` on `pods` (the main resource) for the pod names in (b), when any `spec.containers[*].image` or `spec.initContainers[*].image` differs between `oldObject` and `object`. Metadata-only pod updates are not matched (Verified assumption 77).

**Consequences.**
- `kubectl debug --copy-to`, `kubectl run --overrides` with those accounts, `kubectl rollout restart` of the api and every `helm upgrade` that changes a protected workload object (a chart-version or api template change does) need the operators group. Under PSA baseline `kubectl debug` also needs `--profile=baseline`: kubectl's default profile adds `SYS_PTRACE`.
- The repo's own tooling must run as the operators group: every command that execs, attaches or port-forwards into a `<rel>-api-*`, `<rel>-worker-*`, `<rel>-authentik-*` or `<rel>-postgres-*` pod, creates a token for `<rel>-api` or `<rel>-admin-automation`, creates a pod that uses either account, creates or updates (other than through the `scale` subresource) a workload whose pod template uses either account — a `kubectl rollout restart` of the api, or a `helm upgrade` with a protected-template change — or adds an ephemeral container to, or changes a container image of, one of those pods. Today these are:
  - the kind acting-user token script;
  - LoadTest's `kubectl create token` hint (`AdminAutomationCredentials.cs:111, 117`) and the runbook's (`csr11-identity-plane.md:117`);
  - the runbooks' port-forwards to the api and Authentik;
  - the `ak apply_blueprint` and `ak shell` execs into the Authentik worker (`csr11-identity-plane.md:55, 63-66, 86`; `csr10-infrastructure-cutover.md:223`; `grpc-admin-auth-cutover.md:65`);
  - the `printenv` execs (`kind-cluster-troubleshooting.md:345`; `user-management-and-security.md:528`);
  - the `rollout restart` of the api (`grpc-admin-auth-cutover.md:90`; `kind-cluster-troubleshooting.md:349`);
  - `kubectl cnpg psql` and every other exec into a Postgres instance (`csr12-server.md:65-66, 111`; `rls-force-maintenance-role-cutover.md:30`; the hints in `PostgresSchemaManager.cs:266, 394, 427`);
  - the `helm upgrade` the setup scripts print (`setup.sh:192`, `setup.ps1:126`) and the runbook's (`grpc-admin-auth-cutover.md:50`).

  On kind the runbooks and scripts impersonate it in addition to the admin's own groups (`--as=<user> --as-group=<own group> --as-group=<operators group>`; Helm needs `--kube-as-user` together with `--kube-as-group`). `kubectl cnpg psql` passes only `--context` and `-n` to the `kubectl exec` it spawns, so on kind it runs with a kubeconfig context whose user sets `as` and `as-groups` (passed as `--context`), or is replaced by a plain `kubectl exec` with the impersonation flags into the primary instance's `postgres` container. The runbooks and the three error-message hints cited above change accordingly (Verified assumption 89).
- No Role or RoleBinding is added.
- Installing needs cluster-scoped write on `admissionregistration.k8s.io` policies and bindings (on AKS an Entra cluster-scope role).
- **Group plumbing per cloud** (render/static only):
  - AKS: the Entra group object ID(s) already in `cluster_admin_group_object_ids`;
  - EKS: a Kubernetes group given to the operator principals through their access entry's `kubernetes_groups`, and on each entry an `aws_eks_access_policy_association` of `arn:aws:eks::aws:cluster-access-policy/AmazonEKSClusterAdminPolicy` with `access_scope { type = "cluster" }`. An entry's groups grant nothing without an RBAC binding, and the design adds none; with the association the EKS operators are cluster admins, as the AKS operators group already is. Cluster scope, because operators run the `helm upgrade`, which writes the cluster-scoped VAP, its binding and §2.6's ClusterRole and ClusterRoleBinding. The operator principals are IAM roles other than the Terraform caller: EKS creates the caller's own entry, and an IAM principal can be in only one access entry (Verified assumption 88);
  - GKE: a Google Group under Google Groups for RBAC (`authenticator_groups_config`).

  Terraform gains `access_config { authentication_mode = "API_AND_CONFIG_MAP", bootstrap_cluster_creator_admin_permissions = true }` on `aws_eks_cluster.this`, the EKS access entries and their access-policy associations, and the GKE `authenticator_groups_config`. Access entries need an API mode, and a Terraform-created cluster defaults to `CONFIG_MAP`. The provider (5.100.0) sends the bootstrap flag as `false` when it is unset inside the block, which would drop the Terraform caller's own cluster access that the root's `kubernetes` and `helm` providers use (Verified assumption 82). The first deploy confirms the groups and controller usernames reach `userInfo`.

### 3.2 Kafka listener peers (#5)

- The Kafka CR's `tls` listener (`charts/kafka/templates/kafka.yaml:13-19`) declares `networkPolicyPeers` for the api and worker pod selectors. Strimzi regenerates its 9093 rule from them, without a broker roll (Verified assumption 40).
- The comment at `networkpolicies.yaml:238-239` ("this policy is the only enforcement point") is corrected: Strimzi's generated policy is additive and is now narrowed by the peers.
- Strimzi's generated policy admits its operator on 9090/9091/8443 but not on the mesh port, and the operator reverts edits to it. So the chart's `kafka-ingress` admits the Strimzi cluster-operator (`ns=kafka`, `strimzi.io/kind=cluster-operator`) on 9090, 9091, 8443 and 15008 (§4.2).

### 3.3 Metrics listener (#6)

- A third Kestrel listener on **8082** serves `/metrics` and `/build` on the api **and** the worker. It speaks HTTPS whenever backend TLS is on: a plaintext endpoint beside TLS ones breaks `UseHttpsRedirection`. On the worker, which serves no TLS endpoint (no server certificate; `worker/templates/deployment.yaml:58-63`) and is scraped over http (`prometheus-config.yaml:38-42`), 8082 is plain HTTP like its 8081; the mesh carries Prometheus → worker (§4.1).
- These move together (Verified assumption 41):
  - the `RequireListenerPort(8081)` pins at `Program.cs:521, 579`;
  - the rate-limiter exemptions at `Program.cs:985, 1015`, which become "8081 or 8082";
  - `appsettings.json:16`'s Kestrel endpoints and the api's Helm Kestrel env (the worker takes 8082 from `appsettings.json`, as it takes 8081);
  - both headless Services, plus the ClusterIP Services' port lists;
  - the Helm and compose Prometheus scrape configs;
  - the three NetworkPolicy rules (`networkpolicies.yaml:74-84, 163, 553`);
  - compose's port publish (`127.0.0.1:8082`);
  - LoadTest's `IVERSON_HTTP_URL` default and the `--build-url` defaults of `pattern_leg.py`, `dialogue_patterns.py` and `dialogue_pool.py`;
  - `csr11-transport-network.md:94`;
  - the seven Api.Tests files.
- NetworkPolicy admits 8082 only from Prometheus (plus 15008), and the api's and worker's `AuthorizationPolicy` admit 8082 only from Prometheus's principal (§4.2). No Ingress or route names 8082.
- 8081 keeps `/admin`, `/health` and `/v1/traces`. Probes stay on 8081.

### 3.4 Operator namespaces and AKS (#16)

**Default-deny with explicit allows** in `cnpg-system`, `kafka`, `starrocks`, `istio-system`, and on kind also `tigera-operator` and the gateway namespace `iverson-edge`:

| Namespace | Allows |
|---|---|
| all | DNS; API-server egress to `apiServerCidrs` on 443/6443; 15008 beside every listed port; in the enrolled namespaces (`cnpg-system`, `kafka`, `starrocks`), ingress from `169.254.7.127/32` on each operator pod's probe ports (§4.2): CNPG 9443, Strimzi 8080, StarRocks operator 8081 |
| `cnpg-system` | ingress 9443 (webhook) from `apiServerCidrs`; egress to the release's Postgres pods on 8000 and 5432 |
| `kafka` | egress to the release's Kafka pods on 9090, 9091, 8443 |
| `starrocks` | API server and DNS only (observed), plus the `all` row's probe allow |
| `istio-system` | istiod: ingress 15012 from ztunnel and the edge gateway, 15017 (webhook) from `apiServerCidrs`; ztunnel: egress to istiod 15012. Istio 1.31.2's ambient pods are not host-network (istio-cni included), so the policy governs all of them; istio-cni needs only the common row. |
| `iverson-edge` (kind) | ingress 80/443 from any source; egress to the release's api (8080, 8081), admin-ui and Authentik (9080) pods, and to istiod 15012 |
| `tigera-operator` (kind) | none beyond the common rows. Its pods are host-network, so the policy only governs any future pod. `calico-system` and `calico-apiserver` are left to Calico's own policies. |

- The policies ship as one local chart under `deploy/terraform/modules/operators/charts/`, installed by the Terraform operators module (`helm_release`) and by kind's `setup.sh`/`setup.ps1` from the same path. Its values carry each profile's `apiServerCidrs`.
- Any further edge found in the kind run is added before merge.
- **GKE:** the private cluster has no master → node firewall for webhook ports (no `google_compute_firewall` in Terraform), so CNPG's 9443 webhook is unreachable there today. A rule admits `172.16.0.0/28` → nodes on 9443 and 15017.
- **AKS node-subnet fallback:**
  - a dedicated value `networkPolicy.nodeSubnetFallback` (a list of CIDRs, default empty) adds the node subnet as an extra peer on the api, admin-ui and Authentik edge rules;
  - it renders only with `networkPolicy.keepFallbacks: true`, through the existing fail gate (`networkpolicies.yaml:14-17`);
  - the runbook's hand-edit of `clusterCidrs` (`csr10-infrastructure-cutover.md:139-143`) is withdrawn in favour of the value.
- **AKS etcd encryption:**
  - `key_management_service { key_vault_key_id, key_vault_network_access = "Public" }` on the cluster (azurerm 3.117.1 has the block);
  - the cluster's existing user-assigned control-plane identity gets encrypt/decrypt on a **dedicated** KMS Key Vault and key.
  - The existing data-volume vault restricts public access to listed IPs, which AKS KMS does not support (Verified assumption 44). The dedicated vault allows public network access with access policies only, and keeps purge protection. The vault carries `#tfsec:ignore:azure-keyvault-specify-network-acl` (AKS KMS needs public network access with access policies) and its key `#tfsec:ignore:azure-keyvault-ensure-key-expiry` (KMS-only, purge-protected, rotated rather than expired), each with a one-line justification, following the module's existing ignores (`cluster-azure/main.tf:44, 199`; Verified assumption 63).
- **AKS node-level IMDS restriction** is preview with no azurerm argument, so it stays a residual. Pod egress is already default-deny (§7).

### 3.5 StarRocks readiness without `OPERATE ON SYSTEM`

- `EngagementHealthChecker.AnyBackendReadyAsync` (`:72`; also used by `EngagementRepository.CheckBackendAliveAsync`, the readiness gate before every StarRocks operation) runs `SELECT * FROM TABLE(generate_series(1,2))` under a 5 s query timeout instead of `SHOW BACKENDS`. Two rows mean Healthy. The query needs no grant, returns rows with a backend alive, and fails in about 0.4 s ("Backend node not found") with none (Verified assumption 45).
- The `Healthy` / `AuthPending` / `Unhealthy` mapping is kept. With no grant needed, `AuthPending` now arises only from a refused login.
- The criterion "a backend has reported MB-scale disk" is lost (§7).
- `GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%'` is dropped (`job-create-user.yaml:101`, `docker-compose.yml:116`). Upgraded stacks still hold it, so the job and compose's `starrocks-init` also run `REVOKE OPERATE ON SYSTEM FROM USER 'iverson_app'@'%'` beside the existing role revoke, after `CREATE USER IF NOT EXISTS`; it succeeds whether or not the user holds OPERATE (Verified assumption 71).
- Tests that grant OPERATE or assert `SHOW BACKENDS` change (Verified assumption 45).

### 3.6 Kubernetes versions

- EKS, GKE and AKS move from 1.30 to **1.35** (`cluster-*/variables.tf`), with their companions:
  - the EKS add-on versions pinned against 1.30 (`cluster-aws/main.tf:478-504`);
  - cluster-autoscaler's chart, whose appVersion must match (`operators/versions/Chart.yaml:22-23`);
  - the node AMI family the launch template assumes (AL2 and its root device, `cluster-aws/main.tf:524-526`), which the plan re-checks against the AMIs EKS offers for 1.35;
  - CI's `kubeconform -kubernetes-version` (`deploy-validate.yml:77, 89`, and `.gitlab-ci.yml`).
- No cloud cluster is deployed, so each cloud profile is a fresh install at 1.35; there is no cloud upgrade path.
- kind pins its node image: `kindest/node:v1.35.8@sha256:07b2536e30b803ed61d1677a79df6115f798ce64c80f9e22f6ed45afd09323c0` in `kind-config.yaml`. That image was run with kind v0.33.0, so `setup.sh`/`setup.ps1` refuse a kind older than v0.33.0. The repo pins no kind version today, and v0.24.0 defaults to 1.31.0, below Istio 1.31's supported 1.32–1.37.
- A kind cluster is recreated to change its node image.

## 4. Transport (#12)

### 4.1 Istio ambient on every profile

**Install.**
- Istio **1.31.2**: `base`, `istiod` (profile `ambient`), `istio-cni`, `ztunnel`, plus the Gateway API standard CRDs where the platform does not manage them (kind: v1.6.3).
- The four charts are not in the classic Helm index or the OCI registry for 1.31.x, so they are vendored from the release tarball (URL and SHA-256 pinned in a header) into one directory that Terraform's operators module (`helm_release`) and kind's `setup.sh`/`setup.ps1` both install from. They move back to an index once it carries 1.31.x.
- The ztunnel and istio-cni charts' default tolerations (`NoSchedule`/`Exists`, `CriticalAddonsOnly`/`Exists`, `NoExecute`/`Exists`) cover the cloud datastore pools' `NoSchedule` taints, so no override is set; an override would have to restate all three (Verified assumption 76).
- On kind (local and laptop, one shared install in `setup.sh`/`setup.ps1`), Istio is installed with kind-sized requests, so the laptop profile still fits its documented 4-CPU / 9 GB host: istiod, ztunnel and istio-cni through each chart's `resources.requests`, with istiod's autoscaler off (`autoscaleEnabled: false`, so the chart renders `replicas: 1` and no `HorizontalPodAutoscaler`): its HPA scales on CPU as a percentage of the request, so a lower request would otherwise add replicas instead of reserving less; and the gateway pod through `global.proxy.resources`. The charts' defaults (istiod 500m / 2048Mi, ztunnel 200m / 512Mi, istio-cni 100m / 100Mi) set no limits, so a lower request reserves less and does not throttle; under node contention those pods lose the reservation. The plan picks the values and re-measures `values-laptop.yaml`'s capacity comment on a 4-CPU host with them (Verified assumption 87).
- GKE sets `global.platform=gke`. EKS: if security groups for pods are ever enabled, the VPC CNI must run with `POD_SECURITY_GROUP_ENFORCING_MODE=standard` (runbook note).

**Enrolment.** The namespaces `iverson`, `cnpg-system`, `kafka` and `starrocks` are labelled `istio.io/dataplane-mode=ambient`, by Terraform and the kind scripts, all four together. On kind they are labelled only after the chart's 15008 rules exist (§5); on a cloud, always a fresh install, Terraform labels them before any release pod exists. Running pods are captured without a restart.

**Peer authentication.** The chart renders a selector-scoped `PeerAuthentication` per release workload:
- `mtls.mode: STRICT`, with `portLevelMtls: PERMISSIVE` only for ports reached from outside the mesh;
- on AWS, Azure and GCP that is api 8080 and 8081, the Authentik public listener 9080, and admin-ui's port;
- on kind no port is PERMISSIVE: the Istio gateway is a mesh client.

Port-level settings are honoured only on workload-selector policies; ztunnel ignores them on a namespace-wide policy (Verified assumption 50). The operator chart (§3.4) renders the same shape for the CNPG operator, with 9443 PERMISSIVE on cloud profiles: there the API server is off-node and outside the mesh. On kind it is host-network and bypasses ztunnel.

**What it covers.** api/worker → Postgres, StarRocks FE, Kafka, Qdrant, TEI, Ollama and Jaeger; FE ↔ BE; Prometheus → api/worker; the operators → their operands; the kind gateway → pods. There are no per-hop TLS sidecars for TEI or Ollama and no native TLS for Qdrant or Jaeger: the mesh covers them. Ollama under the mesh was not exercised live (§7).

### 4.2 NetworkPolicy and HBONE

- **15008 is added to every ingress and every egress rule that lists ports,** in the chart and in the operator-namespace chart, to the same peers. In ambient the source's egress leaves for `<dst>:15008`, so ingress alone times out every hop (Verified assumption 47).
- **Port scoping in the mesh.** Inside the mesh both sides' NetworkPolicy see only 15008, and ztunnel forwards to whatever port the sender asked for, so NetworkPolicy keeps pod-pair scoping but loses port scoping for in-mesh traffic. The chart therefore renders a selector-scoped ztunnel `AuthorizationPolicy` (ALLOW, `security.istio.io/v1`) per release workload that mirrors each of its NetworkPolicy ingress rules: `from.source.principals` = the peers' service-account principals (`cluster.local/ns/<ns>/sa/<sa>`), `to.operation.ports` = that rule's ports. Once a workload has an ALLOW policy, ztunnel refuses every connection none of its rules lists (Verified assumption 66).
  - Peers outside the release namespace are named by principal: the CNPG operator (`cnpg-system/sa/cnpg-cloudnative-pg`) → Postgres 8000 and 5432; the Strimzi cluster-operator (`kafka/sa/strimzi-cluster-operator`) → brokers 9090, 9091 and 8443; the kind gateway (`iverson-edge/sa/<gateway name>-istio`) → api 8080 and 8081, Authentik 9080 and admin-ui 8080.
  - The StarRocks FE and BE get dedicated service accounts, `<rel>-starrocks-fe` and `<rel>-starrocks-be`, through the `StarRocksCluster` CR's `starRocksFeSpec.serviceAccount` and `starRocksBeSpec.serviceAccount`. Today both run as `default`, which no principal can tell apart from any other pod without a service account. The FE and BE get one `AuthorizationPolicy` each: its selector takes `matchLabels` only (Verified assumption 79).
  - Postgres's peer rule is mirrored with the instances' own principal `cluster.local/ns/<ns>/sa/<rel>-postgres` on all ports.
  - The PERMISSIVE edge ports on the clouds (§4.1) get one more rule: `from.source.ipBlocks` = the ranges the NetworkPolicy edge rule admits (`clusterCidrs`, and `nodeSubnetFallback` where set), scoped to those ports. A principals-only policy refuses plaintext from outside the mesh. `notPrincipals` is never used (it also matches a connection with no identity, so it admits every caller), and `requestPrincipals` never matches in ztunnel.
  - Kubelet probes need no rule: they arrive SNATed and are not subject to the policy.
  - A policy change applies to new connections; connections already open may survive it.
- New chart rules for operator → operand:
  - Strimzi cluster-operator → Kafka pods on 9090/9091/8443 + 15008;
  - CNPG operator (`ns=cnpg-system`, `app.kubernetes.io/name=cloudnative-pg`) → Postgres pods on 8000 and 5432 + 15008.
- Every probed pod's ingress also admits `169.254.7.127/32` on its probe ports. That is the address Istio SNATs kubelet probes to. Calico iptables needs no such rule (Verified assumption 51), but Istio's documentation asks for it wherever NetworkPolicy is enforced, and the cloud CNIs are untested.
- The edge peers: on kind, the `ingress-nginx` namespace peers (`networkpolicies.yaml:67, 77, 503, 591`) become `iverson-edge`, plus 15008. On clouds the `clusterCidrs` peers stay.

### 4.3 Side fixes found by the prototype (bugs on `main`)

1. The chart's Postgres policy admits only app pods on 5432, so the CNPG operator cannot reach the instance status port 8000 ("Instance Status Extraction Error"). §4.2's rule admits it.
2. `charts/postgres/templates/cluster.yaml:18-22` renders `affinity.nodeSelector: {}` and `tolerations: []`. Once the status port is reachable, CNPG 1.30 sees a PodSpec difference and restarts the primary forever. Each renders only when non-empty.
3. The StarRocks FE and BE specs get `MYSQL_PWD` from `<rel>-starrocks-app`'s `root-password` (`feEnvVars`, `beEnvVars`). Otherwise a restarted BE's entrypoint (`mysql -u root`, no password) is refused after the create-user job sets root's password, and crash-loops. Because the FE and BE then log in with root's password from their first start, the create-user Job sets root's password as soon as FE-0 answers, no longer waiting for the BE count first (that wait, `job-create-user.yaml:87-96`, existed only because the BE logged in without one). It then waits for `BE_REPLICAS` backends and every FE before the rest of the batch. A BE or FE follower that starts before the password is set exits 1, unregistered (the BE after about 60 s, a follower after 120 s), and joins on its first restart after it (Verified assumption 64). `MYSQL_PWD` must always equal root's live password, so `root-password` is never changed without running `SET PASSWORD` first: `fe_entrypoint.sh`'s pod-0 discovery treats a refused login as an empty cluster, so an FE-0 rebuilt without metadata would start a second cluster (§7).
4. The chart's Postgres policy admits no traffic from the Postgres instances themselves, so on every profile with `instances` ≥ 2 (the default and the AWS, Azure and GCP overlays) a standby cannot replicate from the primary or be cloned. `postgres-ingress` gains a port-less rule from `cnpg.io/cluster: <rel>-postgres` pods, matching the Kafka and StarRocks peer rules. It stays port-less because the instances also call each other's 8000. CNPG's Cluster status stays "healthy" while a standby cannot stream, so a streaming check reads `pg_stat_replication` (Verified assumption 78).

### 4.4 StarRocks, the revoke job and HSTS

- **StarRocks:**
  - `ssl_force_secure_transport = true` in the chart's `fe.conf` ConfigMap. It is not runtime-mutable, so it takes effect at an FE restart. The upgrade runbook restarts the FE rather than relying on the StarRocks operator to roll it on a ConfigMap change.
  - The FE and BE entrypoints' `mysql` calls already negotiate TLS (MySQL 8 client default `PREFERRED`).
  - The stale comment at `configmap-fe.yaml:83-85` is corrected. The header claims "4.1.1 verbatim" while the FE image is 4.1.6, so the base is refreshed from 4.1.6's `conf/fe.conf` if it differs.
- **Revoke-cross-db Job:** `PGSSLMODE=verify-full`, `PGSSLROOTCERT` pointing at a mounted `<rel>-postgres-ca` with `items: [ca.crt]` only (the Secret also holds the CA key).
- **HSTS:**
  - The tls-proxy's `:9080` server block adds `add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always`, rendered only when `global.externalScheme` is `https`.
  - Kestrel adds `AddHsts` (one year, `includeSubDomains`) and `UseHsts` under the same condition. It emits only on HTTPS requests, so it is silent where the edge → pod hop is plaintext.
  - The main host keeps the console's own header, and kind's gateway sets the header through its `ResponseHeaderModifier`.

### 4.5 Edges

**kind and laptop: Istio gateway** (replaces ingress-nginx, archived 2026-03-24 with no further security fixes).
- `setup.sh`/`setup.ps1` create the namespace `iverson-edge` (not enrolled) before they install the operator-namespace policy chart (§3.4), then a Gateway API `Gateway` of class `istio` in it.
  - Its `infrastructure.parametersRef` ConfigMap gives the gateway pod host ports 80/443 and a ClusterIP Service, so `kind-config.yaml`'s existing `127.0.0.1:8080→80` and `8443→443` mappings reach it. It also sets the Deployment strategy to `maxSurge: 0, maxUnavailable: 1`: with host ports, the default surge pod can never schedule on a single-node cluster, so a rollout would deadlock (Verified assumption 70).
  - Its HTTPS listener's `certificateRefs` name the release's `iverson-api-tls`.
  - ingress-nginx and its 40-line justification block are removed from both scripts.
- When `global.gateway.class` is `istio`, the chart stops rendering the four Ingresses and renders instead:
  - a `ReferenceGrant` for the edge Secret;
  - a `GRPCRoute` for the six gRPC services;
  - `HTTPRoute`s for admin-api (rendered only when `api.adminConsoleEnabled`, as today), Authentik and admin-ui, with the same hosts and path sets as today's Ingresses (admin-api: `/admin`, `/health`, `/v1/traces` only);
  - a `BackendTLSPolicy` per TLS backend (hostname plus a CA ConfigMap from the internal TLS Secret's `ca.crt`);
  - `ResponseHeaderModifier` filters carrying the headers the nginx `configuration-snippet` sets today (`api/templates/ingress.yaml:30-41`, `admin-ui/templates/ingress.yaml:43-50`);
  - a `URLRewrite` filter for admin-ui's `/admin` prefix.
- Gateway → pod is mTLS through the mesh, and the app's TLS runs inside it (Verified assumption 52).
- What else changes:
  - `iverson.edgeAnnotations` keeps only the cloud branches;
  - the `_validate.tpl` guards cover the gateway block;
  - `global.trustedProxies` for local and laptop is re-checked against the gateway pod's addresses;
  - the docs naming ingress-nginx are updated (`api-ingress-nginx-grpc-design.md` is marked superseded; the runbooks `csr11-transport-network.md`, `kind-cluster-troubleshooting.md`, `admin-console-landing-page-usage.md` and `operator-access-onboarding.md`; `user-management-and-security.md:250-255`; `Iverson.AdminUI/README.md`).
- TMA TB12 describes the old peer; the TMA refresh is outside this sub-project.

**GCP: GKE Gateway** (class `gke-l7-global-external-managed`) replaces the four GCE Ingresses.
- Terraform:
  - `gateway_api_config { channel = "CHANNEL_STANDARD" }` on the cluster (provider 5.45.2 supports it);
  - a `google_compute_global_address` for the Gateway;
  - the Cloud Armor policy from `waf.tf`, whose name the overlay already carries.
- With `global.gateway.class` set to `gke-l7-global-external-managed`, the chart renders, in the release namespace (same hosts and path sets as today):
  - one `Gateway` with an HTTPS listener per host, `certificateRefs` to the existing TLS Secrets, the static address (`NamedAddress`), and **no port-80 listener**;
  - `HTTPRoute`s only. GRPCRoute is not supported on this class, so gRPC rides `HTTPRoute` to Service ports with `appProtocol: HTTP2`; the HTTPS ports carry `appProtocol: HTTPS`;
  - a `BackendTLSPolicy` for the api and Authentik Services (admin-ui stays plaintext from the edge, as on every profile), with a same-namespace ConfigMap holding `ca.crt` and the certificate hostname. Backend authenticated TLS on this class was released 2026-05-29;
  - a `GCPBackendPolicy` on the Authentik Service carrying the Cloud Armor `securityPolicy` (round 11's WAF rules), plus timeouts;
  - a `HealthCheckPolicy` per Service matching today's checks (Authentik HTTPS `/-/health/live/` on 9080; api HTTPS on 8081).
- `BackendConfig`, the `app-protocols` annotations and the GCE branch of `iverson.edgeAnnotations` go away on this profile.
- The GCE admin-ui rewrite gap (today the nginx-only `rewrite-target` does nothing on GCE) is closed by the `URLRewrite` filter.
- gRPC-Web is plain HTTP on the same route; the class documentation does not mention it, so it is checked at first deploy.

**Azure (AGIC):** unchanged. It verifies the pod certificate once the trusted-root step is done.
**AWS (ALB):** unchanged. AWS documents that the load balancer does not validate target certificates and relies on VPC packet-level authentication (§7).

### 4.6 Compose TLS

- `generate-compose-secrets.sh`'s CA signs, in the same pass as today's Authentik certificate (which also gains `localhost`; today it names only `authentik-server`), certificates for:
  - Postgres (`postgres`, `localhost`);
  - the StarRocks FE (`starrocks`, `localhost`);
  - Kestrel (`iverson-api`, `iverson-worker`, `localhost`, `127.0.0.1`);
  - Qdrant (`qdrant`, `localhost`).

  All are rewritten together whenever any is missing or near expiry, and the CA key is still deleted afterwards (`generate-compose-secrets.sh:42-63`). The CA carries `keyUsage = critical, keyCertSign, cRLSign`: Python 3.14's default context refuses a CA without it, and today's has none (Verified assumption 67).
- **Postgres** serves TLS (`ssl=on`). The key is mounted read-only and, at container start, copied before `docker-entrypoint.sh` runs into a file owned by `postgres` with mode 0600, which `ssl_key_file` names. The host files stay 0644 as today. The api, the worker and Authentik connect `VerifyFull`/`verify-full` against the compose CA. Plaintext is still accepted from host tools: the image's `pg_hba.conf` ends with `host all all all scram-sha-256`, which admits both (Verified assumption 74).
- **The StarRocks FE** loads a keystore built from its certificate, and the api and worker connect `SslMode=VerifyFull`. Compose does not force secure transport.
- **Kestrel** serves HTTPS on 8080, 8081 and 8082 with the compose certificate.
- The worker's compose healthcheck (`docker-compose.yml:690`) sends its `GET /health` over TLS with the runtime image's `openssl`, keeping the `CMD-SHELL "bash -c '…'"` wrapper and the ` 200 ` test: `printf "GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" | openssl s_client -quiet -verify_return_error -CAfile /etc/iverson/authentik-ca/ca.crt -verify_hostname localhost -connect 127.0.0.1:8081 2>/dev/null | head -n 1 | grep -q " 200 "`. The mounted CA is the compose CA that signs the Kestrel leaf. The api's TCP-open healthcheck (`:576`) is unaffected (Verified assumption 83).
- **Qdrant** serves TLS as Helm does (`QDRANT__SERVICE__ENABLE_TLS`, `QDRANT__TLS__CERT`/`KEY`), and the api and worker set `Qdrant__CertPath` to its certificate, which the client pins by SHA-256 thumbprint (`ServiceCollectionExtensions.cs:36-44`). The API key and the per-tenant JWTs then travel inside TLS.
- **Authentik:** compose publishes the existing tls-proxy (8443) on `127.0.0.1:9443` and no longer publishes 9000. The api and worker set `Authentication__ExternalIssuer=https://localhost:9443/` and `Authentik__PublicBaseUrl=https://localhost:9443`. The issuer is the scheme plus the Host header the client sends, so every host consumer dials exactly `https://localhost:9443` and stops forcing the Host `authentik-server:9000`: the mint script, LoadTest, ClientConformance, `pattern_leg.py`, the console's dev OIDC authority and the tests that pin those defaults. The scheme is `https` only while Authentik trusts the proxy, so §2.5's `proxy_bind 127.0.0.2` and `TRUSTED_PROXY_CIDRS=127.0.0.2/32` ship in the same change.
- Every host consumer moves to `https://localhost:…` and dials `localhost`, not `127.0.0.1`: the Postgres, FE and Qdrant leaves carry no IP SAN. In-network consumers keep the compose service names their leaves carry (Prometheus → `iverson-api`, `iverson-worker`). Each consumer trusts `deploy/compose-tls/ca.crt` through its runtime's standard setting (Verified assumption 75): `SSL_CERT_FILE` for .NET and Go; in Python, `SSL_CERT_FILE` for urllib **and** `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH` for grpcio, since each ignores the other's; `NODE_EXTRA_CA_CERTS` for Node; for Java, a PKCS12 truststore passed with `-Djavax.net.ssl.trustStore`; the OS or browser store for the console; `tls_config.ca_file` for Prometheus. A process that also reaches public endpoints (the Agents → Anthropic) points `SSL_CERT_FILE` at a combined bundle. The consumers:
  - LoadTest and ClientConformance, with the latter's five SDK drivers. The harness (`ClientConformance/Program.cs:111-114`) and the five drivers (`Auth.cs:52, 65`; `main.go:507, 889`; `Driver.java:177`; `driver.py:376-381`; `driver.ts:390, 399`) hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme;
  - LoadTest's StarRocks connection string (`Program.cs:39`, `127.0.0.1`);
  - the Python scripts and `stack.py`;
  - the host consumers of Qdrant (`stack.py:172`, which dials `127.0.0.1`; `ingest.py:159`, whose URL is a constant);
  - `prometheus.local.yml`;
  - the console's local dev configuration (its browser trusts the compose CA).

  The plan's sweep starts from Verified assumption 75's list.
- Ports stay loopback-bound. TEI, Ollama and Jaeger stay plaintext in compose; they carry no credentials.

## 5. Deployment, upgrade order and runbooks

**Order on an existing kind cluster.** No cloud instance is deployed, so the clouds have no upgrade order: a cloud install is the single-pass Terraform apply (Istio, the operator-namespace chart and the labels), then the chart install. The node image pin means a new kind cluster; the upgrade-from-`main` check installs `main`'s chart, with Authentik 2026.5.3, on it first.
1. Install Istio (§4.1) and the Gateway API CRDs.
2. `helm upgrade --wait` the release, run as the operators group. This renders the 15008 rules, the operator → operand rules, PeerAuthentication, the VAP, the routes and the Authentik changes. 15008 rules and PeerAuthentication are inert before enrolment. The post-upgrade `REVOKE OPERATE` then runs once the new pods are Ready; a terminating previous-release pod may still briefly fail readiness, as with today's role revoke.
3. Restart the StarRocks FE (`ssl_force_secure_transport` is read only at start, §4.4).
4. Create the namespace `iverson-edge` (not enrolled), then install the operator-namespace policy chart.
5. Label the four namespaces together.
6. kind: uninstall ingress-nginx, then create the gateway (the two compete for host ports 80/443). This is a short outage on a dev cluster.
7. Apply the changed Authentik blueprints (`csr11-identity-plane.md` §2), as the operators group.

**Runbooks:**
- an upgrade note covering the steps above;
- operator onboarding by recovery link and `sub` list;
- rebuilding the operator list after an Authentik database recreation;
- the operators group for exec, port-forward, token minting, restarts of the api and Helm;
- `AUTHENTIK_SECRET_KEY` rotation;
- the AKS fallback value;
- the GKE issuer check;
- EKS: VPC CNI `POD_SECURITY_GROUP_ENFORCING_MODE=standard` if security groups for pods are enabled;
- first-deploy cloud checks (§7): as an operator principal on EKS, GKE and AKS, `kubectl auth can-i` for exec, port-forward, token creation and the protected `helm upgrade`; on AWS, a second `terraform plan` showing no change to `aws_eks_cluster.this`;
- existing docs this design makes wrong:
  - the compose endpoints, `http://localhost:9000` → `https://localhost:9443` (issuer `https://localhost:9443/`) and the API's `http://localhost:808x` → `https://localhost:808x`, in `operator-access-onboarding.md:20`, `client-conformance-matrix.md:30`, `admin-console-landing-page-usage.md:80, 138-139`, `user-management-and-security.md:211, 229, 305-307, 526` and `Iverson.AdminUI/README.md:23, 57-58`;
  - `csr12-server.md:5, 87`, where `iverson_app` no longer holds `OPERATE ON SYSTEM`;
  - `csr11-identity-plane.md` §5, rewritten for §2.5's pair and address rules, the 1 h expiry, and recovery links being throttled too;
  - `Iverson.Server/Iverson.LoadTest/README.md:10, 138, 166, 167, 174`: the compose defaults `IVERSON_GRPC_URL` `https://localhost:8080`, `IVERSON_HTTP_URL` `https://localhost:8082`, the acting-user Host header no longer forced to `authentik-server:9000`, and `IVERSON_STARROCKS_CS` on `localhost` with TLS (§3.3, §4.6);
  - `integration-test-flake-signatures.md:36-42`, where the readiness check no longer requires reported backend capacity (§3.5);
  - `user-management-and-security.md:326, 345, 358, 370, 390`, the five SDK snippets' plaintext connection to the compose API at `iverson-api:8080`, which serves HTTPS once §4.6 applies.

## 6. Testing and verification (proof)

**Unit and integration** (each new guard gets a mutation that the suite must catch):
- **IdP client:** request shapes, including that every page carries the `attributes` filter; a user whose `tenant_id` differs is dropped even when the server returns it; single-user lookup for RemoveUser and SetTenantAdmin; a non-integer `user_id` is refused with no IdP request. Live on 2026.8.4: the filter and the per-pk lookup with the orchestrator token.
- **Limits:** the 31st TenantAdmin call in a minute is refused while other RPCs pass; the `"admin"` policy refuses the 31st admin HTTP call; the 101st invite of a tenant's day is refused across two api instances sharing Postgres; the relay's shared ceiling.
- **Console:** the sign-in policy live (admin admitted, ordinary user refused, operator-list subject admitted); `/v1/traces` 403 for an ordinary console token; schema and data-volume 403 for ordinary users; the roles endpoint; the console's role guard.
- **Revocation:** a NOTIFY from one instance invalidates another's caches; a listener reconnect invalidates; the 5 s skew boundary; a revoked user's stream ends `Unauthenticated` at its next message and a suspended tenant's with `PermissionDenied`, through both `WriteAsync` overloads; a seed insert invalidates another instance's cached miss.
- **Operator list:** Operator from the list on the policy, the three acting-user sites and SchemaAdmin; the compose fallback grants SchemaAdmin; the `operators` group no longer grants Operator without the compose fallback; the fallback is absent from every Helm render.
- **Authentik live on 2026.8.4** (fixture moved): the superuser refusal running the shipped `superusers.yaml`; the throttle (a new login plan refused once the pair reaches −5, the address cap at 50, a recovery link refused); MFA deny; `add_user`/`remove_user` scoped to `tenant-admins` and refused on other groups; the `iss` pin; the TokenReview pin (needs a Kubernetes API, so in the kind run), including that an exchange from the live api pod succeeds.
- **Listeners:** the 8082 gate (`/metrics` and `/build` answer only on 8082; 8081 refuses them).
- **StarRocks:** readiness with no grant, Healthy with a backend alive, Unhealthy within the timeout with none, and the revoke leaving `iverson_app` without OPERATE.

**Chart and Terraform:**
- renders, `helm lint`, kubeconform and kube-score on all five overlays; tfsec;
- render assertions:
  - the VAP and binding names and rules;
  - Kafka `networkPolicyPeers`;
  - 15008 in every ingress and egress rule that lists ports;
  - the operator-namespace policies;
  - the AKS fallback failing without `keepFallbacks`;
  - the GCP Gateway, routes, `BackendTLSPolicy` and `GCPBackendPolicy`;
  - AKS `key_management_service`;
  - the kind gateway routes;
  - `serviceAccountIssuer` and `operatorSubjects` required on cloud overlays;
  - Authentik PDB and anti-affinity only when replicas > 1;
  - the KMS vault and key carry exactly the two tfsec ignores (§3.4);
  - Postgres admits its own instances in both the NetworkPolicy and the AuthorizationPolicy;
  - an `AuthorizationPolicy` per release workload whose principals and ports match its NetworkPolicy ingress rules, with an `ipBlocks` rule only on cloud PERMISSIVE ports; the StarRocks FE and BE service accounts.

**Committed probe scripts** under `Iverson.Server/deploy/scripts/`:
- `authentik-probes.sh`: the superuser refusal on `:9080` for login and recovery; the in-cluster route admitting it; the slug allow-list, including `initial-setup` and `/if/user/` answering 404 and `/` redirecting to the console; on the in-cluster route, `/setup` and the `initial-setup` executor show no prompt; a failed login through `:9080` produces a reputation row whose `ip` is the edge-reported client address, not `127.0.0.2` or `127.0.0.1`, and a failed login through a `kubectl port-forward` to 9000 a row with `ip` `127.0.0.1`, both read from `GET /api/v3/policies/reputation/scores/?identifier=<user>`.
- `mesh-check.sh`: every release pod enrolled; plaintext from an unenrolled pod to a StarRocks BE port refused; ztunnel reporting mTLS for FE ↔ BE; a peer refused on a port its rule does not list (api → Jaeger 16686); the edge gateway admitted to the api.
- `admission-check.sh`: a non-operator refused token minting, exec, port-forward, a bare pod, a Job and an ephemeral container for the protected identities; the operators group admitted; a non-operator refused an in-place image change on an api pod, the operators group admitted; a non-operator refused exec into a Postgres instance pod, the operators group admitted; a legacy SA-token Secret's token refused by both pins.

**kind live run:**
- a fresh install (Istio ambient, VAP, operator-namespace policies, gateway edge), in which every FE and BE reaches `Alive: true`;
- the round-12 Task-13-style checks (compose, kind, the conformance harness);
- an upgrade from `main` installed with Authentik 2026.5.3 (the release before 2026.8.4), so the worker migrates to 2026.8.4 while the old servers serve;
- the three probe scripts.

Cloud profiles are render and static only.

## 7. Known issues / accepted as out of scope

- **AWS ALB → pod is not certificate-verified.** AWS documents that the ALB does not validate target certificates and relies on VPC packet-level authentication. The hop is TLS, and the mesh does not cover it, because the ALB is outside the mesh.
- **Copied pod-token window: about 10–12 s** after the pod is deleted (was up to 600 s), the API server's token-authentication cache. An orchestrator access token obtained before then stays valid for its 5-minute lifetime. A token copied from a **running** api pod still works; reaching one needs exec or node access, which §3.1 limits to operators.
- **AKS node-level IMDS restriction** is preview with no azurerm argument. Pods' egress is already default-deny; host-network pods keep access.
- **Throttle limits.**
  - Recovery links minted before the throttle change skip it until they expire.
  - After a reputation row expires, new failures for that pair are invisible until the 5-minute cleanup runs.
  - Scores clamp at ±5, so the pair rule gives an unknown username 2 failed attempts (its 3rd plan is refused) and the address cap needs at least 10 identifiers.
  - Every port-forward caller shares the address 127.0.0.1.
  - Failures against one account from many addresses are not summed; each address gets its own pair budget.
  - **Retries inside one login plan.** The pair rule refuses new plans, not password attempts. A plan that starts with the pair's rows between −1 and −4 accepts wrong passwords until the edge rate limit stops it (cloud profiles; kind and laptop have none), and a correct password still completes it. Plans started in parallel before the pair reaches −5 each keep their own retries.
- **First-come MFA enrolment remains on dev profiles** (compose, kind, laptop), whose test identities enrol at first login. Cloud profiles deny.
- **Istio ambient on AKS (Azure CNI with Azure NPM) and GKE Dataplane V2 has no vendor support statement.** GKE Dataplane V2's eBPF masquerading is the probe risk Istio documents for Cilium. Any failure is found at first deploy (user choice: on everywhere).
- **Cloud admission identities are unverified live.** That the controllers' usernames and the operators' groups reach `userInfo` as on kind is confirmed at first deploy, as is that the EKS apply creates the access entries with the Terraform caller's access intact (§3.1). A mismatch fails closed (FailedCreate events, refused commands). Also accepted unverified, each settled by a first-deploy check (§5): that an EKS operator principal, through its access-policy association, may exec, port-forward, create the protected tokens and run the protected `helm upgrade`; that a second `terraform plan` after the first apply shows no change to `aws_eks_cluster.this` (its bootstrap flag is `ForceNew`, so a diff would plan a cluster replacement); that GKE operators hold those permissions from project IAM outside Terraform; and that AKS binds the admin group to cluster-admin with `azure_rbac_enabled`. The EKS, GKE and AKS claims fail closed as refused commands; the plan check shows a replacement before it is applied.
- **StarRocks readiness no longer checks backend disk capacity.**
- **The orchestrator can still place users in non-superuser Authentik groups** (row-permission groups included) through `change_user` and global `add_user`. No group yields Operator on Helm any more. On compose, the dev-only group fallback (§2.4) still makes an `operators` member an Operator, so the compose orchestrator credential can create one.
- **Namespace RBAC still reaches Authentik's database through Secrets.** Reading Secrets gives the `authentik` role's password, and a `ValidatingAdmissionPolicy` cannot gate reads; this is TMA F1's precondition (store credentials stay namespace-readable).
- **A future CNPG version that execs into instances or edits their images in place would be refused.** CNPG 1.30's role grants both, but it used neither under the policy (Verified assumption 84); a CNPG bump re-runs VA84's sequence on kind before it merges: `postgres.instances: 2` with the instance peer rule and the CNPG operator not admitted; scale-up 1 → 2, failover by deleting the primary with the old primary rejoining, re-clone of a standby after deleting its PVC and pod, a rolling restart, a rolling image change and a switchover; the operator's account draws no admission denial.
- **Legacy SA-token Secrets are not gated.** A namespace-RBAC holder can create a `kubernetes.io/service-account-token` Secret for a protected account; its token is a non-expiring Kubernetes credential for that account, and only the pins' audience and lifetime clauses keep it out of Authentik (Verified assumption 80).
- **A compromised api or worker can still reach every tenant** (round 12 §10, carried).
- **Ollama under the mesh was not exercised live.**
- **Release history keeps old values.** Helm's stored release history keeps the bootstrap password and old operator lists until those revisions rotate out.
- **1.35 availability.** That 1.35 is offered on EKS and AKS is taken from the decision. The plan confirms each vendor's version table before pinning add-ons.
- **The edge → admin-ui hop is plaintext** on every profile (round 11). It serves the console's static files; the mesh covers it on kind, and on clouds it crosses the LB → node network.
- **An FE-0 rebuilt without metadata while `MYSQL_PWD` differs from root's live password starts a second cluster.** The operator images' entrypoints do not check the login's result. The rule in §4.3 #3 prevents it; nothing enforces it.
- **Cloud LB sources and `ipBlocks`.** That each cloud LB's traffic reaches the pod from an address inside the edge rule's ranges (not SNATed to something else) is unverified; a mismatch fails closed at first deploy (edge 503s).

## 8. Out of scope

- Strimzi upgrade beyond 1.1.0; `STRIMZI_OPERATOR_NAMESPACE_LABELS`.
- An OTel Collector that rewrites `service.name` on browser spans.
- Calico's own namespaces (`calico-system`, `calico-apiserver`) and `kube-system` policies.
- Editing `docs/security/tma.md` (a later refresh records this sub-project).
- Azure and AWS edge replacement.

## Choices made while writing

Points the brief left open, decided here with the simplest option consistent with the decisions:

1. **Authentik learns the operator list from Helm.** `global.operatorSubjects` is rendered into both the api env and the console sign-in expression. The release values are the one source; changes apply at the blueprint re-apply.
2. **Compose keeps the `operators` group as a dev-only fallback** (`Authorization:OperatorGroupFallback`, set only by compose, warned at startup, asserted absent from Helm renders). The bootstrap-writes-the-sub alternative needs a compose one-shot and a file-fed setting.
3. **Helm marks the `operators` group absent** on upgraded installs.
4. **`:9080`'s root redirects to the console**, so every login without `?next` returns there; `/if/user/` stays closed.
5. **Object permissions on `tenant-admins` come from the blueprint entry's `permissions:` field.** The API assignment is the fallback (see "Could not reconcile").
6. **Invite quota:** a UTC day; the slot is reserved before the IdP call and not returned on failure; rows are not pruned.
7. **NOTIFY also on CreateTenant's insert and rollback.**
8. **Admin RPC limits are per (service, sub);** one `"admin"` HTTP bucket per sub covers every `/admin/*` route except `/health`.
9. **The TokenReview relay reuses `<rel>-authentik`** (shared with the worker) and has no startup probe.
10. **MFA `deny` is rendered only where `provisionTestIdentities` is false.**
11. **One throttle policy and deny stage** for pair and address. The address cap is a sum of negative scores ≤ −50; pair rows match the pending user's username or email; the old flow-level binding is removed.
12. **Compose:** Authentik → Postgres is also `verify-full`; Postgres offers TLS without refusing host plaintext; StarRocks does not force secure transport.
13. **Kubernetes bump companions** (EKS add-ons, cluster-autoscaler, kubeconform), and setup scripts that refuse kind older than v0.33.0.
14. **Istio charts vendored** from the pinned release tarball.
15. **Operator-namespace policies as one local chart** shared by Terraform and kind.
16. **kind gateway created by the setup scripts** in `iverson-edge`; the chart renders routes. GRPCRoute on Istio (as verified), HTTPRoute on GKE.
17. **The `169.254.7.127/32` probe allow is rendered on every profile.**
18. **Jaeger `maxTraces` chart default 50,000;** local overlays may lower it.
19. **Console:** the `performance` and `storage` stub routes are removed; the landing page is admins-only with cards by role; the roles endpoint is `GET /admin/console/roles`.
20. **The `bootstrap-password` key also leaves the Secret.**
21. **HSTS:** one year, `includeSubDomains`, no `preload`.
22. **Stream re-checks live in a new interceptor** after `ActingUserInterceptor`.

**Could not reconcile with the brief (decided here, for the user to confirm):**
- **Blueprints *can* assign per-object permissions in 2026.8.4.** The brief says they cannot. The probe's finding is that `RoleObjectPermission` cannot be a blueprint model; the entry-level `permissions:` field is a different mechanism (Verified assumption 29). Live confirmation is in §6.
- **"The existing Key Vault" cannot back AKS KMS.** Its IP-restricted firewall is a mode AKS KMS does not support, so the design adds a dedicated vault.
- **The admission policy gained rules (d), (e) and (f).** Rule (c) alone admits any pod the job, replicaset or statefulset controllers create, so a Job naming `<rel>-admin-automation` would bypass it; ephemeral containers bypass (b); an in-place image change keeps the pod's service account and token, which (f) closes.
- **EKS and GKE carry no operator group today.** The design adds an EKS access entry's `kubernetes_groups` and an access-policy association, and GKE Google Groups for RBAC, verified only statically.
- **MFA `deny` everywhere would lock out the dev profiles' scripted test identities.** The design scopes `deny` to profiles without test identities.
- **Compose Kestrel HTTPS reaches every compose consumer.** Each runtime's custom-CA path is now known (Verified assumption 75); the harness and the five SDK conformance drivers need a code change, because they hard-code plaintext channel credentials.
- **kind v0.24.0 cannot be assumed to run the 1.35.8 node image.** The design requires kind ≥ v0.33.0, the version tested.

## Verified assumptions

Evidence was gathered on 2026-10-19 against `bc1be994`. "Live" means a throwaway Authentik 2026.8.4 stack (`ghcr.io/goauthentik/server@sha256:e7226ae1…`, the compose digest, with the repo's 38 blueprints applied), or a throwaway kind cluster (kind v0.33.0, `kindest/node:v1.35.8`, Calico v3.32.2, CNPG 1.30.0, Strimzi 1.1.0, StarRocks operator 1.11.5, Istio 1.31.2, Gateway API v1.6.3). "Source" means the 2026.8.4 tag or image contents. The probe records are in the session scratchpad (`verify-static.md`, `verify-static-2.md`, `verify-docs.md`, `verify-authentik/results.md`, `kind-proto/results.md`).

**Identity and admin plane**

1. **The `attributes` filter is server-side and keeps pagination.** Live: `?attributes={"tenant_id":"t1"}` returned only t1's users with `uid` and the envelope; `page_size=1` paged with the filter; malformed JSON gives 400; it works with the orchestrator token. Each JSON key matches `attributes__<key>` exactly. Source: `core/api/users.py:598-613` returns the unfiltered queryset on `ValueError`. The repo's loop drops the filter after page 1 (`IdpAdminClient.cs:198`).
2. **Per-pk lookup works with `view_user` only.** Live: `GET /api/v3/core/users/12/` with the orchestrator token returned `uid`, `is_active`, `groups` and `attributes.tenant_id`; an unknown pk gives 404.
3. **The directory callers are four.** `TenantAdminGrpcService.cs:52` (ListUsers), `:62` and `:80` via `RequireUserInTenantAsync` (`:114-122`, which lists and picks by id), and `IdpAdminClient.cs:210-217` (deactivation, only from `TenantLifecycleGrpcService.cs:84`). `command grep` finds no other caller.
4. **Limits can be split without touching other RPCs.**
   - `RateLimitInterceptor.cs:10-18` is one 50,000/min limiter keyed by `sub` (`:41`), on Unary and ServerStreaming only. No proto declares client-streaming or duplex RPCs.
   - `ServerCallContext.Method` names the service.
   - The HTTP admin routes bypass the interceptor (`Program.cs:644-756`, `AdminConsoleEndpoints.cs:55-83`) and see only the 6,000/min post-auth limiter (`Program.cs:996-1005`).
   - Admin-automation is one user (`secret-service-clients.yaml:641, 660-666`).
5. **`ResourceExhausted` is already what clients get when limited** (`RateLimitInterceptor.cs:46`), and no SDK or console code special-cases it (`command grep -ri resource_?exhausted` over `Iverson.Clients`, `Iverson.AdminUI/src`).
6. **A new api grant has three obligations:** `ApiLoginGrants.cs:57-61` (add the table to `Tables`), `:73-83` (move `ReadinessProbeSql`), and `ApiLoginGrantsPostgresIntegrationTests.cs:144` (the exact grant list). The pattern is `WorkerStoreBootstrap.cs:25-52`.
7. **Authentik 2026.8.4 migrations.** Source:
   - `ak server` migrates in `gunicorn.conf.py:175`;
   - `ak worker`'s first worker process (id 1000) migrates in `lifecycle/worker_process.py:154-157`;
   - both are skipped by `AUTHENTIK_SKIP_MIGRATIONS` (`lifecycle/migrate.py:124-125`);
   - the pass holds `pg_advisory_lock(1000)` (`:57-63`);
   - `python -m lifecycle.migrate` is the full sequence, while `ak migrate` is only `manage migrate`;
   - the server is ready only after gunicorn has served, which is after migration.

   The repo's single migrator and its wedge rationale are at `deployment-server.yaml:35-59` and `deployment-worker.yaml:84-91`; the worker's liveness is an exec probe (`:117-118`). Live: `python -m lifecycle.migrate` exited 0 on a fresh database in 2 m 22 s.
8. **Two server replicas are safe apart from migration.** The server pod's only writable volume is the tls-proxy's `/tmp` emptyDir (`deployment-server.yaml:211-212`); the tls-proxy has no `limit_req` zones (`configmap-tls-proxy.yaml:10-86`); the JWKS relay is in the worker (`deployment-worker.yaml:28-72`); live, no Redis is needed. kube-score requires a PDB at replicas ≥ 2, and the accepted pattern is `charts/admin-ui/templates/pdb.yaml`.
9. **The Authentik client has no timeout, and the exchange runs inside the call.** `Program.cs:427-445` sets none (100 s default; the Prometheus client sets 5 s at `:468`). `AuthentikOrchestratorTokenHandler.cs:31-35, 55-70` exchanges inside the same `SendAsync`, so `HttpClient.Timeout` covers both. No call has a reason to exceed 10 s (static; no recorded latencies).
10. **An application policy gates console sign-in.** Live: a policy `ak_is_group_member(…"tenant-admins") or …` bound to an OAuth2 application gave members a 302 with a code and non-members "Permission denied", in both `any` and `all` modes.
11. **The console is the only `iverson-oidc-default` client, and it serves ordinary users today.** LoadTest and conformance use `iverson-loadtest-human` (`LoadTest/Program.cs:46`, `TokenBroker.cs:56`). `router.tsx:24-29` leaves landing, `performance` and `storage` unguarded; the latter two render only "Coming soon" (`PerformancePage.tsx`, `StoragePage.tsx`). `/admin/console/schema` and `data-volume` require only authentication (`AdminConsoleEndpoints.cs:60-79`).
12. **`/v1/traces` and the exporter.** The endpoint is `RequireAuthorization("ConsoleClient")` plus the active-tenant filter and `"traces"` (`Program.cs:882-914`). No existing policy is Operator-or-TenantAdmin (`Program.cs:225-248`). The console exports every signed-in user's spans (`telemetry.ts:60-120`).
13. **Jaeger is uncapped.** `charts/jaeger/templates/deployment.yaml:35-63` passes no args to `jaegertracing/all-in-one:1.62.0`. Jaeger's v1 CLI documents `--memory.max-traces` (default unbounded; live: Verified assumption 86).
14. **LISTEN/NOTIFY needs no privilege and does not replay.** Live (Postgres 16.15; semantics as 18): a no-grant login LISTENed and received a NOTIFY; a NOTIFY with no listener was not delivered to a later LISTEN. Docs: "LISTEN takes effect at transaction commit"; registrations end with the session. The Npgsql API has the `Notification` event and `WaitAsync`; keepalive is off by default.
15. **Every revocation and tenant-status write is in the two admin services, except the worker's legacy-tenant seed.** `TenantAdminGrpcService.cs:66, 71, 88, 93`; `TenantLifecycleGrpcService.cs:29, 46, 81, 100`. The worker inserts five `active` tenants at startup when `Tenancy:SeedLegacyTenants` is set (`Program.cs:803-807`; `TenantRepository.cs:15-22`, `INSERT … ON CONFLICT ("Id") DO NOTHING`), which `docker-compose.yml:678` sets and Helm sets from `global.provisionTestIdentities` (`worker/templates/deployment.yaml:108-109`). No other worker path, runbook SQL or LoadTest writer touches either table.
16. **The skew change moves one test.** `TokenRevocationCache.cs:32-39` compares `issuedAt <= revokedAt`; `TokenRevocationCacheTests.cs:50-52` uses +1 s; the other tests use −10 min, exact and null; the pipeline test uses +30 s. `revoked_at` is `now()` (`Iverson.Sql/TokenRevocationRepository.cs:21-28`). `iat` is parsed as integer seconds (`Program.cs:1047-1049`).
17. **Streams write only through `IServerStreamWriter<T>`.** Search, SearchSimilar, SearchChunks, GroupBy, Pipeline, MatchPattern and GetMany use both overloads (`ObjectSearchGrpcService.cs:138, 499, 632, 880, 965`; `ObjectRetrievalGrpcService.cs:90-151`). In Grpc.Core.Api 2.84.0 the default two-argument implementation throws `NotSupportedException` when the token can be cancelled.
18. **No client uses `iverson-acting-user`'s refresh grant.** `secret-service-clients.yaml:349-372` declares it; refresh is used only by LoadTest's flow client against `iverson-loadtest-human` (`AuthentikFlowExecutorClient.cs:329, 447`).
19. **MFA `deny` works and sticks; dev identities enrol at first login.**
    - Source: `NotConfiguredAction` = skip, deny, configure (`flows/models.py:46`).
    - Live: after the blueprint change, a device-less user got "No (allowed) MFA authenticator configured." and a TOTP user was challenged. The value survived a worker recreation and a forced re-apply of the stock flow blueprint, whose entry sets no attrs.
    - The tooling enrols on the login flow (`mint_acting_user_token.py:310-315`, `AuthentikFlowExecutorClient.cs:248-250`).
    - `provisionTestIdentities` is true only in `values-local.yaml:40` and `values-laptop.yaml:39`.
20. **A recovery link can onboard an operator without the RPC.** `POST /api/v3/core/users/{pk}/recovery/` (`IdpAdminClient.cs:107`); the worker-side link in `csr11-identity-plane.md:84-99`; the brand's `flow_recovery` is `iverson-recovery` (`recovery-flow.yaml:193-200`). The link carries the request's host (`PublicLink`, `IdpAdminClient.cs:355-376`).
21. **Stage-binding policy semantics.**
    - Source: `flows/planner.py:346-376` drops a stage whose binding policy fails; `ReevaluateMarker` returns None (skip) on failure (`flows/markers.py:41-75`); the login stage needs only `pending_user` (`stages/user_login/stage.py:165-180`).
    - Live: a deny stage at order 15 with a policy saw the pending user (`request.user` = pending user, in the server process) and refused it; an unknown username yields an unsaved pending user and the flow continues.
22. **Reputation behaviour.**
    - Source: `Reputation` is unique on (identifier, ip); `expires` is set on insert only; scores clamp at the tenant's −5/+5 (`tenants/models.py:34-35`).
    - Live:
      - `AUTHENTIK_REPUTATION__EXPIRY=3600` gave a 1 h row;
      - expired rows are hidden until `clean_expired_models` (crontab `2-59/5`);
      - an unknown username scores −2 per attempt;
      - a flow-level policy on `iverson-recovery` refused a tokenless start but not a recovery link ("Set your password");
      - a stage-level deny bound at order 1 refused a new link.
23. **Port-forward and the tls-proxy share 127.0.0.1.** `configmap.yaml:18` and `docker-compose.yml:385` trust `127.0.0.1/32,::1/128`; both tls-proxy blocks `proxy_pass http://127.0.0.1:9000` (`configmap-tls-proxy.yaml:51, 78-84`); `kubectl port-forward` dials localhost in the pod. `compose-tls-proxy.conf` sets no `X-Forwarded-For`. That a distinct `proxy_bind` source works is Linux `lo` behaviour; it is checked in the kind run.
24. **The `:9000` consumers need no change with `proxy_bind`:**
    - the api's `InternalIssuer` (`api/templates/deployment.yaml:217-219`);
    - `mint_acting_user_token.py:35-38, 404, 472-476`;
    - LoadTest's and ClientConformance's Host-header env (`TokenBroker.cs:51, 60`);
    - the runbooks (`csr11-identity-plane.md:81-104, 150-151`; `operator-access-onboarding.md:31-32`; `kind-cluster-troubleshooting.md:291-311`);
    - the kubelet probes (`deployment-server.yaml:126-137`) and the Service port.
25. **TokenReview from the server pod is feasible.**
    - Live Authentik: application policies on `/application/o/token/` run in the server's web process, not the worker; an expression policy reached a loopback HTTP server with the pre-bound `requests` (no sandbox); a refused connection became `invalid_grant`.
    - Source: the token path sets `use_cache=False` (`providers/oauth2/token/base.py:120-141`); `get_http_session()` has no default timeout (`lib/utils/http.py:76-83`); on a first federated exchange the user is created after the policy check (`token/client_credentials.py:83-135`).
    - Live kind: a relay `kubectl proxy --accept-paths='^/apis/authentication.k8s.io/v1/tokenreviews$'` with a tokenreviews-only ClusterRole answered a loopback POST, and refused GETs of other paths. A pod-bound token reviewed `authenticated=True` until its pod was deleted, then "service account token has been invalidated" 12 s later (still valid at 0 s).
    - The server's egress admits only DNS and Postgres (`networkpolicies.yaml:601-614`).
    - Not run: the federated `client_assertion` exchange itself.
26. **`sub` is a per-install hash.** Source: providers default to `HASHED_USER_ID` (`providers/oauth2/models.py:307-311`), resolved to `user.uid` (`id_token.py:139-153`), which is `sha256(f"{user.id}-{install_id}")` (`core/models.py:606-609`). No repo provider sets `sub_mode`. Changing it would re-key revocations (`TokenRevocationCache.cs:32-37`) and rate-limit partitions.
27. **Every Operator derivation site.**
    - `OperatorAuthorizationPolicy.cs:9-15` and `SchemaAdminAuthorizationPolicy.cs:10-16` (group `operators` or scope);
    - the named policies (`Program.cs:231-236`);
    - the hand-written acting-user checks (`Program.cs:654, 681, 717`);
    - the console's group checks (`router.tsx:35`, `Sidebar.tsx:23`, `groups.ts:57-62`);
    - the tests that pin it: `OperatorAuthorizationPolicyTests`, `SchemaAdminAuthorizationPolicyTests`, `AuthenticationPipelineTests`, `PrivilegedAdminEndpointTests`, `AdminConsoleEndpointsPipelineTests`, `AdminConsoleMetricsPipelineTests`, `AdminConsoleActiveTenantPipelineTests`, `InactiveTenantHttpEndpointTests`, `TenantLifecycleGrpcServiceAuthorizationPipelineTests`, `RegisterSchemaAuthorizationPipelineTests`.

    Admin-automation is Operator by scope (`secret-service-clients.yaml:694-714`). Compose's bypass user is in `operators` by blueprint (`compose-only/service-clients.yaml:310-321`); on Helm it is not (`secret-service-clients.yaml:471-485`).
28. **Group membership: only `groups/{pk}/add_user/` honours object permissions.** Live, with object-level `add_user_to_group` and `remove_user_from_group` on one tenant group and `tenant-admins`:
    - `add_user` returned 204 there and 403 on `operators` and `authentik Admins`;
    - a user PATCH of `groups` added `operators` to a user the orchestrator created (200);
    - `POST /core/users/` with `groups:[operators]` returned 201;
    - a superuser group was refused ("superuser group").

    Source: `core/api/groups.py:487, 515-522` (2026.8.4 checks `enable_group_superuser` on `add_user`); `core/api/users.py:292-308` checks superuser groups only. `RoleObjectPermission` is excluded as a blueprint model (`blueprints/v1/importer.py:60-73`). The repo's path is the user PATCH (`IdpAdminClient.cs:267-311`).
29. **Blueprint entries can assign object permissions to a role.** Source: `BlueprintEntry.permissions` (`blueprints/v1/common.py:66-85`) is applied by `Importer._apply_permissions` → `role.assign_perms(perm, obj=instance)` after each entry is saved (`blueprints/v1/importer.py:366-375, 443`). Live with its ordering condition: Verified assumption 73; §6 tests it.
30. **The service-account issuer per profile.**
    - Live kind: the discovery document and the api pod's projected token carry `iss: https://kubernetes.default.svc.cluster.local`, and the expression context exposes `iss` (`blueprints-configmap.yaml:91-108` reads `oauth_jwt`).
    - EKS: `aws_eks_cluster.this.identity[0].oidc[0].issuer` exists (`cluster-aws/main.tf:288, 292`) but is not an output.
    - AKS docs (`learn.microsoft.com/azure/aks/use-oidc-issuer`): issuer `https://{region}.oic.prod-aks.azure.com/<tenant>/<cluster>/` with a trailing slash; "enabled by default for new AKS Standard clusters on 1.34+"; cannot be disabled. azurerm 3.117.1 has `oidc_issuer_enabled` and `oidc_issuer_url` (`kubernetes_cluster_resource.go:1210, 1215`).
    - GKE: format not confirmed.
31. **The bootstrap password can leave the worker.** Live: the worker recreated without it started healthy, logged "Tenant is already setup, skipping", and `akadmin` was unchanged; once `akadmin` is inactive the bootstrap token gets 403 (`api/authentication.py:107`). Statically, `initial-setup` is forwarded by `:9080` today (`configmap-tls-proxy.yaml:79-80`), retitled but kept (`secret-service-clients.yaml:428-431`), and its stock gate is "akadmin has no usable password" plus a context only `/setup` plants (Verified assumption 62).
32. **`AUTHENTIK_SECRET_KEY` signs cookies and encrypts nothing.** Docs (`docs.goauthentik.io/install-config/configuration/`): "Secret key used for cookie signing… Changing this will invalidate active sessions." Source: every `SECRET_KEY` use in 2026.8.4 is cookie, impersonation and file-URL signing, the outpost/metrics bearer, or a sentry tag; OAuth tokens are signed with the provider's certificate. Consumers: `deployment-server.yaml:68-70, 108-110`, `deployment-worker.yaml:97-99`.
33. **Round 11's superuser refusal holds on 2026.8.4.** Live with the shipped `superusers.yaml`:
    - a superuser with the public marker header was refused after identification, with no password asked;
    - without the header it was challenged and signed in;
    - a non-superuser with the header signed in;
    - a recovery link for a superuser was refused with the header and allowed without.
34. **The browser paths `:9080` must serve.** Live (headless Chromium):
    - login uses `default-authentication-flow`'s page and executor and `/static/dist/`;
    - an authorize without a session redirects to login with `next`;
    - the console provider's end-session runs `default-invalidation-flow`, and other providers run `default-provider-invalidation-flow`;
    - logout uses `/flows/-/default/invalidation/` and then `/` → `/flows/-/default/authentication/`;
    - a login without `next` lands on `/if/user/`, which calls five `/api/v3/` endpoints;
    - recovery uses only `iverson-recovery`'s page and executor;
    - no WebSocket was opened.

    The flow page reads its config from inline JSON. The console's discovery document lists authorize, token, userinfo, revoke, end-session, jwks and device. Every shipped provider uses the implicit-consent flow.
35. **The test fixture is behind compose and Helm.** `AuthentikContainerFixture.cs:43` pins `2026.5.3`; compose and Helm run 2026.8.4 (`charts/authentik/values.yaml:1`). Every blueprint the fixture mounts applies successfully on 2026.8.4, and a manual replay of its orchestrator assertions matched. The fixture class itself was not run.
36. **API-created OAuth2 providers need `grant_types`.** Live: one created without it got `[]` and refused `authorization_code`. The shipped blueprints set it.

**Cluster privilege and reach**

37. **The VAP can express (a)–(c), and cluster-admin is not exempt.**
    - Docs: VAP is GA since 1.30, and `CONNECT` is a matchable operation.
    - Source: TokenRequest is admitted as `serviceaccounts/token` CREATE (`plugin/pkg/admission/noderestriction/admission.go`). For CONNECT, `request.name` is the pod name.
    - Live kind:
      - non-operator token requests for `iverson-api` and `iverson-admin-automation` were denied;
      - `iverson-worker`'s was not;
      - the operators group was admitted;
      - `kubernetes-admin` was denied;
      - exec, attach and port-forward into the api and Authentik pods were denied for a non-operator and admitted for the group;
      - exec into Postgres was unaffected;
      - kubelet-projected tokens still mounted.

    The `kubectl debug --copy-to` and `run --overrides` paths are inferred from (c)'s match. (d) and (e) were not run.
38. **Controller usernames on kind.** Live: FailedCreate messages named `system:serviceaccount:kube-system:replicaset-controller`, `…:statefulset-controller` and `…:job-controller`. With (c) on, rollout restarts of api, worker and Authentik produced Ready pods and no FailedCreate. EKS, GKE and AKS were not checked.
39. **The chart has no cluster-scoped objects today.** `command grep` finds no Role, RoleBinding, ClusterRole or admission objects in the chart. Terraform's VAP is the fixed name `iverson-pvc-storageclass` (`operators/main.tf:192-206`), absent on kind. AKS uses Entra RBAC with local accounts off (`cluster-azure/main.tf:211-213`). kubeconform validates VAP v1 without ignoring schemas (`deploy-validate.yml:85-88`).
40. **Strimzi `networkPolicyPeers` narrows its generated rule.**
    - Source: `KafkaCluster.java` `generateNetworkPolicy` adds each listener's peers, or all sources when none are given. `GenericKafkaListener` documents the OR semantics.
    - Live: before the change, Strimzi's 9093 rule had no `from` and an unrelated pod connected (`OPEN`). After patching the peers, Strimzi regenerated the rule with the api and worker selectors and no broker roll; the unrelated pod got `CLOSED`, and the api and worker kept producing and consuming.
    - Strimzi's rule admits its operator on 9090/9091/8443 but not 15008, and the operator reverts edits.
41. **The 8081 consumers of `/metrics` and `/build` are all listed in §3.3:**
    - `Program.cs:521, 575-579, 985, 1015`;
    - `appsettings.json:16`;
    - `api/headless-service.yaml:17-20`, `worker/headless-service.yaml:17-18`;
    - `prometheus-config.yaml:28-42`, `prometheus.local.yml:6, 9`;
    - `networkpolicies.yaml:74-84, 163, 553`;
    - `docker-compose.yml:496`;
    - `LoadTest/Program.cs:41` (used only for `/build`), `pattern_leg.py:859`, `dialogue_patterns.py:880`, `dialogue_pool.py:292`;
    - `csr11-transport-network.md:94`;
    - `BuildIdentityEndpointTests`, `AdminConsoleMetricsPipelineTests`, `AdminConsoleMetricsSeriesNamesTests`, `AuthenticationPipelineTests`, `AdminConsoleEndpointsPipelineTests`, `AdminConsoleCorsPipelineTests`, `AdminConsoleCorsTestWebApplicationFactories`.

    Probes and the admin-api Ingress do not use these routes. A plaintext endpoint beside TLS ones breaks `UseHttpsRedirection` (`api/templates/deployment.yaml:241-257`).
42. **Operator-namespace traffic, and CNPG broken on `main`.**
    - Live kind: none of `cnpg-system`, `kafka` and `starrocks` has a NetworkPolicy. On `main`, CNPG reports "Instance Status Extraction Error" with `:8000` timeouts, because `postgres-ingress` admits only 5432 from app pods.
    - The traffic observed: webhook 9443, operator API-server egress, CNPG → 8000, Strimzi → 9090/9091/8443, DNS, and 15008 for in-mesh hops. The StarRocks operator talked only to the API server.
    - GKE: the cluster is private with master `172.16.0.0/28` (`cluster-gcp/main.tf:174-178`), and `command grep google_compute_firewall` finds no rule.
43. **The AKS fallback fits the existing gate.** `networkpolicies.yaml:14-17` fails a fallback without `keepFallbacks`, and `values.yaml:27-37` documents it. The runbook's hand-edit is `csr10-infrastructure-cutover.md:139-143`.
44. **AKS KMS and IMDS.**
    - azurerm 3.117.1 (the locked version, `azure/.terraform.lock.hcl`) has `key_management_service { key_vault_key_id, key_vault_network_access }` (`kubernetes_cluster_resource.go:903-922`).
    - The cluster identity is already user-assigned (`cluster-azure/main.tf:128, 234-237`), with API Server VNet Integration on (`:259-262`).
    - Microsoft's KMS article (`use-kms-etcd-encryption.md:55`): "Azure Key Vault can't be configured with the firewall option *Allow public access from specific virtual networks and IP addresses*". The existing vault is exactly that (`cluster-azure/main.tf:34-38`, `default_action = "Deny"` with `ip_rules`).
    - IMDS restriction is "(preview)" (`learn.microsoft.com/azure/aks/imds-restriction`), and azurerm has no argument for it (no `imds` in v5.10.0's resource).
45. **Readiness works without `OPERATE ON SYSTEM`; today it needs it.**
    - `EngagementHealthChecker.cs:72` runs `SHOW BACKENDS` as `iverson_app` for `/health` and the readiness gate (`EngagementRepository.cs:27-29, 103, 853-860`).
    - Missing OPERATE reads as `Unhealthy`, not `AuthPending`. `ClassifyConnectionException` maps only `AccessDenied` (1045) to `AuthPending` (`EngagementHealthChecker.cs:42-44`), and `SHOW BACKENDS` without OPERATE fails with `ERROR 5203` (live, `kind-proto/ev/b10.txt`), which `ReadinessPolicy.cs:27-28` does not tolerate. A pre-upgrade pod still running `SHOW BACKENDS` therefore fails readiness once the revoke has run.
    - Live on StarRocks 4.1.6 as a login with no grants: `SELECT * FROM TABLE(generate_series(1,2))` returned two rows with a backend up (0.5 s) and `ERROR 1064 Backend node not found` with none (0.4 s). `SELECT 1` succeeds either way, and `SHOW BACKENDS`, `SHOW PROC` and `be_metrics` need OPERATE.
    - Tests pinning the old check: `StarRocksHealthCheckerTests`, `StarRocksContainerReadinessTests`, `StarRocksSplitLogins`, `StarRocksCredentialSplitIntegrationTests`, `StarRocksReadinessIntegrationTests`, `RegisterSchemaAuthorizationIntegrationTests:146-175`.
46. **Kubernetes versions.**
    - The Terraform default is 1.30 everywhere (`cluster-aws/variables.tf:13`, `cluster-azure/variables.tf:13`, `cluster-gcp/variables.tf:15`). The EKS add-ons and cluster-autoscaler are pinned against it (`cluster-aws/main.tf:478-504`, `operators/versions/Chart.yaml:22-23`). CI validates at 1.30.0.
    - `kind-config.yaml` has no image, and kind v0.24.0 defaults to v1.31.0.
    - Istio's supported-releases table gives 1.31 as 1.32–1.37. `istioctl x precheck` passed on 1.35.8 under kind v0.33.0.
    - GKE's release notes refer to "GKE version 1.35 and later".

**Transport**

47. **Ambient works on kind + Calico, and egress needs 15008.**
    - Live: istio-cni chained into Calico's conflist, and every enrolled workload showed HBONE.
    - With 15008 on ingress rules only, api, worker and Authentik went NotReady, and ztunnel logged "maybe a NetworkPolicy is blocking HBONE port 15008" for every hop. With it on egress too, all became Ready.
    - End to end under STRICT: `/health` all true; register, post, get, search and SearchSimilar with tenant isolation.
    - ztunnel reported `mutual_tls` for api and worker → Postgres, FE, Kafka, Qdrant, TEI and Jaeger, for FE ↔ BE, and for Prometheus → 8081.
48. **No vendor statement covers ambient on AKS, GKE Dataplane V2 or Calico.** Istio's platform prerequisites cover GKE (`global.platform=gke`), EKS (security-groups-for-pods strict mode drops link-local probes) and Cilium ("BPF masquerading … breaks health checks"); AKS, Azure NPM, Dataplane V2, Calico and kind are not named. Istio 1.31.2 was released 2026-10-16.
49. **The operators keep working under ambient once their rules exist.** Live:
    - Strimzi's AdminClient timed out on 9090/9091 until the chart admitted it on those ports and 15008, then reconciled.
    - CNPG reached the instance over HBONE and reported healthy.
    - The StarRocks operator scaled FE/BE through the CR.
    - The CNPG webhook answered even under STRICT. That is because istio-cni SNATs host-originated sockets to 169.254.7.127, and kind's API server is host-network; off-node API servers on clouds are not covered by this.
50. **Port-level mTLS needs a workload selector.**
    - Docs (`istio.io/latest/docs/ambient/usage/l4-policy/`): PeerAuthentication is supported by ztunnel, and DISABLE is ignored.
    - Source (`pilot/pkg/serviceregistry/ambient/authorization.go`, `convertPeerAuthentication`): policies without a selector or port settings are skipped for port-level handling.
    - Live: from a non-mesh namespace with NetworkPolicy admitting it, FE 9030 and Postgres 5432 were refused under STRICT, and a port marked PERMISSIVE answered 200.
51. **Probes need no link-local allow on Calico iptables.** Live: under STRICT with no 169.254.7.127 rule, rolled and deleted pods came back Ready, with zero ztunnel rejections. The SNAT happens in `nat POSTROUTING` after Calico's filter. The allow changed nothing. Docs (`ambient/usage/networkpolicy/`): "If your workload, namespace, or cluster enforces NetworkPolicy, allow both addresses." Calico's eBPF dataplane was not tested.
52. **An Istio gateway replaces ingress-nginx on kind.**
    - Live: a `Gateway` (class `istio`) in an unenrolled namespace, with host ports through `infrastructure.parametersRef`, served gRPC (Post, Get, Search) and HTTPS `/health` (HTTP/2, the security headers via `ResponseHeaderModifier`), and Authentik's login page, through kind's existing mappings.
    - `BackendTLSPolicy` for the api and Authentik was Accepted/ResolvedRefs, and Istio re-encrypted to the pods.
    - The certificate needed a `ReferenceGrant`.
    - The gateway → pod hop was `mutual_tls` from the gateway's SPIFFE identity.
    - ingress-nginx's repository was archived 2026-03-24 with no further releases.
53. **StarRocks can force TLS.**
    - Live: the FE, BE and CN entrypoints call plain `mysql … -u root`, and the image's MySQL 8.0.46 client negotiates TLS by default.
    - `ADMIN SET FRONTEND CONFIG ssl_force_secure_transport` is refused (not mutable).
    - With it in `fe.conf` and an FE restart: the BE's call works, `--ssl-mode=DISABLED` gets error 5205, a BE restart re-registers, and the app passes end to end.
    - Also live: without `MYSQL_PWD`, a restarted BE gets "Access denied (using password: NO)" once root has a password.
    - The comment to correct is `configmap-fe.yaml:83-85`.
54. **The revoke Job can verify.** It runs `postgres:18.6` with no `PGSSLMODE` and only a `tmp` volume (`job-revoke-cross-db.yaml:40-55`). `<rel>-postgres-ca` holds `ca.crt` (and `ca.key`), mounted elsewhere with `items: [ca.crt]` (`deployment-server.yaml:213-218`). Authentik already connects `verify-full` to the same host (`authentik/templates/configmap.yaml:11-12`).
55. **HSTS today.** The console's nginx sends it when https (`docker-entrypoint.sh:99-104`), and so do the nginx Ingress snippets (`admin-ui/templates/ingress.yaml:48-51`, `api/templates/ingress.yaml:37-39`). Kestrel has no `UseHsts` (`command grep`), and ASP.NET's middleware writes it only when `Request.IsHttps`. No tls-proxy location sets its own `add_header`, so a server-level one is inherited (`configmap-tls-proxy.yaml:65-86`).
56. **GKE Gateway capabilities.**
    - `gke-l7-global-external-managed`: "HTTPRoute is the only Route type supported"; gRPC to backends by Service `appProtocol` `HTTP2`. Source: `docs.cloud.google.com/kubernetes-engine/docs/how-to/deploying-gateways`.
    - `BackendTLSPolicy`: CA from same-namespace ConfigMaps under `ca.crt`, plus `validation.hostname`. `GCPBackendPolicy` takes `securityPolicy` and `timeoutSec`. `HealthCheckPolicy` takes HTTP, HTTPS, HTTP2 or GRPC. Static addresses use `NamedAddress`, and omitting the port-80 listener turns HTTP off. Sources: `how-to/secure-gateway`, `configure-gateway-resources`, `gatewayclass-capabilities`.
    - Release notes (`cloud.google.com/kubernetes-engine/docs/release-notes`), May 29, 2026: "GKE Gateway now supports backend authenticated TLS for Gateway-originated connections to Pods … gke-l7-global-external-managed".
57. **Gateway API is not enabled on GKE but can be.** `cluster-gcp/main.tf:134-190` has no `gateway_api_config`. The google provider is locked at 5.45.2, which supports it.
58. **The repo's edge scope.**
    - Four Ingress templates (`api/ingress.yaml`, `api/admin-api-ingress.yaml`, `authentik/ingress.yaml`, `admin-ui/ingress.yaml`) and `iverson.edgeAnnotations` (`_helpers.tpl:111`).
    - The ingress-nginx peers (`networkpolicies.yaml:67, 77, 503, 591`) and the `_validate.tpl:70-112` guards.
    - Install and host ports: `setup.sh:84-132`, `setup.ps1:69-75`, `kind-config.yaml`.
    - GCE pieces: `backendconfig.yaml`, the `app-protocols` annotations (`api/templates/service.yaml:7-9`, `authentik/templates/service.yaml:8-16`) and `waf.tf`'s hand-copied output. No ManagedCertificate, FrontendConfig or static address exists.
59. **Kafka authorisation does not depend on addresses.** The KafkaUser uses SCRAM-SHA-512 with prefix ACLs and no `host` (`charts/kafka/templates/kafkauser.yaml`). The single `tls` listener has no `networkPolicyPeers` today (`kafka.yaml:13-21`).
60. **Compose today.** The compose CA signs only Authentik's certificate, and its key is deleted after signing (`generate-compose-secrets.sh:42-63`). Ports are loopback-bound (`docker-compose.yml:47, 65-66, 495-496`). Round 11 accepted plaintext for compose's API, StarRocks and Authentik → Postgres hops (`2026-10-12-csr-round11-transport-network-design.md:124`). Compose clients default to `http://localhost:8080` (`ClientConformance/Program.cs:17`, `LoadTest/Program.cs:36`, `pattern_leg.py:658`), and no compose client configures a custom CA today (`command grep`).
61. **AWS ALB does not verify targets.** `docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-target-groups.html`: "The load balancer does not validate these certificates… traffic between the load balancer and the targets is authenticated at the packet level".

**Added in review round 1** (2026-10-19, against `203ae962`; records under `udd-probes/` in the same session scratchpad)

62. **`policy_engine_mode: all` plus an always-false flow-level binding denies `initial-setup` on 2026.8.4.** Live, throwaway stack with no bootstrap password, flow in its default `any` mode: `/` → 302 `/setup` → the password prompt, because `SetupView` plants the context the stock policy requires and stays open while the Setup flag is False; the flag is set at startup only by `AUTHENTIK_BOOTSTRAP_PASSWORD`, `_PASSWORD_HASH` or `_TOKEN` (`core/setup/signals.py`). A cold start of the executor is refused by the stock policy itself. The worker's file watcher applied a custom-directory blueprint setting `all` and binding a `return False` policy at flow level; `/setup` then answered 500 (`FlowNonApplicableException`, uncaught in `SetupView.get`) and the executor access-denied. The mode and binding survived a forced API re-apply of `default/flow-oobe.yaml`, `ak apply_blueprint` and a worker restart. Control: the binding alone, in `any` mode, left the prompt reachable. Record: `p1/` (U4).
63. **The two ignores are what the dedicated KMS vault needs to pass tfsec.** Live, tfsec v1.28.6 (the CI version) on a scratch vault with public network access and a key without expiry: CRITICAL `azure-keyvault-specify-network-acl` plus MEDIUM `azure-keyvault-ensure-key-expiry`, exit 1; with the two ignore comments, 2 ignored, "No problems detected!", exit 0. The unmodified repo tree also reports "No problems detected!". Record: `tfsec-kms/`.
64. **The StarRocks entrypoints tolerate `MYSQL_PWD` set before root has a password.** Live, the chart's 4.1.6 FE and BE images under podman, with the pod environment copied from operator v1.11.5's source. FE-0 on a fresh install never logs in and starts as leader after 30 s. A BE refused 62 times logged `Time out, abort!` and exited 1 at 61 s, unregistered; after `SET PASSWORD` a restart registered it, `Alive: true`. A follower FE logged "No leader yet" 60 times and exited 1 at 120 s without `ADD FOLLOWER`; a restart after the password joined it as `FOLLOWER` in the same ClusterId. With the password already set, every case joined at first start. The scripts never inspect mysql's exit status (`fe_entrypoint.sh:102-103, 154, 235`; `be_entrypoint.sh:88-89`): an index-0 FE with no metadata, facing an FE whose root password differed, became leader of a second cluster (ClusterIds 1326736343 and 769566813). Kubelet restart backoff was not emulated. Record: `p2/` (U5).
65. **The login deny stage runs once per plan; password retries inside a plan are bounded by the password stage alone.** Live, throwaway 2026.8.4 stack with §2.5's policy bound to a deny stage at order 12 of `default-authentication-flow` (`evaluate_on_plan: false`, `re_evaluate_policies: true`): the server's evaluation log showed the policy evaluated once per plan, when identification completed, and never on a retried password. A plan starting with the pair's row at 0 was cancelled at the 5th wrong password by the password stage's `failed_attempts_before_cancel` (5). A pair already at −5 was refused at order 12 before the password prompt. Plans starting with the row at −3 and at −4 accepted 12 wrong passwords without cancelling, and a correct password plus TOTP then completed the login. Record: `p1/` (U7).
66. **ztunnel L4 `AuthorizationPolicy` restores port scoping.** Live, kind with Istio 1.31.2 ambient and STRICT: an ALLOW on Jaeger for the api's and worker's principals on 4317/4318 admitted them there, refused them on 16686 (`allow policies exist, but none allowed`) and refused other service accounts on every port; before the policy every client reached all three ports. Kubelet probes on a port the policy does not list kept the pod Ready. On a PERMISSIVE port, a principals-only ALLOW refused plaintext from an unenrolled pod; adding `from.source.ipBlocks` with its address admitted it; `notPrincipals` admitted it and every other caller; `requestPrincipals: ["*"]` matched nothing. The kind gateway in the unenrolled `iverson-edge` is a mesh client with principal `cluster.local/ns/iverson-edge/sa/edge-istio` (`<gateway>-istio`); a principal ALLOW admitted it and its absence refused it. Cross-namespace principals matched: CNPG operator → Postgres 8000 and Strimzi operator → brokers 9090/9091 (removing either rule broke that operator's reconcile; restoring it fixed it). An ALLOW for `ns/iverson/sa/default` admitted two unrelated pods alike; operator 1.11.5's CRD has `starRocksFeSpec.serviceAccount` and `starRocksBeSpec.serviceAccount`. A policy change did not always cut connections already pooled. Record: `p3/` (Phase 4).
67. **Compose's Authentik issuer through the tls-proxy.** Live, throwaway 2026.8.4 behind a byte-for-byte copy of `deploy/compose-tls-proxy.conf`: for `issuer_mode: global` providers, the discovery `issuer` and a token's `iss` are `<scheme>://<Host header as received>/`, so `localhost` and `127.0.0.1`, and each port, give different issuers; a forced Host `authentik-server:9000` through the proxy gives `https://authentik-server:9000/`, which the api accepts under none of its issuers (`Program.cs:161-202`). `X-Forwarded-Proto` is honoured only from an address in `TRUSTED_PROXY_CIDRS`: with `127.0.0.2/32` and `proxy_bind 127.0.0.2` the issuer is `https://localhost:<P>/`; with either alone it is `http://…`. Today's leaf names only `DNS:authentik-server` (`generate-compose-secrets.sh:60`), so `-verify_hostname localhost` fails. A CA made by the script's command has no `keyUsage`; Python 3.14.4's default context refuses it ("CA cert does not include key usage extension"), and a CA with `keyUsage=critical,keyCertSign,cRLSign` passes. Port 9443 is free in compose and in kind (`kind-config.yaml:21-23` binds 8443). Record: `p1/` (option A).
68. **`main`'s ingress-nginx runs on the pinned node image.** Live, kind v0.33.0 with `kindest/node:v1.35.8`: `main`'s unchanged `setup.sh` exited 0; the ingress-nginx controller v1.12.8 (chart 4.12.8) became Ready and its webhook admitted all four Ingresses with no API-deprecation errors; through `127.0.0.1:8443` the gRPC route answered 401 with a Bearer challenge, and over plaintext `127.0.0.1:8080` admin-api `/health`, Authentik and admin-ui each answered 200 (`ev/p1-requests.txt:8, 25, 29, 33`). Record: `p3/` (Phase 1).
69. **Admission rules (d) and (e) hold live.** Kind, one VAP with rules (a)–(e) as §3.1 states them: a non-operator with namespace `admin` was denied creating a Job, CronJob, Deployment, StatefulSet, DaemonSet and ReplicaSet with either protected account (including the deprecated `serviceAccount` alias, which the API server copies before admission) and denied every update of the api Deployment's main resource (a `rollout restart`, a template patch, a metadata-only label), while `kubectl scale` through the `scale` subresource was admitted (`ev/p2-matrix.txt:37`); an operator was admitted for all. The controllers created pods for operator-made Deployments, ReplicaSets, StatefulSets, Jobs and CronJobs; a DaemonSet's pods were refused (`… created by system:serviceaccount:kube-system:daemon-set-controller denied`), no other controller identity was missing, and admitting `daemon-set-controller` was not run. Garbage collection completed. `kubectl debug` (ephemeral container) on api, worker and Authentik pods was denied for a non-operator and for cluster-admin, and admitted for an operator. A `helm upgrade` that leaves the api's pod template unchanged sends no patch and passes without the group; one that changes it is denied without the group and admitted with `--kube-as-user` plus the group (`--kube-as-group` alone is refused by Helm). The built-in `admin` and `edit` roles do not grant `pods/ephemeralcontainers`. One policy matching pods, workloads, tokens and CONNECT draws a type-check warning on rule (c) for workload kinds, harmless at runtime. Record: `p3/` (Phase 2).
70. **The `istio-system` and `iverson-edge` allow lists are complete.** Live, kind after §5's order: with default-deny and exactly §3.4's allows, istiod, ztunnel, istio-cni and the gateway all restarted Ready; ztunnel and the gateway re-subscribed to istiod; new pods got HBONE and freshly issued certificates; istiod's validating webhook answered; every route served. istiod needs no egress beyond the API server and DNS; the gateway needs no ingress beyond 80/443. On kind the 15017-from-API-server and gateway 80/443 rules are not exercised (host traffic is always admitted); egress is enforced. A gateway rollout deadlocked on one node with the default surge until `maxSurge: 0`. Record: `p3/` (Phase 3).
71. **`REVOKE OPERATE ON SYSTEM` is idempotent for an existing user.** Live, StarRocks 4.1.6 FE with mysql clients 8.0.46 and 26.7.0: the revoke exits 0 for a user without OPERATE, with or without the `USER` keyword; grant → revoke → revoke all exit 0. It errors only for a user that does not exist (`ERROR 1064 … cannot find user`), which in a `set -e` batch would stop it; the batch creates the user first. Record: `p2/` (U6).

**Added in review round 2** (2026-10-19/20, against `dcd334e9`; records under `udd2-probes/` in the same session scratchpad unless named otherwise)

72. **Authentik's migration lock and version marker do not show that migration finished.** Source, 2026.8.4 (`lifecycle/migrate.py`): lock 1000 (`ADV_LOCK_UID`, `:18`) is taken only in `wait_for_lock` (`:58-63`), called once at the start of the migrator's pass (`:134`) and released in `finally` (`:175`); no other code takes lock 1000. The system-migration loop (`:135-153`) runs `version_history_update`, which INSERTs the new version into `authentik_version_history` and commits (`system_migrations/version_history_update.py:21-36`), before `migrate_schemas` (`:164`).
73. **Entry-level object permissions need the role to exist when the entry is applied.** Live, 2026.8.4, a fresh database per variant, the repo's blueprint tree mounted as compose and the fixture mount it. With `permissions:` (`add_user_to_group` and `remove_user_from_group` for `role: !Find [authentik_rbac.role, [name, iverson-admin-orchestrator]]`) on the compose `tenant-admins` entry where it sits today, before the role entry: the instance reported `successful`, the role held no object permission, and the orchestrator token's `groups/<pk>/add_user/` and `/remove_user/` answered 403; a later re-apply assigned them (204/204). With the entry moved after the role entry: both were assigned on the first apply, add/remove answered 204/204, and membership changed. Every variant answered 403 on a tenant group and on `operators`. Cause: `Importer._apply_permissions` (`blueprints/v1/importer.py:366-375`) skips a permission whose role resolved to `None`. On kind, the Helm blueprint with the entry moved after the role (`templates/secret-service-clients.yaml`, today `:489` before `:540`) assigned both on a fresh database with no re-apply, and `add_user`/`remove_user` answered 204 (403 on `operators`). Record: `p4/` (U3), `p5/` (Phase 4b).
74. **Compose Postgres serves TLS from a copied key and still accepts plaintext.** Live, the compose digest of `postgres:18.6` under rootless podman. A directly mounted key is refused at 0644 ("has group or world access") and unreadable at 0600 or 0640 ("Permission denied"); the container exits 1. A wrapper that copies the key into a `postgres`-owned 0600 file before `docker-entrypoint.sh` started with `ssl=on` and restarted cleanly. From the host, `sslmode=disable` was accepted (`ssl=false`, password enforced) because the image's `pg_hba.conf` ends with `host all all all scram-sha-256`; `verify-full` to `localhost` gave TLSv1.3; `verify-full` to `127.0.0.1` was refused, because the leaf names only `postgres` and `localhost`. Compose's `pg_isready -U iverson` healthcheck still exits 0. Records: the review round's `cdr2-pg` probe and `p4/` (U4).
75. **Each host runtime's custom-CA path, and the plaintext pins.** Live against a throwaway CA (with `keyUsage`) and `localhost` leaf, each runtime's real client library: .NET 10 `HttpClient` and Grpc.Net.Client, and Go `net/http` and grpc-go, trust it through `SSL_CERT_FILE`; Python urllib through `SSL_CERT_FILE` and grpcio only through `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH` (each ignores the other's); Node `fetch` and grpc-js through `NODE_EXTRA_CA_CERTS`; Java's JDK client and grpc-java through a PKCS12 truststore in `-Djavax.net.ssl.trustStore` (a PEM file is not accepted). Plaintext channel credentials against TLS fail in every runtime; .NET's `ChannelCredentials.Insecure` with an `https://` address throws. The ClientConformance harness (`Program.cs:111-114`) and the five drivers (`Auth.cs:52, 65`; `main.go:507, 889`; `Driver.java:177`; `driver.py:376-381`; `driver.ts:390, 399`) hard-code plaintext credentials. LoadTest's StarRocks connection string (`Program.cs:39`) and `stack.py:172` dial `127.0.0.1`; `ingest.py:159`'s Qdrant URL is a constant. The Agents' Anthropic client builds its context from `$SSL_CERT_FILE` alone (`httpx2/_config.py:34-35`, source). Record: `p4/` (U6).
76. **Istio 1.31.2's ztunnel and istio-cni tolerate every `NoSchedule` taint.** The release tarball (SHA-256 `b3d58081…3575`, matching the published checksum): `ztunnel/values.yaml:41-47` and `istio-cni/values.yaml:127-135` default `tolerations` to `{effect: NoSchedule, operator: Exists}`, `{key: CriticalAddonsOnly, operator: Exists}` and `{effect: NoExecute, operator: Exists}`; `helm template` with `profile=ambient` (and `global.platform=gke`) renders all three on both DaemonSets. The cloud datastore pools carry only `NoSchedule` taints (`cluster-aws/main.tf:583-590`, `cluster-gcp/main.tf:275-279`, `cluster-azure/main.tf:301-303`). Record: `p4/` (U7).
77. **Rule (f) refuses in-place image swaps and admits controller and mesh updates.** Live, kind with the mesh and the ALLOW mirror: a non-operator's `set image` was refused on an api pod both as a server-side dry-run and for real (the image did not change), and a non-operator's JSON-patch of a container image and cluster-admin without the group were refused as server-side dry-runs (`ev/p1-matrix.txt:3-7`); an init-container image change was refused on a throwaway pod named `iverson-api-…` (the api pod has no init container today). Admitted: the operators group's changes, a non-operator's label and annotation edits, re-setting the same image, and image changes on pods outside (b)'s names. Under (f), istio-cni's redirection annotation on new api pods and its removal on Job completion, an operator `rollout restart` (Ready in 23 s), a deleted api pod's replacement (Ready in 26 s), and a protected-SA Job's tracking-finalizer removal, completion and TTL/GC deletion all passed; the API server's admission counter showed exactly the six deliberate UPDATE denials, and (f) drew no type-check warning. Record: `p5/` (Phase 1).
78. **A two-instance Postgres needs the instance peer rule in both layers.** Live, kind with the mesh and STRICT, `instances` patched 1 → 2, the CNPG operator admitted on 8000 and 5432. With a port-less NetworkPolicy rule from `cnpg.io/cluster` pods and a port-less ALLOW for `…/sa/iverson-postgres`: the join job completed and the cluster was healthy 2/2 in 32 s; `pg_stat_replication` showed the standby streaming; join and standby connected as the instances' principal, and the primary also connected to the standby's 8000. Deleting the primary promoted the standby in about 90 s; the old primary rejoined streaming, and the api's `/health` showed postgres true. With the rule removed from both layers, the standby could not stream while CNPG reported "Cluster in healthy state, ready=2" and every pod was Ready (`ev/p2-control-a.txt`), and a clone of a deleted standby looped at "Creating a new replica, ready=1" (`ev/p2-control-a-join.txt`); with only the NetworkPolicy rule restored, the clone still failed on a ztunnel 401 (the ALLOW layer alone; tested on the clone, not on streaming). With the operator's 5432 removed, failover and rejoin still worked; over the whole run the operator connected only to 8000. Record: `p5/` (Phase 2).
79. **The ALLOW mirror admits every peer on the remaining hops.** Live, kind, one ALLOW policy per workload built from `main`'s NetworkPolicy ingress rules: Prometheus → api and worker metrics (8081, standing in for 8082), api → Prometheus 9090, api and worker → Qdrant 6334 and TEI, api, worker and the create-user Job → StarRocks FE 9030, FE ↔ BE, api → Authentik 8443, and the gateway → Authentik 9080, admin-ui and api were each admitted; a non-peer pod was refused on every destination, and api → FE 8030 (a port its rule does not list) was refused. The StarRocks operator honoured `starRocksFeSpec.serviceAccount` and `starRocksBeSpec.serviceAccount`. An `AuthorizationPolicy` selector takes `matchLabels` only, so FE and BE need one policy each. Ollama and Qdrant raft were not exercised (memory; the chart forms no Qdrant cluster). Record: `p5/` (Phase 3).
80. **Both pins refuse a legacy service-account-token Secret's token.** Live, kind: a non-operator with namespace `admin` created `kubernetes.io/service-account-token` Secrets for `iverson-api` and `iverson-admin-automation` (no admission rule matched) and read the tokens; the Kubernetes API accepted the `iverson-api` token as that account (`kubectl auth whoami`, `ev/p4-extra.txt:5-11`; the admin-automation token was not checked there). The tokens carry `iss: kubernetes/serviceaccount` and no `aud`, `exp`, `iat` or pod binding. Exchanged where the real tokens are, both answered 400 `invalid_grant`: Authentik's federation check verified the JWT, and the chart's pin policy refused it. Its audience clause and its lifetime clause each refuse alone, as does the orchestrator's pod clause; neither Authentik nor the pin checks the issuer. TokenRequest tokens with the right audience got 200 on both pins. Record: `p5/` (Phase 4).
81. **`main`'s chart runs on Authentik 2026.5.3 and upgrades to 2026.8.4.** Live, kind: `main`'s chart with server and worker on `ghcr.io/goauthentik/server:2026.5.3` applied all 37 blueprints and served a token exchange (200). On upgrade to 2026.8.4 the migration took 152 s while the 2026.5.3 server kept serving (56/56 polls 200); on `main`'s chart the new worker started at once on the old schema and logged 71 database errors until the migration finished. Record: `p5/` (Phase 5).

**Added in review round 3** (2026-10-20, against `51fd76ee`; records under `udd3-probes/` in the same session scratchpad unless named otherwise)

82. **EKS access entries need an API authentication mode, and the provider's bootstrap default.** AWS API reference (`CreateAccessConfigRequest.authenticationMode`): a cluster created through the EKS API or an SDK defaults to `CONFIG_MAP`; the EKS User Guide: access entries require `API_AND_CONFIG_MAP` or `API`. `terraform-provider-aws` v5.100.0 (`aws/.terraform.lock.hcl`): `access_config.authentication_mode` takes `CONFIG_MAP`, `API` or `API_AND_CONFIG_MAP` (`website/docs/r/eks_cluster.html.markdown:347, 362-367`); `bootstrap_cluster_creator_admin_permissions` is an optional bool documented "Default is `false`" and sent from `tfMap[…].(bool)` (`internal/service/eks/cluster.go:107-111, 1121-1122`). `aws/main.tf:44-56` authenticates the `kubernetes` and `helm` providers with the caller's `aws_eks_cluster_auth` token. The repo has no `access_config`, `authentication_mode`, `aws_eks_access*` or `aws-auth` (grep). Not applied against AWS (no account); confirmed at first deploy (§7). Records: the review round's `cdr-platform-3/s2/aws-accesscfg.txt` and the provider sources at tag v5.100.0.
83. **The compose worker healthcheck needs TLS once Kestrel serves HTTPS.** Live: a net10.0 Kestrel app on `https://127.0.0.1:18081` (PEM certificate, `Http1`). Compose's current check against it exits 1 (no plaintext status line); against the same app over `http://` it exits 0. The `openssl s_client` form in §4.6, run in the pinned runtime image (`mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778…`, `Iverson.Api/Dockerfile:37`) with host networking, exits 0 for `/health` and 1 for a 404 path and for a closed port. The image has `openssl` and `bash` and no `curl`, `wget` or `nc`. The worker already mounts the compose CA at `/etc/iverson/authentik-ca/ca.crt` (`docker-compose.yml:680`). Records: the review round's `cdr-platform-3/probe-hc/` and `hc/`.
84. **CNPG 1.30 needs no admission under the Postgres-pod gate.** Live, kind, rules (b), (e) and (f) extended to `iverson-postgres-*` with the CNPG operator's account not admitted, two instances with the instance peer rule: scale-up 1 → 2 (51 s), failover by deleting the primary with the old primary rejoining (20 s), re-clone of a standby after deleting its PVC and pod (31 s), a rolling restart, a rolling image change, and a manual switchover all completed. The operator made no exec, attach or port-forward request and changed no image in place; it deleted and recreated pods (new pod IDs each time). A positive control impersonating its account was refused exec by (b) and an image patch by (f), so the counters would have shown it. A non-operator was refused exec, attach, port-forward, debug and image change on `iverson-postgres-1`; the operators group's `psql` ran. Not run: the operator upgrade, backup and restore, fencing, hibernation. Record: `p6/` (U2).
85. **Rule (b) holds on the worker and both Authentik pods.** Live, kind: a non-operator with namespace `admin` was refused exec, attach and port-forward into an `iverson-worker-*`, an `iverson-authentik-server-*` and an `iverson-authentik-worker-*` pod (9/9, `(b) <verb> into <pod> by alice denied`), and cluster-admin without the group was refused too; the operators group was admitted for all nine (exec returned output, attach streamed, each port-forward served a request). `ak help` by exec into the Authentik worker lists `apply_blueprint`. A refused exec, attach or port-forward from current kubectl counts twice on the API server's rejection counter (WebSocket, then SPDY fallback). Record: `p6/` (U3).
86. **Jaeger 1.62.0 enforces `--memory.max-traces`.** Live, the chart's `jaegertracing/all-in-one:1.62.0` with the chart's security settings: with `--memory.max-traces=50000` it logged `Memory storage initialized … {"MaxTraces":50000}` and stayed up with its admin, UI and intake ports answering; without the flag it logs `{"MaxTraces":0}` (unbounded). With a cap of 3 and 6 traces sent, the newest 3 were kept and the oldest returned 404. A misspelled flag and a non-numeric value each exit 1 at start. The chart's Deployment passes no `args:` today, and pins the image by tag only. Record: `p6/` (U4).

**Added in review round 4** (2026-10-20, against `e317f447`; records under `cdr-platform-4/` in the same session scratchpad)

87. **Istio 1.31.2's charts take smaller requests and set no limits.** `helm template` of the vendored charts with `profile=ambient`: the defaults render istiod 500m / 2048Mi, ztunnel 200m / 512Mi and istio-cni 100m / 100Mi as requests, and none of the three renders a `limits` block. With `--set resources.requests.cpu=100m,resources.requests.memory=256Mi` on istiod and `…cpu=50m,…memory=64Mi` on ztunnel, the Deployment and DaemonSet render exactly those requests. The gateway's pod template takes its resources from `global.proxy.resources` (`istio-discovery/files/kube-gateway.yaml:93-95`). `values-laptop.yaml:1-11` budgets the laptop release at about 3.8 of 4.0 CPU on "a 4-CPU / 9GB host"; the ingress-nginx 4.12.8 controller the gateway replaces requests 100m / 90Mi (rendered with `setup.sh`'s flags). That the four pods and the laptop release fit 4 CPU / 9 GB at reduced requests is measured in the plan's kind run, not here. The default istiod render also includes a `HorizontalPodAutoscaler` (`minReplicas: 1`, `maxReplicas: 5`, CPU `averageUtilization: 80`; `istio-discovery/values.yaml:4-6`, `autoscaleEnabled: true`); with `autoscaleEnabled=false` it renders none, the Deployment has `replicas: 1`, and there is no `PodDisruptionBudget` (re-rendered in review round 5). In the round-1 kind run istiod was at 2 replicas at the default request (`udd-probes/p3/results.md:159`); kind runs metrics-server (`setup.sh:175-189`). Records: `s3-istio/`, `asm-nginx/render.yaml`.
88. **EKS access entries need an access-policy association to grant permissions.** EKS User Guide, "Create access entries": an entry for a Kubernetes group without matching RBAC is accepted "but the IAM principal won't have any permissions until matching Kubernetes RBAC resources are created", and "an IAM principal can't be included in more than one access entry" (`s2/creating-access-entries.html`; `s2/createaccessentry.html`). The access-policy page: `AmazonEKSClusterAdminPolicy` grants "administrator access to a cluster", its scope "typically the cluster" (`s2/access-policies.html`); the namespace-scoped `AmazonEKSAdminPolicy` lists no `admissionregistration` or cluster-role resource (`asm-nginx/eks-adminpolicy.txt`). `terraform-provider-aws` v5.100.0 has `aws_eks_access_policy_association` with `policy_arn`, `principal_arn` and `access_scope.type` = `namespace` or `cluster` (`website/docs/r/eks_access_policy_association.html.markdown:33-42`). The repo has no RBAC binding or association (grep over `deploy/terraform`). Not applied against AWS (§7).
89. **`kubectl cnpg psql` does not forward impersonation flags.** Live: the CNPG 1.30.0 plugin binary (release checksum matched) against a header-logging fake API server. With `--as=kubernetes-admin --as-group=kubeadm:cluster-admins --as-group=iverson-operators`, the plugin's own requests carried `Impersonate-User` and both groups, while the spawned `kubectl/v1.36.2` pod GET and exec GET/POST carried none. With `--context` naming a kubeconfig context whose user sets `as` and `as-groups`, the spawned requests carried them; a plain `kubectl exec` with the three flags carried them too. Source v1.30.0: `getKubectlInvocation` builds `kubectl exec` with optional `--context`, `-t`, `-i`, `-n`, `-c`, the pod and `-- psql`. Records: `s2/cnpg-psql-impersonation-req.log`, `s2/cnpg-psql.go`.

**Added in design verification round 1** (2026-10-20, against `19d74cfb`; records under `udd6-probe/` in the same session scratchpad)

90. **An unknown username's 3rd login plan is refused after 2 failed attempts.** Source, 2026.8.4: `stages/identification/stage.py:198` sends `identification_failed` while validating identification, before the pretend user continues (`:210-212`), and `policies/reputation/signals.py:58-61` scores it −1. Live, throwaway 2026.8.4 stack with §2.5's policy on the deny stage at order 12 of `default-authentication-flow` (identification: `pretend_user_exists` true, no `password_stage`), one new plan per attempt with one wrong password each. For an identifier with no user, the row read −1 after identification and −2 after the password in plan 1, −3 and −4 in plan 2, and −5 after identification in plan 3, which was refused at order 12 ("Too many failed attempts. Try again later.") before the password prompt. Control, a known username: identification scored nothing, the row reached −5 after the 5th wrong password, and plan 6 was refused at order 12. The throttle policy ran once per plan (9 evaluations, 9 plans). Record: `udd6-probe/ev/count.txt` (md5 `4039c5e2018035bc85ca732d0196c8d7`).

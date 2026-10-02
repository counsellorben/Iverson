# LoadTest Tenant-Admin Password — Design

**Date:** 2026-10-02
**Status:** Approved design, assumptions verified

## Problem

On a fresh stack, LoadTest creates its tenant and then logs its data plane in as `iverson-loadtest-tenant-admin`, using the password in `IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD` (default `dev-only-not-for-production-tenant-admin-password-0123456789`, `Program.cs:66`). That user has no password.

Since CSR round-2 finding #4, CreateTenant creates the admin through passwordless onboarding (`94a05ae`): `IdpAdminClient.CreateUserAsync` creates the Authentik user without a password, mints a one-time recovery link and returns it as `Tenant.admin_recovery_link`. `EnsureTenantProvisionedAsync` (`Program.cs:351-375`) discards the response. Every data-plane login then fails with `Authentication flow did not complete after 20 stages`. On 2026-09-28 that cost 1,121 failed searches over about 7 hours (`iverson-benchmark-corpora/matchpattern-rrf-2026-09-28/phase0.md`, "Fix round 2").

## Decisions (user, this session)

- **Scope A — fresh stacks only.** Only a tenant that LoadTest creates itself gets the configured password. Two things are out: repairing a tenant that already exists without a password, and a fail-fast startup login (options B and C).
- **Approach A1.** LoadTest follows the recovery link CreateTenant returns.
  - **Rejected, set the password through Authentik's admin API.** That needs an Authentik admin token, which LoadTest doesn't have.
  - **Rejected, blueprint the user with a password.** CreateTenant would hit Authentik's unique-username 400 and roll the tenant back.
  - **Rejected, restore a password option on CreateTenant.** That reverses CSR #4.

## Design

### `AuthentikFlowExecutorClient` (`Iverson.Server/Iverson.LoadTest/Auth/`)

A new public method, `Task SetPasswordFromRecoveryLinkAsync(string recoveryLink)`:

1. **Parse the link.** It is shaped like `http://authentik-server:9000/if/flow/iverson-recovery/?flow_token=…`. Take the flow slug (the last path segment) and the `flow_token` query value. Ignore the link's scheme, host and port: they are the API server's internal view of Authentik.
2. **Build the executor URL:** `{identity.BaseUrl}/api/v3/flows/executor/{slug}/?query=` followed by the URL-encoded `flow_token=<token>`.
3. **Drive the flow through the existing `SendAsync`.** That supplies the Host header, the `Accept: application/json` header, the cookie jar and the CSRF echo.
   - A GET must return the component `ak-stage-prompt`.
   - Then POST `{ "password": identity.Password, "password_repeat": identity.Password }`.
   - **The POST's own response** must have the component `xak-flow-redirect`, which means the flow completed (prompt → MFA check skipped → user write → login). Do not GET the executor again afterwards: the completed session is authenticated, and this flow requires an unauthenticated one, so a further GET returns `ak-stage-access-denied`.

A second constructor, `AuthentikFlowExecutorClient(AuthentikIdentityConfig, ILogger<AuthentikFlowExecutorClient>, HttpMessageHandler)`, builds its `HttpClient` over the given handler, so tests can inject a fake. The existing constructor is unchanged.

### `Program.cs` (`Iverson.Server/Iverson.LoadTest/`)

- **`EnsureTenantProvisionedAsync`** returns `Task<string?>`:
  - `null` when `ListTenants` already shows the tenant;
  - otherwise the `AdminRecoveryLink` from `CreateTenantAsync`'s `Tenant` response.

  Its comment about the removed password parameter is reworded to say the admin's password is now set through that link.
- **In the bootstrap block, after provisioning,** a non-null link is handled with a **separate** `AuthentikFlowExecutorClient`. It takes the same `AuthentikIdentityConfig` the login client gets (`tenantAdminUsername`, `tenantAdminPassword`, `actingUserClientId`, `actingUserRedirectUri`, `actingUserBaseUrl`, `actingUserHostHeader`, `actingUserCacheTarget`). It is used once for `SetPasswordFromRecoveryLinkAsync` and then disposed. A separate instance means a separate cookie jar: the recovery flow ends by logging the user in, and that session must not reach the login client. The login client then authenticates as before, with password and TOTP enrolment.

Nothing else changes: the server, proto, blueprints, environment variables and the dev default password stay as they are. The `kind` target takes the same path, since only the slug and token come from the link.

### Errors

Each case throws `InvalidOperationException` with a specific message. The bootstrap's existing `catch` (`Program.cs:124-128`) prints `Tenant provisioning failed: <message>` and exits 1, before any seeding or querying.

- **CreateTenant returned an empty `AdminRecoveryLink`.** The server returns an empty link when Authentik gave no `link`. The message says the tenant admin has no password, so the data plane cannot log in.
- **The link has no `flow_token` query value or no flow slug.** The message names the link as malformed. No request is sent.
- **The GET's component isn't `ak-stage-prompt`.** The message names the component.
- **The POST response has `response_errors`.** The message includes Authentik's error strings, for example `Password needs to be 8 characters or longer.`
- **The POST response's component isn't `xak-flow-redirect`.** The message names the component.

### Testing

A new `Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs` uses a recording fake `HttpMessageHandler`, in the style of `TeiRerankClientTests`. It covers:

1. **Happy path.**
   - The first request is a GET to `{BaseUrl}/api/v3/flows/executor/iverson-recovery/?query=flow_token%3D<token>`, not to the link's host, and carries the configured Host header.
   - The second is a POST whose JSON body has `password` and `password_repeat` equal to the identity's password.
   - There are exactly two requests.
2. **The POST response carries a `non_field_errors` policy error.** It throws, and the message contains that error's string.
3. **The first GET returns `ak-stage-access-denied`.** It throws, naming the component, and sends no POST.
4. **A link without `flow_token`.** It throws and sends no request.

**Live check.** The `Program.cs` wiring is top-level code with no test seam, so it gets a live check instead.
1. Run `SetPasswordFromRecoveryLinkAsync` once against an isolated Authentik. Start it from `Iverson.Server/docker-compose.yml` with its `container_name:` lines stripped, under its own compose project name, using only `postgres redis authentik-migrate authentik-server authentik-worker`, so it can't collide with the main stack's containers or volumes.
2. Create a `tenant-admins` user and mint its recovery link with the admin API, using `AUTHENTIK_BOOTSTRAP_TOKEN` and `Host: authentik-server:9000`.
3. Confirm that a fresh session's `default-authentication-flow` accepts the password and moves on to `ak-stage-authenticator-validate`.
4. Tear the stack down afterwards.

`dotnet test Iverson.Server/Iverson.LoadTest.Tests` must pass.

## Known issues / accepted as out of scope

Accepted by Ben under scope A, 2026-10-02:

- **A tenant that already exists without a password stays broken.** That covers tenants created before this change, and any whose recovery step failed after CreateTenant succeeded. LoadTest skips creation for them, so their data-plane logins fail on every call, as today.
  - **Manual fix:** set the user's password, for example `docker exec iverson-authentik-worker ak shell` then `User.objects.get(username="iverson-loadtest-tenant-admin").set_password(<password>)` and `.save()`, as done on 2026-09-28.
  - **Deleting and recreating the tenant is no remedy:** DeleteTenant only soft-deletes, marking the row `deleted` and deactivating the user, so a recreate collides on both.
- **After a recovery failure, the bootstrap's existing second error line can mislead.** It reads "Is the Iverson API running, and does IVERSON_CLIENT_SCOPE include 'admin schema_admin'?" and is printed for every provisioning failure. The first line carries the real reason.

## Verified assumptions

Probes P1–P5 ran 2026-10-02 against an isolated Authentik 2026.5.3. It was started from this repo's compose file and blueprints, under its own project name, with `container_name` lines stripped. Each probe followed the server's side through `IdpAdminClient`'s API calls, and LoadTest's side through `127.0.0.1:9000` with `Host: authentik-server:9000`, a cookie jar and the CSRF echo.

| # | Assumption | Evidence |
|---|---|---|
| 1 | A recovery link is `/if/flow/<slug>/` with a `flow_token` query value, on the server's internal host | P1: `POST /api/v3/core/users/{pk}/recovery/` returned host `authentik-server:9000`, path `/if/flow/iverson-recovery/`, query keys `['flow_token']` |
| 2 | For a new user, one POST completes the recovery flow | P2: the GET to `/api/v3/flows/executor/iverson-recovery/?query=flow_token%3D…` returned `ak-stage-prompt` with fields `[password, password_repeat]`. The POST returned `xak-flow-redirect`, because the MFA stage skips for a user with no enrolled device (`recovery-flow.yaml` `not_configured_action: skip`). |
| 3 | The password really is set | P3: a fresh session's `default-authentication-flow` went identification → password → `ak-stage-authenticator-validate` |
| 4 | Another GET after completion is denied | P2: `ak-stage-access-denied`, "Flow does not apply to current user" |
| 5 | A policy rejection appears in the POST response, and the password stays unset | P4: with `short1`, the POST returned `ak-stage-prompt` with `response_errors.non_field_errors[0].string` = "Password needs to be 8 characters or longer.", and a later login rejected the password |
| 6 | The dev default password passes the recovery password policy | P2/P3 used `dev-only-not-for-production-tenant-admin-password-0123456789` |
| 7 | A blueprinted user would break CreateTenant | P5: `POST /api/v3/core/users/` with an existing username returned 400 `{"username":["This field must be unique."]}`. CreateTenant deletes the tenant row when user creation throws (`TenantLifecycleGrpcService.cs:42-46`) |
| 8 | LoadTest holds no Authentik admin credential | `Program.cs` reads no Authentik token environment variable; its Authentik use is the flow-executor clients (`:117-121`, `:148-157`) |
| 9 | CreateTenant returns the link | `TenantLifecycleGrpcService.cs:55` `AdminRecoveryLink = adminUserResult.RecoveryLink ?? string.Empty`; `tenant_lifecycle.proto:56` `string admin_recovery_link = 4` |
| 10 | Changing `EnsureTenantProvisionedAsync`'s return type is safe | `git grep` in `Iverson.LoadTest*`: one definition (`Program.cs:351`), one call (`:112`), and the only `CreateTenantAsync` call (`:368`) |
| 11 | `SendAsync` supplies the Host and Accept headers and the CSRF echo for any URL | `AuthentikFlowExecutorClient.cs:122-143`; `MintAsync` builds its executor URL from `identity.BaseUrl` (`:272`) |
| 12 | The login identity uses `tenantAdminPassword` with the shared base URL and Host header | `Program.cs:117-121` |
| 13 | The client is `IDisposable`, and the CSRF read is harmless with a fake handler | `:22` `: IDisposable`; `:138` reads the client's own `CookieContainer`, which stays empty when a fake handler is injected |
| 14 | A throw in the bootstrap exits 1 with the reason | `Program.cs:124-128` |
| 15 | The test project can host the new tests | `Iverson.LoadTest.Tests.csproj` references `Iverson.LoadTest` plus xunit 2.9.3 and FluentAssertions 8.11.0. `NullLogger<T>` arrives through `Microsoft.Extensions.Logging.Console` (LoadTest's package) and is already used by the other server test projects. The project is in `Iverson.slnx`. |
| 16 | kind ships the same recovery flow | `charts/authentik/templates/blueprints-configmap.yaml:6` globs `blueprints/*.yaml`, which includes `recovery-flow.yaml`, mounted at `/blueprints/custom` on the server and worker |
| 17 | DeleteTenant can't roll back a failed recovery | `TenantLifecycleGrpcService.cs:73-84`: status `deleted` plus `DeactivateAllUsersInTenantAsync`, with no row or user deletion |

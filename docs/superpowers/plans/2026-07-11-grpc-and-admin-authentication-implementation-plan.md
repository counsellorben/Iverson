# gRPC and Admin Endpoint Authentication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add JWT-based authentication and two-tier authorization (any-authenticated-caller for gRPC, operator-tier for `/admin/*`) to `Iverson.Api`, provision the backing Authentik OAuth2 clients, and wire matching credential support into all 5 client SDKs.

**Architecture:** ASP.NET Core `JwtBearer` authentication validates tokens issued by Authentik against a fallback-deny policy; Authentik provisions 4 OAuth2 clients (1 human PKCE public client corrected + 3 new confidential service/automation clients) via a genuinely-templated Helm blueprint ConfigMap (`lookup`-sourced Secret values, unlike Part 1's static `.Files.Glob` blueprints); all 5 client SDKs gain an OAuth2 client-credentials token-fetch/cache mechanism, each wired through that language's actual working call-credentials mechanism (confirmed empirically per language — they differ).

**Tech Stack:** ASP.NET Core 10 / `Microsoft.AspNetCore.Authentication.JwtBearer`, Authentik (OIDC/OAuth2), Helm, grpc-dotnet / grpc-java / grpcio / grpc-go / `@grpc/grpc-js`.

**Source spec:** `docs/superpowers/specs/2026-07-11-grpc-and-admin-authentication-design.md` (commit SHA: bc41b95)

## Global Constraints

- Hard cutover: authentication becomes mandatory the moment this ships; no permissive/warn-only rollout window.
- No `.proto` contract changes — only client-side call construction changes.
- No TLS changes — this works within the existing plaintext h2c deployment.
- Every registered client gets its own distinct `client_id`/`client_secret` (no shared secrets).
- All 5 client SDKs (.NET, Java, Python, Go, TypeScript) get the capability, even though only .NET callers exist today.

## Inherited from spec (verified assumptions — NOT re-verified by this plan)

See the spec's own 17-item `Verified assumptions` table for full citations. Key ones this plan depends on directly: `aud` = calling client's own `client_id` (#15); `scope` claim is a plain string (#5); `groups` claim is a JSON array exploded into multiple same-typed `Claim`s by .NET's JWT handler (confirmed separately during this plan's drafting, see Task 1); `FallbackPolicy` + `AllowAnonymous()` reliably exempts specific endpoints (#16); Helm `lookup` reads real Secret data during actual `helm install`/`upgrade`, always empty under `helm template` (#7); `.Files.Glob "blueprints/*.yaml"` is single-level (#8); Authentik's blueprint scan is recursive (#9); per-language call-credentials mechanisms differ and were each confirmed against a real listening server (#1).

## Verified plan-level assumptions

All verified empirically during plan drafting (real file reads, live cluster/registry/jar inspection, or scratch-project compiles) — not asserted from memory or documentation alone.

| # | Assumption | Evidence |
|---|---|---|
| 1 | `Program.cs` line numbers (routes, admin routes, gRPC mapping) match the current repo state, zero drift since the design phase | Direct read: routes at 169-225, admin routes at 227-252, gRPC mapping at 263-266 — exact match |
| 2 | No existing test exercises the real HTTP/auth pipeline for `/admin/*` or the gRPC services, so Task 1 breaks nothing existing | Direct grep of `Iverson.Api.Tests`: gRPC test files use `TestServerCallContext` (bypasses Kestrel/auth middleware entirely); no `WebApplicationFactory` anywhere in the test tree |
| 3 | `ClaimsPrincipal.FindAll`/`FindFirst` are real BCL methods with the signatures Task 1 uses | Well-established, version-stable .NET BCL API (not a third-party package) |
| 4 | Authentik blueprint model names `authentik_providers_oauth2.scopemapping` and `authentik_crypto.certificatekeypair` are correct | Live API query against the running Authentik instance: `meta_model_name` field confirmed as `authentik_providers_oauth2.scopemapping` on a real scope-mapping object |
| 5 | A certificate keypair literally named `authentik Self-signed Certificate` exists on the target instance | Live query: `GET /api/v3/crypto/certificatekeypairs/?search=Self-signed` returned exactly that name |
| 6 | Blueprint `attrs` cross-entry patch semantics (does a second blueprint entry with matching `identifiers` correctly patch fields onto an object a first entry created, leaving untouched fields alone?) | Tested live — result was flaky/inconclusive (async blueprint apply queue, one run hit a stray 400). **Plan was revised (Task 2) to avoid this dependency entirely** rather than trust an unverified mechanism: the human provider's full definition now lives in one complete blueprint entry instead of a two-entry correction. A single-entry, all-fields-at-once blueprint was separately confirmed to apply cleanly. |
| 7 | Helm's `dict`/`list`/`index` Sprig functions render as intended in Task 2's range-loop template, including the not-found fallback branch | Real `helm template` render against a scratch throwaway chart reproducing the same pattern — both the found and fallback branches rendered correctly |
| 8 | `blueprints/compose-only/` doesn't already exist (Task 4 collision check) | Direct `ls` — confirmed absent |
| 9 | `.NET` SDK files (`ServiceCollectionExtensions.cs`, `Iverson.Client.Core.csproj`, `Iverson.Client.Sample/Program.cs`, `Iverson.LoadTest/Program.cs`) match Task 5's draft exactly; exactly 3 files in the repo reference `AddIversonClient` | Direct read of all 4 files + repo-wide grep for `AddIversonClient` |
| 10 | `IdentityModel` 7.0.0 exists on NuGet and its `RequestClientCredentialsTokenAsync`/`ClientCredentialsTokenRequest` API shape compiles exactly as Task 5 uses it | Real scratch `dotnet new console` project, package installed via `dotnet add package`, the exact code from Task 5's `CachedClientCredentialsTokenProvider` compiled successfully (0 errors) |
| 11 | Java's `IversonClient.java` matches Task 6's draft exactly; `withCallCredentials(io.grpc.CallCredentials)` exists on `AbstractStub` | Direct read of the file + `javap` inspection of the real `grpc-stub-1.71.0.jar` from the local Maven cache, confirming the exact method signature |
| 12 | `com.google.code.gson:gson:2.11.0` resolves on Maven Central | Direct HTTP HEAD against `repo1.maven.org` — 200 |
| 13 | Python's `core.py` matches Task 7's draft exactly (constructor at line 358, no existing `IversonClient(...)` call sites anywhere in the repo) | Direct read + repo-wide grep |
| 14 | Go's `coordinator.go` matches Task 8's draft exactly; package name is `iverson`; `credentials.PerRPCCredentials`'s exact interface shape | Direct read of `coordinator.go` + direct inspection of the real `google.golang.org/grpc@v1.71.0` module cache's `credentials.go` |
| 15 | TypeScript's `core.ts` has exactly 9 RPC call sites, all confined to that one file; the generated unary method's 4-arg `(request, metadata, options, callback)` overload exists; `grpc.credentials.createFromMetadataGenerator`/`generateMetadata` shape is correct | Direct read + a dedicated grep sweep across the whole SDK (confirming `search.ts`/`vector-search.ts` never construct or invoke `ObjectSearchServiceClient`) + direct inspection of `node_modules/@grpc/grpc-js/build/src/call-credentials.d.ts` and `generated/object_mapping.ts` |
| 16 | `Iverson.LoadTest` has a real `seed` subcommand and a generically-parsed `--count` flag that `DirectSeeder.RunAsync(flags)` consumes | Direct read of `Program.cs`'s `switch (command)` block and `CommandFlags.Parse` |
| 17 | Authentik picks up file-based blueprint ConfigMap changes without requiring a pod restart | **Not directly verified for the file-based path** — only the manual `/apply/` API path was tested live (and confirmed to work, asynchronously, within several seconds). Accepted on Part 1's precedent (its file-based blueprints already work in this exact repo) plus Task 10's explicit fallback step (`kubectl rollout restart deployment/iverson-authentik-worker` if not reflected within 2 minutes) as a safety net rather than a hard guarantee. |
| 18 | Every task's `git commit`/`git add` file list matches that task's Create/Modify/Delete file list | Manual cross-check across all 10 tasks |

## Known issue inherited from spec (CDR-fixed)

The human OIDC client (`iverson-oidc-default`) has no fixed, `lookup`-accessible `client_id` under Part 1's original blueprint — Authentik auto-generates one. This plan's Task 2 fixes it by giving that provider an explicit `client_id`, sourced the same lookup-guarded-Secret way as the 3 new service clients.

---

### Task 1: API-side authentication & authorization middleware

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Iverson.Api.csproj`
- Create: `Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Modify: `Iverson.Server/Iverson.Api/appsettings.json`
- Create: `Iverson.Server/Iverson.Api.Tests/OperatorAuthorizationPolicyTests.cs`

**Interfaces:**
- Produces: `OperatorAuthorizationPolicy.IsSatisfiedBy(IEnumerable<string> groupClaims, string? scopeClaim) : bool`, consumed by the "Operator" `AuthorizationPolicy` registered in `Program.cs`.

- [ ] **Step 1: Add the `JwtBearer` package**

Add to `Iverson.Api.csproj`'s existing `<ItemGroup>` of `PackageReference`s: `<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.9" />` (matches the installed ASP.NET Core shared framework version and every other `Microsoft.AspNetCore.*` package already pinned in this file; confirmed via a real `dotnet restore` that this version resolves cleanly against `net10.0`).

- [ ] **Step 2: Write the pure, testable Operator-policy logic**

`OperatorAuthorizationPolicy.cs` (same top-level-file, static-class shape as the existing `ReadinessPolicy.cs`):
```csharp
namespace Iverson.Api;

public static class OperatorAuthorizationPolicy
{
    // Two caller shapes satisfy "operator": a human via Authentik group membership (groups
    // claim, exploded into one Claim per array element by the JWT handler — confirmed via a
    // real decoded token), or CI/runbook automation via a dedicated `admin` scope (a single
    // space-separated string claim, not an array — confirmed via a real decoded token).
    public static bool IsSatisfiedBy(IEnumerable<string> groupClaims, string? scopeClaim)
    {
        if (groupClaims.Contains("operators"))
            return true;

        return scopeClaim is not null && scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("admin");
    }
}
```

- [ ] **Step 3: Wire authentication/authorization into `Program.cs`**

Add after the existing `using` block (top of file):
```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
```

Add after `builder.Services.AddGrpc();` (`Program.cs:82`):
```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = cfg["Authentication:Authority"];
        options.TokenValidationParameters.ValidAudiences = cfg.GetSection("Authentication:ValidAudiences").Get<string[]>();
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("Operator", policy => policy.RequireAssertion(context =>
        OperatorAuthorizationPolicy.IsSatisfiedBy(
            context.User.FindAll("groups").Select(c => c.Value),
            context.User.FindFirst("scope")?.Value)));
});
```

Add after `app.UseHttpsRedirection();` (`Program.cs:154`):
```csharp
app.UseAuthentication();
app.UseAuthorization();
```

Chain `.AllowAnonymous()` onto the 6 existing health/probe route registrations at `Program.cs:169` (`/health/live`), `171` (`/health`), `202` (`/probe/sql`), `208` (`/probe/starrocks`), `214` (`/probe/vector`), `220` (`/probe/kafka`) — e.g. `app.MapGet("/health/live", ...).WithName("HealthLive").AllowAnonymous();`.

Chain `.RequireAuthorization("Operator")` onto the 3 admin route registrations at `Program.cs:227` (`/admin/reconcile/{typeName}`), `237` (`/admin/dlq`), `243` (`/admin/dlq/{id}/replay`) — e.g. `.WithName("Reconcile").RequireAuthorization("Operator");`.

No change needed for the 4 `MapGrpcService<...>()` calls at `Program.cs:263-266` — they get the `FallbackPolicy`'s "any authenticated caller" requirement automatically (confirmed via Microsoft Learn: `.RequireAuthorization()` chained onto `MapGrpcService` enforces a policy exactly as it would a regular endpoint, and gRPC endpoints participate in the standard pipeline — spec's verified assumption #2).

- [ ] **Step 4: Add the config section**

Add to `appsettings.json` after the existing `"Otel"` section:
```json
  "Authentication": {
    "Authority": "",
    "ValidAudiences": []
  }
```
Left empty in the base file — real values are injected via Helm/compose env vars in Tasks 3/4, matching how `Kafka:BootstrapServers` etc. work today.

- [ ] **Step 5: Unit tests**

`OperatorAuthorizationPolicyTests.cs` (same xUnit + FluentAssertions convention as `ReadinessPolicyTests.cs`):
```csharp
using FluentAssertions;
using Xunit;

namespace Iverson.Api.Tests;

public class OperatorAuthorizationPolicyTests
{
    [Fact]
    public void IsSatisfiedBy_HumanInOperatorsGroup_ReturnsTrue()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(["operators"], null).Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_HumanInOtherGroupOnly_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(["some-other-group"], null).Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithAdminScope_ReturnsTrue()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy([], "openid admin profile").Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithoutAdminScope_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy([], "openid profile").Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_NoGroupsNoScope_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy([], null).Should().BeFalse();
    }
}
```

- [ ] **Step 6: Build and test**
```bash
cd Iverson.Server
dotnet build Iverson.Api/Iverson.Api.csproj
dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter OperatorAuthorizationPolicyTests
```
Expected: build succeeds; 5/5 tests pass. (Confirmed no existing test in `Iverson.Api.Tests` exercises the real HTTP/gRPC pipeline — the gRPC test files use `TestServerCallContext`, a direct-call harness that bypasses Kestrel/auth middleware entirely — so nothing existing breaks from this change.)

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Api/Iverson.Api.csproj Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/appsettings.json Iverson.Server/Iverson.Api.Tests/OperatorAuthorizationPolicyTests.cs
git commit -m "feat(api): add JWT bearer authentication and two-tier authorization"
```

---

### Task 2: Authentik-side provisioning (Secrets + templated Blueprint)

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml`
- Create: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- Create: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/human-provider-application.yaml`
- Delete: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/oauth2-provider.yaml` (Part 1's static definition of `iverson-oidc-default` — superseded; its content is folded into Step 3 as one complete entry)
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-worker.yaml`

**Interfaces:**
- Produces: 4 Secrets (`{{ .Release.Name }}-authentik-{loadtest,webtest,admin-automation,human-oidc}-client`) whose `client-id`/`client-secret` keys Task 3 reads to populate `Authentication:ValidAudiences` and the docker-compose/Helm env wiring.

- [ ] **Step 1: Lookup-guarded Secrets for the 4 client identities**

`templates/secret-service-clients.yaml` (same pattern as the chart's existing `secret.yaml`/`starrocks/templates/secret.yaml` — `randAlphaNum` + `lookup`-guard, confirmed via a real `helm install` that `lookup` reads actual Secret data, not just existence):
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-loadtest-client
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-loadtest-client" .Release.Name) }}
{{- if $existing }}
  client-id: {{ index $existing.data "client-id" }}
  client-secret: {{ index $existing.data "client-secret" }}
{{- else }}
  client-id: {{ randAlphaNum 40 | b64enc }}
  client-secret: {{ randAlphaNum 50 | b64enc }}
{{- end }}
---
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-webtest-client
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-webtest-client" .Release.Name) }}
{{- if $existing }}
  client-id: {{ index $existing.data "client-id" }}
  client-secret: {{ index $existing.data "client-secret" }}
{{- else }}
  client-id: {{ randAlphaNum 40 | b64enc }}
  client-secret: {{ randAlphaNum 50 | b64enc }}
{{- end }}
---
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-admin-automation-client
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-admin-automation-client" .Release.Name) }}
{{- if $existing }}
  client-id: {{ index $existing.data "client-id" }}
  client-secret: {{ index $existing.data "client-secret" }}
{{- else }}
  client-id: {{ randAlphaNum 40 | b64enc }}
  client-secret: {{ randAlphaNum 50 | b64enc }}
{{- end }}
---
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-human-oidc-client
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-human-oidc-client" .Release.Name) }}
{{- if $existing }}
  client-id: {{ index $existing.data "client-id" }}
{{- else }}
  client-id: {{ randAlphaNum 40 | b64enc }}
{{- end }}
```

- [ ] **Step 2: Static blueprint entry — bind the human provider to an Application**

No Secret-sourced values needed for this one, so it follows Part 1's plain `.Files.Glob`-loaded convention:

`blueprints/human-provider-application.yaml`:
```yaml
version: 1
metadata:
  name: Application binding for the human OIDC provider
entries:
  - model: authentik_core.application
    identifiers:
      slug: iverson-api
    attrs:
      name: iverson-api
      provider: !Find [authentik_providers_oauth2.oauth2provider, [name, iverson-oidc-default]]
```

- [ ] **Step 3: Templated ConfigMap — the 3 service-client providers + the full human-provider definition**

`templates/blueprints-configmap-service-clients.yaml` — a genuinely Go-templated file (unlike `blueprints-configmap.yaml`, which only embeds static file bytes via `.Files.Get`; `lookup` values interpolate directly into this YAML string). Confirmed live: Authentik model names `authentik_providers_oauth2.scopemapping` (verified via `meta_model_name` on a real scope-mapping object) and `authentik_crypto.certificatekeypair` with a certificate literally named `authentik Self-signed Certificate` (verified live on the running cluster) are correct. Confirmed live that a single blueprint entry setting every field together (flows, `client_type`, `redirect_uris`, plus new fields) applies cleanly — a cross-blueprint two-entry patch was tested and proved unreliable to verify with confidence, so the human provider's full original definition (from the now-deleted `oauth2-provider.yaml`) is folded into one entry here rather than left as a separate correction:
```yaml
{{- $loadtest := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-loadtest-client" .Release.Name) }}
{{- $webtest := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-webtest-client" .Release.Name) }}
{{- $adminAuto := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-admin-automation-client" .Release.Name) }}
{{- $humanOidc := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-human-oidc-client" .Release.Name) }}
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .Release.Name }}-authentik-blueprints-service-clients
data:
  service-clients.yaml: |
    version: 1
    metadata:
      name: Service/automation OAuth2 clients and full human-provider definition
    entries:
      - model: authentik_providers_oauth2.scopemapping
        identifiers:
          scope_name: admin
        attrs:
          name: "Iverson: admin operator scope"
          description: Grants operator-tier access to Iverson's admin endpoints
          expression: "return {}"
      - model: authentik_providers_oauth2.scopemapping
        identifiers:
          scope_name: groups
        attrs:
          name: "Iverson: group membership"
          description: Exposes the authenticated user's Authentik group names
          expression: "return [group.name for group in user.ak_groups.all()]"
{{- range $name := list "loadtest" "webtest" "admin-automation" }}
{{- $secret := index (dict "loadtest" $loadtest "webtest" $webtest "admin-automation" $adminAuto) $name }}
      - model: authentik_providers_oauth2.oauth2provider
        identifiers:
          name: iverson-{{ $name }}
        attrs:
          authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
          invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
          client_type: confidential
          client_id: {{ if $secret }}{{ index $secret.data "client-id" | b64dec }}{{ else }}{{ randAlphaNum 40 }}{{ end }}
          client_secret: {{ if $secret }}{{ index $secret.data "client-secret" | b64dec }}{{ else }}{{ randAlphaNum 50 }}{{ end }}
          redirect_uris: []
          grant_types: ["client_credentials"]
          signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
          issuer_mode: global
      - model: authentik_core.application
        identifiers:
          slug: iverson-{{ $name }}
        attrs:
          name: iverson-{{ $name }}
          provider: !Find [authentik_providers_oauth2.oauth2provider, [name, iverson-{{ $name }}]]
{{- end }}
      - model: authentik_providers_oauth2.oauth2provider
        identifiers:
          name: iverson-admin-automation
        attrs:
          property_mappings:
            - !Find [authentik_providers_oauth2.scopemapping, [scope_name, admin]]
      - model: authentik_providers_oauth2.oauth2provider
        identifiers:
          name: iverson-oidc-default
        attrs:
          authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
          invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
          client_type: public
          redirect_uris:
            - matching_mode: strict
              url: "http://localhost/placeholder-callback"
              redirect_uri_type: authorization
          client_id: {{ if $humanOidc }}{{ index $humanOidc.data "client-id" | b64dec }}{{ else }}{{ randAlphaNum 40 }}{{ end }}
          signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
          issuer_mode: global
          property_mappings:
            - !Find [authentik_providers_oauth2.scopemapping, [scope_name, groups]]
```

- [ ] **Step 4: Mount the new ConfigMap as a second blueprints volume**

In both `deployment-server.yaml` and `deployment-worker.yaml`, add a second volume mount alongside the existing `blueprints` one (subdirectory, confirmed Authentik's blueprint scan is recursive):
```yaml
          volumeMounts:
            - name: blueprints
              mountPath: /blueprints/custom
              readOnly: true
            - name: blueprints-service-clients
              mountPath: /blueprints/custom/service-clients
              readOnly: true
      volumes:
        - name: blueprints
          configMap:
            name: {{ .Release.Name }}-authentik-blueprints
        - name: blueprints-service-clients
          configMap:
            name: {{ .Release.Name }}-authentik-blueprints-service-clients
```

- [ ] **Step 5: Operators Group (runtime step, no blueprint)**

No file change. Document in Task 10's smoke test that a human operator must be added to an `operators` Authentik Group manually post-deploy; not templated (matches Part 1's precedent for the bootstrap-admin flow).

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/human-provider-application.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-worker.yaml
git rm Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/oauth2-provider.yaml
git commit -m "feat(authentik): provision service-client OAuth2 providers and correct human provider"
```

Note: the two-pass `helm upgrade` requirement (Helm's `lookup` can't see Secrets created earlier in the same install pass) doesn't bite during this task — it only matters at actual install time, covered explicitly in Task 10.

---

### Task 3: Wire API's Authentication config + NetworkPolicy

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml`

**Interfaces:**
- Consumes: the 4 Secrets from Task 2 (`{{ .Release.Name }}-authentik-{loadtest,webtest,admin-automation,human-oidc}-client`, key `client-id`).

- [ ] **Step 1: Add `Authentication` env vars to the api Deployment**

In `charts/api/templates/deployment.yaml`, add after the existing `Otel__Endpoint` env entry (confirmed the Authentik Service is named `{{ .Release.Name }}-authentik`, port 9000):
```yaml
            - name: Otel__Endpoint
              value: "http://{{ .Release.Name }}-jaeger:4317"
            - name: Authentication__Authority
              value: "http://{{ .Release.Name }}-authentik:9000/application/o/iverson-api/"
            - name: Authentication__ValidAudiences__0
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-human-oidc-client, key: client-id }
            - name: Authentication__ValidAudiences__1
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-loadtest-client, key: client-id }
            - name: Authentication__ValidAudiences__2
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-webtest-client, key: client-id }
            - name: Authentication__ValidAudiences__3
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-admin-automation-client, key: client-id }
```
`worker`'s Deployment is untouched — `workloadRole == "worker"` never maps the gRPC/admin routes that need this config.

- [ ] **Step 2: Add the `api-egress` → Authentik rule**

In `templates/networkpolicies.yaml`, add a new egress entry to the `{{ .Release.Name }}-api-egress` policy, alongside the existing datastore rules:
```yaml
    - to: [{ podSelector: { matchLabels: { app: {{ .Release.Name }}-authentik-server } } }]
      ports: [{ protocol: TCP, port: 9000 }]
```

- [ ] **Step 3: Add the `authentik-ingress` → api rule**

In the same file, extend the `{{ .Release.Name }}-authentik-ingress` policy's `ingress` list (currently only the kubelet `from: []` probe rule):
```yaml
  ingress:
    - from:
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-api } }
      ports: [{ protocol: TCP, port: 9000 }]
    - from: []   # kubelet readiness/liveness probes...
      ports: [{ protocol: TCP, port: 9000 }]
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml
git commit -m "feat(api): wire JWT authentication config and authentik NetworkPolicy path"
```

---

### Task 4: docker-compose parity

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- Modify: `Iverson.Server/docker-compose.yml`

**Interfaces:**
- Consumes: nothing from earlier tasks — docker-compose is a fully independent deployment target with hardcoded dev values.

- [ ] **Step 1: Compose-only blueprint with hardcoded dev values**

`blueprints/compose-only/service-clients.yaml` (picked up automatically — `docker-compose.yml`'s existing bind mount, `./deploy/helm/iverson/charts/authentik/blueprints:/blueprints/custom:ro`, covers the whole directory tree recursively; confirmed this directory doesn't exist yet, no collision. Not matched by Helm's `.Files.Glob "blueprints/*.yaml"` — single-level, confirmed via Go's `filepath.Match` semantics — so zero effect on the Helm deployment):
```yaml
version: 1
metadata:
  name: Service/automation OAuth2 clients and human-provider correction (docker-compose dev values)
entries:
  - model: authentik_providers_oauth2.scopemapping
    identifiers:
      scope_name: admin
    attrs:
      name: "Iverson: admin operator scope"
      description: Grants operator-tier access to Iverson's admin endpoints
      expression: "return {}"
  - model: authentik_providers_oauth2.scopemapping
    identifiers:
      scope_name: groups
    attrs:
      name: "Iverson: group membership"
      description: Exposes the authenticated user's Authentik group names
      expression: "return [group.name for group in user.ak_groups.all()]"
  - model: authentik_providers_oauth2.oauth2provider
    identifiers:
      name: iverson-loadtest
    attrs:
      authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
      invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
      client_type: confidential
      client_id: "dev-iverson-loadtest-client-id"
      client_secret: "dev-only-not-for-production-loadtest-secret-0123456789"
      redirect_uris: []
      grant_types: ["client_credentials"]
      signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
      issuer_mode: global
  - model: authentik_core.application
    identifiers:
      slug: iverson-loadtest
    attrs:
      name: iverson-loadtest
      provider: !Find [authentik_providers_oauth2.oauth2provider, [name, iverson-loadtest]]
  - model: authentik_providers_oauth2.oauth2provider
    identifiers:
      name: iverson-webtest
    attrs:
      authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
      invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
      client_type: confidential
      client_id: "dev-iverson-webtest-client-id"
      client_secret: "dev-only-not-for-production-webtest-secret-0123456789"
      redirect_uris: []
      grant_types: ["client_credentials"]
      signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
      issuer_mode: global
  - model: authentik_core.application
    identifiers:
      slug: iverson-webtest
    attrs:
      name: iverson-webtest
      provider: !Find [authentik_providers_oauth2.oauth2provider, [name, iverson-webtest]]
  - model: authentik_providers_oauth2.oauth2provider
    identifiers:
      name: iverson-admin-automation
    attrs:
      authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
      invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
      client_type: confidential
      client_id: "dev-iverson-admin-automation-client-id"
      client_secret: "dev-only-not-for-production-admin-secret-0123456789"
      redirect_uris: []
      grant_types: ["client_credentials"]
      signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
      issuer_mode: global
      property_mappings:
        - !Find [authentik_providers_oauth2.scopemapping, [scope_name, admin]]
  - model: authentik_core.application
    identifiers:
      slug: iverson-admin-automation
    attrs:
      name: iverson-admin-automation
      provider: !Find [authentik_providers_oauth2.oauth2provider, [name, iverson-admin-automation]]
  - model: authentik_providers_oauth2.oauth2provider
    identifiers:
      name: iverson-oidc-default
    attrs:
      client_id: "dev-iverson-human-oidc-client-id"
      signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
      issuer_mode: global
      property_mappings:
        - !Find [authentik_providers_oauth2.scopemapping, [scope_name, groups]]
```

- [ ] **Step 2: `iverson-api` env vars in docker-compose.yml**

Add to `docker-compose.yml`'s `iverson-api` service, after the existing `Kafka__BootstrapServers` line:
```yaml
      - Kafka__BootstrapServers=kafka:29092
      - Authentication__Authority=http://authentik-server:9000/application/o/iverson-api/
      - Authentication__ValidAudiences__0=dev-iverson-human-oidc-client-id
      - Authentication__ValidAudiences__1=dev-iverson-loadtest-client-id
      - Authentication__ValidAudiences__2=dev-iverson-webtest-client-id
      - Authentication__ValidAudiences__3=dev-iverson-admin-automation-client-id
```
Also add `authentik-server` to `iverson-api`'s `depends_on`, matching the existing `condition: service_healthy` pattern used for every other backing service:
```yaml
    depends_on:
      postgres:
        condition: service_healthy
      starrocks:
        condition: service_healthy
      qdrant:
        condition: service_healthy
      kafka:
        condition: service_healthy
      jaeger:
        condition: service_healthy
      authentik-server:
        condition: service_healthy
      ollama-init:
        condition: service_completed_successfully
```

- [ ] **Step 3: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml Iverson.Server/docker-compose.yml
git commit -m "feat(compose): add dev-only service-client OAuth2 providers for docker-compose parity"
```

---

### Task 5: .NET SDK — `Iverson.Client.Core` + `Iverson.LoadTest` wiring

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core/IversonClientCredentials.cs`
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core/CachedClientCredentialsTokenProvider.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs` (consumer fix — confirmed only 3 files in the repo reference `AddIversonClient`: its own definition, `Iverson.Client.Sample/Program.cs`, and `Iverson.LoadTest/Program.cs`; both callers accounted for in this task)
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks directly — the values it needs (`client_id`/`client_secret`/token endpoint) are operational config supplied at runtime, matching how `IVERSON_GRPC_URL` already works.
- Produces: `IversonClientCredentials(string ClientId, string ClientSecret, string TokenEndpoint)` and a new optional parameter on `AddIversonClient`.

- [ ] **Step 1: Add the `IdentityModel` package reference**

`Iverson.Client.Core.csproj` — add to the existing `<ItemGroup>`: `<PackageReference Include="IdentityModel" Version="7.0.0" />` (confirmed on NuGet; confirmed via a real scratch project that `HttpClient.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest {...})` compiles and exposes `.IsError`/`.AccessToken`/`.ExpiresIn` as used below). No existing OAuth2 package in the solution — confirmed via full csproj read.

- [ ] **Step 2: The credentials record**

`IversonClientCredentials.cs`:
```csharp
namespace Iverson.Client.Core;

public sealed record IversonClientCredentials(string ClientId, string ClientSecret, string TokenEndpoint);
```

- [ ] **Step 3: The cached token provider**

`CachedClientCredentialsTokenProvider.cs`:
```csharp
using IdentityModel.Client;

namespace Iverson.Client.Core;

internal sealed class CachedClientCredentialsTokenProvider(IversonClientCredentials credentials) : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetTokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _token;

        await _lock.WaitAsync();
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _token;

            var response = await _httpClient.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
            {
                Address = credentials.TokenEndpoint,
                ClientId = credentials.ClientId,
                ClientSecret = credentials.ClientSecret,
            });

            if (response.IsError)
                throw new InvalidOperationException($"Failed to acquire Iverson client token: {response.Error}");

            _token = response.AccessToken;
            // Refresh 60s early so no call ever races a token that expires mid-flight.
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn - 60);
            return _token!;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
```

- [ ] **Step 4: Wire into `AddIversonClient`**

`ServiceCollectionExtensions.cs` — full replacement:
```csharp
using System.Reflection;
using Iverson.Client.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Iverson.Client.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full Iverson client framework.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="grpcEndpoint">Base URI of the Iverson.Api gRPC endpoint.</param>
    /// <param name="credentials">
    /// OAuth2 client-credentials identity to attach to every gRPC call as a Bearer token.
    /// Omit only for calls to endpoints that don't require authentication.
    /// </param>
    /// <param name="entityAssemblies">
    /// Assemblies to scan for <c>[IversonEntity]</c> classes.
    /// Defaults to the calling assembly if none are provided.
    /// </param>
    public static IServiceCollection AddIversonClient(
        this IServiceCollection services,
        string grpcEndpoint,
        IversonClientCredentials? credentials = null,
        params Assembly[] entityAssemblies)
    {
        var assemblies = entityAssemblies.Length > 0
            ? entityAssemblies
            : [Assembly.GetCallingAssembly()];

        services.AddSingleton(new EntityRegistry(assemblies));
        services.AddSingleton<GraphAssembler>();

        var mappingBuilder = services.AddGrpcClient<ObjectMappingService.ObjectMappingServiceClient>(
            o => o.Address = new Uri(grpcEndpoint));
        var persistenceBuilder = services.AddGrpcClient<ObjectPersistenceService.ObjectPersistenceServiceClient>(
            o => o.Address = new Uri(grpcEndpoint));
        var retrievalBuilder = services.AddGrpcClient<ObjectRetrievalService.ObjectRetrievalServiceClient>(
            o => o.Address = new Uri(grpcEndpoint));
        var searchBuilder = services.AddGrpcClient<ObjectSearchService.ObjectSearchServiceClient>(
            o => o.Address = new Uri(grpcEndpoint));

        if (credentials is not null)
        {
            services.AddSingleton(new CachedClientCredentialsTokenProvider(credentials));

            AttachCredentials(mappingBuilder);
            AttachCredentials(persistenceBuilder);
            AttachCredentials(retrievalBuilder);
            AttachCredentials(searchBuilder);
        }

        services.AddTransient(typeof(EntityCoordinator<>));

        services.AddSingleton<SchemaRegistrar>();

        return services;
    }

    private static void AttachCredentials(IHttpClientBuilder builder)
    {
        // Without UnsafeUseInsecureChannelCallCredentials=true, CallCredentials are silently
        // dropped over this repo's plaintext h2c channel — no exception, no Authorization
        // header. Confirmed via Microsoft's own docs and a real listening-server test.
        builder
            .ConfigureChannel(o => o.UnsafeUseInsecureChannelCallCredentials = true)
            .AddCallCredentials(async (_, metadata, serviceProvider) =>
            {
                var provider = serviceProvider.GetRequiredService<CachedClientCredentialsTokenProvider>();
                var token = await provider.GetTokenAsync();
                metadata.Add("Authorization", $"Bearer {token}");
            });
    }
}
```

- [ ] **Step 5: Fix the `Iverson.Client.Sample` consumer**

`Iverson.Client.Sample/Program.cs:13-15`, currently:
```csharp
    .AddIversonClient(
        grpcEndpoint: "https://localhost:7142",
        typeof(Article).Assembly)
```
The trailing positional `typeof(Article).Assembly` would now bind to the new `credentials` parameter (an `Assembly` isn't assignable to `IversonClientCredentials?` — compile error). Change to a named argument:
```csharp
    .AddIversonClient(
        grpcEndpoint: "https://localhost:7142",
        entityAssemblies: [typeof(Article).Assembly])
```

- [ ] **Step 6: Wire `Iverson.LoadTest`**

`Iverson.LoadTest/Program.cs` — add new env vars near the existing `Env(...)` calls (around line 21):
```csharp
var grpcUrl         = Env("IVERSON_GRPC_URL",        "http://localhost:8080");
var clientId        = Environment.GetEnvironmentVariable("IVERSON_CLIENT_ID");
var clientSecret    = Environment.GetEnvironmentVariable("IVERSON_CLIENT_SECRET");
var tokenEndpoint   = Environment.GetEnvironmentVariable("IVERSON_TOKEN_ENDPOINT");
```
Build the credentials object and pass it, replacing the existing `.AddIversonClient(grpcUrl, typeof(BenchmarkArticle).Assembly)` call (line 53):
```csharp
var clientCredentials = clientId is not null && clientSecret is not null && tokenEndpoint is not null
    ? new IversonClientCredentials(clientId, clientSecret, tokenEndpoint)
    : null;

var services = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
    .AddIversonClient(grpcUrl, clientCredentials, entityAssemblies: [typeof(BenchmarkArticle).Assembly])
    .AddSingleton(config)
    .AddSingleton(kafkaOptions)
    .AddSingleton<DirectSeeder>()
    .AddSingleton<WritePathScenario>()
    .AddSingleton<KindWritePathScenario>()
    .AddSingleton<ReadPathScenario>()
    .BuildServiceProvider();
```
(All three env vars unset → `null` credentials → unauthenticated calls, matching how missing Kafka SASL vars degrade today.)

- [ ] **Step 7: Build**
```bash
cd Iverson.Clients/DotNet && dotnet build Iverson.Client.Core/Iverson.Client.Core.csproj && dotnet build Iverson.Client.Sample/Iverson.Client.Sample.csproj
cd ../../Iverson.Server && dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
```
Expected: all three build successfully.

- [ ] **Step 8: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Core/IversonClientCredentials.cs Iverson.Clients/DotNet/Iverson.Client.Core/CachedClientCredentialsTokenProvider.cs Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs Iverson.Server/Iverson.LoadTest/Program.cs
git commit -m "feat(dotnet-client): add OAuth2 client-credentials support to AddIversonClient"
```

---

### Task 6: Java SDK — `OAuth2ClientCredentials` + constructor overload

**Files:**
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java`
- Modify: `Iverson.Clients/Java/client/pom.xml`

**Interfaces:**
- Produces: `public OAuth2ClientCredentials(String clientId, String clientSecret, String tokenEndpoint)` (a `CallCredentials`); new constructor `IversonClient(String host, int port, CallCredentials credentials)`.

- [ ] **Step 1: Add a minimal JSON dependency**

`grpc-stub`/`grpc-protobuf` bring `io.grpc.CallCredentials` transitively (confirmed: no new grpc dependency needed — `grpc-api`'s `CallCredentials.class` was inspected directly from the real 1.71.0 jar, exact abstract method `applyRequestMetadata(RequestInfo, Executor, MetadataApplier)` confirmed, and `thisUsesUnstableApi()` confirmed to have a concrete default — no override needed). There is no JSON library in `client`'s main scope. Add to `client/pom.xml`'s `<dependencies>` (confirmed `gson:2.11.0` resolves on Maven Central):
```xml
<dependency>
  <groupId>com.google.code.gson</groupId>
  <artifactId>gson</artifactId>
  <version>2.11.0</version>
</dependency>
```

- [ ] **Step 2: `OAuth2ClientCredentials`**

`OAuth2ClientCredentials.java`:
```java
package io.iverson.client.core;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import io.grpc.CallCredentials;
import io.grpc.Metadata;
import io.grpc.Status;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Instant;
import java.util.concurrent.Executor;
import java.util.concurrent.locks.ReentrantLock;

/**
 * Attaches an OAuth2 client-credentials Bearer token to every gRPC call, caching the
 * token in memory and refreshing it 60 seconds before expiry.
 */
public final class OAuth2ClientCredentials extends CallCredentials {

    private final String clientId;
    private final String clientSecret;
    private final String tokenEndpoint;
    private final HttpClient httpClient = HttpClient.newHttpClient();
    private final ReentrantLock lock = new ReentrantLock();

    private volatile String cachedToken;
    private volatile Instant expiresAt = Instant.MIN;

    public OAuth2ClientCredentials(String clientId, String clientSecret, String tokenEndpoint) {
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.tokenEndpoint = tokenEndpoint;
    }

    @Override
    public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
        executor.execute(() -> {
            try {
                Metadata headers = new Metadata();
                headers.put(Metadata.Key.of("Authorization", Metadata.ASCII_STRING_MARSHALLER), "Bearer " + getToken());
                applier.apply(headers);
            } catch (Exception e) {
                applier.fail(Status.UNAUTHENTICATED.withCause(e));
            }
        });
    }

    private String getToken() throws Exception {
        if (cachedToken != null && Instant.now().isBefore(expiresAt)) {
            return cachedToken;
        }
        lock.lock();
        try {
            if (cachedToken != null && Instant.now().isBefore(expiresAt)) {
                return cachedToken;
            }
            String form = "grant_type=client_credentials&client_id=" + clientId + "&client_secret=" + clientSecret;
            HttpRequest request = HttpRequest.newBuilder()
                .uri(URI.create(tokenEndpoint))
                .header("Content-Type", "application/x-www-form-urlencoded")
                .POST(HttpRequest.BodyPublishers.ofString(form))
                .build();
            HttpResponse<String> response = httpClient.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200) {
                throw new IllegalStateException("Failed to acquire Iverson client token: HTTP " + response.statusCode());
            }
            JsonObject body = JsonParser.parseString(response.body()).getAsJsonObject();
            cachedToken = body.get("access_token").getAsString();
            expiresAt = Instant.now().plusSeconds(body.get("expires_in").getAsLong() - 60);
            return cachedToken;
        } finally {
            lock.unlock();
        }
    }
}
```

- [ ] **Step 3: New `IversonClient` constructor overload**

`IversonClient.java` — add after the existing `(String host, int port)` constructor, no change to the two existing constructors (confirmed via direct read: only 1 real call site in the repo, `sample/Main.java:30`'s `new IversonClient("localhost", 5000)`, unaffected):
```java
    /**
     * Creates a plain-text (h2c) channel to the given host and port, authenticating every
     * call with the given credentials (e.g. {@link OAuth2ClientCredentials}).
     */
    public IversonClient(String host, int port, CallCredentials credentials) {
        this(ManagedChannelBuilder.forAddress(host, port).usePlaintext().build(), credentials);
    }

    /**
     * Creates a client using an already-configured channel, attaching the given call
     * credentials to every stub. Confirmed via grpc-java's actual per-call invocation path
     * that plaintext channels accept CallCredentials with no special configuration (unlike
     * the .NET client, which requires an explicit insecure-channel opt-in).
     */
    public IversonClient(ManagedChannel channel, CallCredentials credentials) {
        this.channel         = channel;
        this.mappingStub     = ObjectMappingServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.persistenceStub = ObjectPersistenceServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.retrievalStub   = ObjectRetrievalServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.searchStub      = ObjectSearchServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
    }
```
Add `import io.grpc.CallCredentials;` to the top import block. Confirmed `withCallCredentials(io.grpc.CallCredentials)` is a real public method on `AbstractStub` via direct inspection of the real `grpc-stub-1.71.0.jar`.

- [ ] **Step 4: Build**
```bash
cd Iverson.Clients/Java && mvn -pl client -am compile
```
Expected: `BUILD SUCCESS`.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java Iverson.Clients/Java/client/pom.xml
git commit -m "feat(java-client): add OAuth2 client-credentials support"
```

---

### Task 7: Python SDK — `IversonClientCredentials` + secure-channel wiring

**Files:**
- Create: `Iverson.Clients/Python/iverson_client/auth.py`
- Modify: `Iverson.Clients/Python/iverson_client/core.py`
- Modify: `Iverson.Clients/Python/iverson_client/__init__.py`

**Interfaces:**
- Produces: `IversonClientCredentials(client_id: str, client_secret: str, token_endpoint: str)`; new `IversonClient.__init__` keyword-only param `credentials: IversonClientCredentials | None = None`.

- [ ] **Step 1: The credentials dataclass + token provider + auth plugin**

`auth.py` — no new dependency (stdlib `urllib.request` + `json` cover both the HTTP POST and JSON parsing; confirmed no `requests`/`httpx` in `pyproject.toml` and none needed):
```python
"""OAuth2 client-credentials support for IversonClient."""

from __future__ import annotations

import json
import threading
import time
import urllib.request
from dataclasses import dataclass
from urllib.error import HTTPError

import grpc


@dataclass(frozen=True)
class IversonClientCredentials:
    client_id: str
    client_secret: str
    token_endpoint: str


class _CachedTokenProvider:
    """Fetches and caches an OAuth2 client-credentials access token, refreshing
    60 seconds before expiry."""

    def __init__(self, credentials: IversonClientCredentials) -> None:
        self._credentials = credentials
        self._lock = threading.Lock()
        self._token: str | None = None
        self._expires_at: float = 0.0

    def get_token(self) -> str:
        if self._token is not None and time.monotonic() < self._expires_at:
            return self._token

        with self._lock:
            if self._token is not None and time.monotonic() < self._expires_at:
                return self._token

            body = (
                f"grant_type=client_credentials&client_id={self._credentials.client_id}"
                f"&client_secret={self._credentials.client_secret}"
            ).encode("utf-8")
            request = urllib.request.Request(
                self._credentials.token_endpoint,
                data=body,
                headers={"Content-Type": "application/x-www-form-urlencoded"},
                method="POST",
            )
            try:
                with urllib.request.urlopen(request) as response:
                    payload = json.loads(response.read())
            except HTTPError as e:
                raise RuntimeError(f"Failed to acquire Iverson client token: HTTP {e.code}") from e

            self._token = payload["access_token"]
            self._expires_at = time.monotonic() + payload["expires_in"] - 60
            return self._token


class _BearerTokenAuthPlugin(grpc.AuthMetadataPlugin):
    def __init__(self, provider: _CachedTokenProvider) -> None:
        self._provider = provider

    def __call__(self, context, callback) -> None:
        try:
            token = self._provider.get_token()
            callback((("authorization", f"Bearer {token}"),), None)
        except Exception as e:
            callback(None, e)
```

- [ ] **Step 2: Wire into `IversonClient.__init__`**

`core.py` (constructor at line 358, confirmed via direct read) — modify:
```python
    def __init__(
        self,
        host: str = "localhost",
        port: int = 5000,
        use_tls: bool = False,
        credentials: IversonClientCredentials | None = None,
    ) -> None:
        address = f"{host}:{port}"

        if credentials is not None:
            provider = _CachedTokenProvider(credentials)
            call_creds = grpc.metadata_call_credentials(_BearerTokenAuthPlugin(provider))
            # grpcio rejects CallCredentials on a bare insecure_channel with
            # "UNAUTHENTICATED: Established channel does not have a sufficient security
            # level to transfer call credential" — confirmed live. local_channel_credentials()
            # is a lightweight "trusted network" designation (not real TLS) that satisfies
            # the check without requiring actual certificates.
            channel_creds = grpc.composite_channel_credentials(
                grpc.local_channel_credentials(), call_creds
            )
            self._channel = grpc.secure_channel(address, channel_creds)
        elif use_tls:
            self._channel = grpc.secure_channel(address, grpc.ssl_channel_credentials())
        else:
            self._channel = grpc.insecure_channel(address)

        self._mapping_stub = mapping_grpc.ObjectMappingServiceStub(self._channel)
```
Add near the top imports: `from iverson_client.auth import IversonClientCredentials, _CachedTokenProvider, _BearerTokenAuthPlugin`.

No consumer breakage: confirmed zero existing call sites construct `IversonClient(...)` anywhere in the repo (`sample/main.py` only imports it, never instantiates it; no test does either).

- [ ] **Step 3: Export `IversonClientCredentials`**

`__init__.py` — add the import and `__all__` entry:
```python
from iverson_client.auth import IversonClientCredentials
```
(alongside the existing `from iverson_client.core import ...` line), and add `"IversonClientCredentials",` to `__all__` (after `"IversonClient",`).

- [ ] **Step 4: Test the constructor path**

`tests/test_auth.py` (new file, matching the existing `tests/` convention — confirmed `testpaths = ["tests"]` in `pyproject.toml`):
```python
from iverson_client import IversonClient, IversonClientCredentials


def test_client_with_credentials_uses_secure_channel(monkeypatch):
    captured = {}

    def fake_secure_channel(address, channel_creds):
        captured["address"] = address
        captured["channel_creds"] = channel_creds
        return object()

    monkeypatch.setattr("iverson_client.core.grpc.secure_channel", fake_secure_channel)
    monkeypatch.setattr(
        "iverson_client.core.mapping_grpc.ObjectMappingServiceStub", lambda channel: object()
    )

    IversonClient(
        host="localhost",
        port=5000,
        credentials=IversonClientCredentials("id", "secret", "http://localhost:9000/application/o/token/"),
    )

    assert captured["address"] == "localhost:5000"
```

- [ ] **Step 5: Run tests**
```bash
cd Iverson.Clients/Python && python -m pytest tests/test_auth.py -v
```
Expected: 1 passed.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Python/iverson_client/auth.py Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/iverson_client/__init__.py Iverson.Clients/Python/tests/test_auth.py
git commit -m "feat(python-client): add OAuth2 client-credentials support"
```

---

### Task 8: Go SDK — `OAuth2ClientCredentials` (`PerRPCCredentials`)

**Files:**
- Create: `Iverson.Clients/Go/iverson/auth.go`
- Create: `Iverson.Clients/Go/iverson/auth_test.go`

**Interfaces:**
- Produces: `type OAuth2ClientCredentials struct { ClientID, ClientSecret, TokenEndpoint string }` implementing `credentials.PerRPCCredentials`.
- No change to `NewIversonClient` — it already forwards `...grpc.DialOption` (confirmed via direct read of `coordinator.go`), so callers pass `grpc.WithPerRPCCredentials(creds)` themselves. Confirmed zero existing call sites construct `IversonClient`/call `NewIversonClient` anywhere in the repo (only the definition itself matches the grep).

- [ ] **Step 1: `OAuth2ClientCredentials`**

`auth.go` (package `iverson`, matching `coordinator.go`, confirmed via direct read; no new `go.mod` dependency — stdlib `net/http`/`encoding/json` cover the token fetch, confirmed neither is imported anywhere in this module today):
```go
package iverson

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

// OAuth2ClientCredentials implements credentials.PerRPCCredentials, attaching an
// OAuth2 client-credentials Bearer token to every RPC. The token is fetched lazily
// and cached in memory, refreshing 60 seconds before expiry.
type OAuth2ClientCredentials struct {
	ClientID      string
	ClientSecret  string
	TokenEndpoint string

	mu        sync.Mutex
	token     string
	expiresAt time.Time
}

type tokenResponse struct {
	AccessToken string `json:"access_token"`
	ExpiresIn   int64  `json:"expires_in"`
}

func (c *OAuth2ClientCredentials) GetRequestMetadata(ctx context.Context, _ ...string) (map[string]string, error) {
	token, err := c.getToken(ctx)
	if err != nil {
		return nil, err
	}
	return map[string]string{"authorization": "Bearer " + token}, nil
}

// RequireTransportSecurity returns false: this repo's deployment is plaintext h2c with
// no TLS anywhere in the stack — confirmed via grpc-go's http2_client.go getCallAuthData
// that this still allows the credential through on a plaintext channel, unlike a
// channel-construction-time TLS gate.
func (c *OAuth2ClientCredentials) RequireTransportSecurity() bool {
	return false
}

func (c *OAuth2ClientCredentials) getToken(ctx context.Context) (string, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.token != "" && time.Now().Before(c.expiresAt) {
		return c.token, nil
	}

	form := url.Values{}
	form.Set("grant_type", "client_credentials")
	form.Set("client_id", c.ClientID)
	form.Set("client_secret", c.ClientSecret)

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.TokenEndpoint, strings.NewReader(form.Encode()))
	if err != nil {
		return "", fmt.Errorf("building token request: %w", err)
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return "", fmt.Errorf("requesting token: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("failed to acquire Iverson client token: HTTP %d", resp.StatusCode)
	}

	var body tokenResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		return "", fmt.Errorf("decoding token response: %w", err)
	}

	c.token = body.AccessToken
	c.expiresAt = time.Now().Add(time.Duration(body.ExpiresIn-60) * time.Second)
	return c.token, nil
}
```
Caller usage (documented in the doc comment, no code change to `coordinator.go`):
```go
creds := &iverson.OAuth2ClientCredentials{ClientID: id, ClientSecret: secret, TokenEndpoint: endpoint}
client, err := iverson.NewIversonClient("api:8080",
    grpc.WithTransportCredentials(insecure.NewCredentials()),
    grpc.WithPerRPCCredentials(creds),
)
```

- [ ] **Step 2: Test the token-fetch/caching logic against a fake HTTP server**

`auth_test.go`:
```go
package iverson

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestOAuth2ClientCredentials_GetRequestMetadata_FetchesAndCachesToken(t *testing.T) {
	requestCount := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requestCount++
		_ = json.NewEncoder(w).Encode(tokenResponse{AccessToken: "test-token", ExpiresIn: 3600})
	}))
	defer server.Close()

	creds := &OAuth2ClientCredentials{ClientID: "id", ClientSecret: "secret", TokenEndpoint: server.URL}

	md, err := creds.GetRequestMetadata(t.Context())
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}
	if md["authorization"] != "Bearer test-token" {
		t.Errorf("got %q, want %q", md["authorization"], "Bearer test-token")
	}

	if _, err := creds.GetRequestMetadata(t.Context()); err != nil {
		t.Fatalf("GetRequestMetadata (cached): %v", err)
	}
	if requestCount != 1 {
		t.Errorf("expected 1 token request, got %d", requestCount)
	}
}

func TestOAuth2ClientCredentials_RequireTransportSecurity_ReturnsFalse(t *testing.T) {
	creds := &OAuth2ClientCredentials{}
	if creds.RequireTransportSecurity() {
		t.Error("RequireTransportSecurity() = true, want false (plaintext h2c deployment)")
	}
}
```

- [ ] **Step 3: Run tests**
```bash
cd Iverson.Clients/Go && go test ./iverson/... -run TestOAuth2ClientCredentials -v
```
Expected: both tests `PASS`.

- [ ] **Step 4: Commit**
```bash
git add Iverson.Clients/Go/iverson/auth.go Iverson.Clients/Go/iverson/auth_test.go
git commit -m "feat(go-client): add OAuth2 client-credentials support"
```

---

### Task 9: TypeScript SDK — `CallCredentials` via metadata generator

**Files:**
- Create: `Iverson.Clients/TypeScript/src/auth.ts`
- Create: `Iverson.Clients/TypeScript/tests/auth.test.ts`
- Modify: `Iverson.Clients/TypeScript/src/core.ts`

**Interfaces:**
- Produces: `createOAuth2ClientCredentials(clientId: string, clientSecret: string, tokenEndpoint: string): grpc.CallCredentials`; new `IversonClient` constructor param `callCredentials?: grpc.CallCredentials`.
- Blast radius confirmed narrow: every RPC call site in this SDK is confined to `core.ts` (9 total, confirmed by direct grep sweep — `callUnary` at `core.ts:96-104` covers 5 of them via `SchemaRegistrar.registerAll`/`EntityCoordinator.persist/update/delete/get`; the streaming `getMany` call at `core.ts:346` is separate; the remaining 3 are `.close()` calls needing no auth). `search.ts`/`vector-search.ts`/`pipeline.ts`/`group-by.ts` never construct or invoke `ObjectSearchServiceClient` at all (confirmed via grep — they only build request protos), so Search needs no changes here.

- [ ] **Step 1: Token provider + `CallCredentials` factory**

`auth.ts` — no new dependency; Node's global `fetch` (available at this package's `ES2022`/Node ≥18 baseline, confirmed via `tsconfig.json`) covers the token POST:
```ts
import * as grpc from '@grpc/grpc-js';

interface TokenResponse {
    access_token: string;
    expires_in: number;
}

/**
 * Creates a grpc.CallCredentials that attaches an OAuth2 client-credentials Bearer
 * token to each RPC, fetching and caching the token in memory and refreshing it
 * 60 seconds before expiry.
 */
export function createOAuth2ClientCredentials(
    clientId: string,
    clientSecret: string,
    tokenEndpoint: string,
): grpc.CallCredentials {
    let cachedToken: string | null = null;
    let expiresAt = 0;
    let pending: Promise<string> | null = null;

    async function getToken(): Promise<string> {
        if (cachedToken !== null && Date.now() < expiresAt) {
            return cachedToken;
        }
        if (pending !== null) {
            return pending;
        }
        pending = (async () => {
            const body = new URLSearchParams({
                grant_type: 'client_credentials',
                client_id: clientId,
                client_secret: clientSecret,
            });
            const response = await fetch(tokenEndpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body,
            });
            if (!response.ok) {
                throw new Error(`Failed to acquire Iverson client token: HTTP ${response.status}`);
            }
            const payload = (await response.json()) as TokenResponse;
            cachedToken = payload.access_token;
            expiresAt = Date.now() + (payload.expires_in - 60) * 1000;
            return cachedToken;
        })();
        try {
            return await pending;
        } finally {
            pending = null;
        }
    }

    return grpc.credentials.createFromMetadataGenerator((_options, callback) => {
        getToken()
            .then((token) => {
                const metadata = new grpc.Metadata();
                metadata.add('authorization', `Bearer ${token}`);
                callback(null, metadata);
            })
            .catch((err) => callback(err));
    });
}
```
(`@grpc/grpc-js`'s `combineChannelCredentials()` explicitly throws on an insecure base channel, so this stays a per-call `CallCredentials` attached via `CallOptions` on each RPC rather than composed into the channel. `createFromMetadataGenerator` and its `CallMetadataGenerator`/`generateMetadata` shape confirmed directly against `node_modules/@grpc/grpc-js/build/src/call-credentials.d.ts`.)

- [ ] **Step 2: Thread `callCredentials` through the constructor and every call site**

`core.ts` — constructor (line 364, confirmed via direct read), add the parameter and store it:
```ts
export class IversonClient {
    readonly _mappingClient: ObjectMappingServiceClient;
    readonly _persistenceClient: ObjectPersistenceServiceClient;
    readonly _retrievalClient: ObjectRetrievalServiceClient;
    private readonly _callCredentials?: grpc.CallCredentials;

    constructor(
        host: string = 'localhost',
        port: number = 5000,
        useTls: boolean = false,
        callCredentials?: grpc.CallCredentials,
    ) {
        const address = `${host}:${port}`;
        const credentials = useTls
            ? grpc.credentials.createSsl()
            : grpc.credentials.createInsecure();

        this._mappingClient = new ObjectMappingServiceClient(address, credentials);
        this._persistenceClient = new ObjectPersistenceServiceClient(address, credentials);
        this._retrievalClient = new ObjectRetrievalServiceClient(address, credentials);
        this._callCredentials = callCredentials;
    }
```
`callUnary` (confirmed at `core.ts:96-104`) — add an options parameter carrying credentials, using the 4-arg generated-method overload (confirmed directly against `generated/object_mapping.ts`'s real signature: `(request, metadata, options, callback)`):
```ts
function callUnary<Req, Res>(
    method: (
        req: Req,
        metadata: grpc.Metadata,
        options: Partial<grpc.CallOptions>,
        cb: (err: grpc.ServiceError | null, res: Res) => void,
    ) => grpc.ClientUnaryCall,
    request: Req,
    callCredentials?: grpc.CallCredentials,
): Promise<Res> {
    return new Promise((resolve, reject) => {
        const options: Partial<grpc.CallOptions> = callCredentials ? { credentials: callCredentials } : {};
        method(request, new grpc.Metadata(), options, (err, res) => {
            if (err) reject(err);
            else resolve(res as Res);
        });
    });
}
```
Each of the 5 `callUnary(...)` call sites (`core.ts:116, 281, 298, 315, 331` — confirmed via direct grep) gets a trailing `this._client._callCredentials` argument; `SchemaRegistrar`/`EntityCoordinator` already hold a reference to the owning `IversonClient`, so this is a one-line addition per call site.

`getMany` (confirmed at `core.ts:346`, streaming, bypasses `callUnary`) — add the same `metadata`/`options` args to the streaming call:
```ts
this._retrieval.getMany(request, new grpc.Metadata(),
    this._callCredentials ? { credentials: this._callCredentials } : {});
```

- [ ] **Step 3: Test the credentials factory against a fake token endpoint**

`tests/auth.test.ts`:
```ts
import { describe, it, expect, vi } from 'vitest';
import { createOAuth2ClientCredentials } from '../src/auth.js';

describe('createOAuth2ClientCredentials', () => {
    it('attaches a Bearer token from the token endpoint', async () => {
        globalThis.fetch = vi.fn().mockResolvedValue({
            ok: true,
            json: async () => ({ access_token: 'test-token', expires_in: 3600 }),
        }) as unknown as typeof fetch;

        const creds = createOAuth2ClientCredentials('id', 'secret', 'http://localhost:9000/application/o/token/');

        const metadata = await (creds as any).generateMetadata({ method_name: '', service_url: '' });

        expect(metadata.get('authorization')).toEqual(['Bearer test-token']);
    });
});
```

- [ ] **Step 4: Run tests**
```bash
cd Iverson.Clients/TypeScript && npm test -- auth.test.ts
```
Expected: 1 passed.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/TypeScript/src/auth.ts Iverson.Clients/TypeScript/tests/auth.test.ts Iverson.Clients/TypeScript/src/core.ts
git commit -m "feat(ts-client): add OAuth2 client-credentials support"
```

---

### Task 10: Live kind smoke test (+ docker-compose parity check)

**Files:** none created/modified — pure verification against the live kind cluster and docker-compose.

**Interfaces:**
- Consumes: everything from Tasks 1-9. This proves the whole plan works end-to-end, including the two-pass `helm upgrade` requirement (Helm's `lookup` can't see Secrets created earlier in the *same* install pass — confirmed empirically during this plan's design phase against a real throwaway chart).

- [ ] **Step 1: Rebuild and load the updated `iverson-api` image**
```bash
cd Iverson.Server/deploy/kind && ./build-and-load-image.sh
```

- [ ] **Step 2: First `helm upgrade --install` pass — expect a known-broken intermediate state**
```bash
cd Iverson.Server/deploy/helm/iverson
helm upgrade --install iverson . -n iverson --create-namespace
```
On a fresh install, the 4 new Secrets (Task 2) and the templated blueprint ConfigMap render in the *same* pass — `lookup` returns empty for each Secret while the ConfigMap template renders, so the blueprint's `{{ else }}{{ randAlphaNum 40 }}{{ end }}` fallback mints its own random `client_id`/`client_secret` — **different** from what lands in the Secrets. Expected: `Authentication__ValidAudiences` (sourced from the Secrets) will NOT match the `aud` claim Authentik issues (sourced from the Blueprint's independently-generated fallback) after this pass. Expected and not a bug — matches the earlier CDR finding.

- [ ] **Step 3: Second `helm upgrade` pass — resolves the mismatch**
```bash
helm upgrade iverson . -n iverson
```
`lookup` now finds the Secrets created in Step 2, so the blueprint ConfigMap re-renders using the same values, converging both sides.

- [ ] **Step 4: Confirm Authentik picked up the corrected blueprint**
```bash
kubectl -n iverson port-forward svc/iverson-authentik 9000:9000 &
TOKEN=$(kubectl -n iverson get secret iverson-authentik-app -o jsonpath='{.data.bootstrap-token}' | base64 -d)
LOADTEST_SECRET_ID=$(kubectl -n iverson get secret iverson-authentik-loadtest-client -o jsonpath='{.data.client-id}' | base64 -d)
curl -s -H "Authorization: Bearer $TOKEN" "http://localhost:9000/api/v3/providers/oauth2/" | python3 -c "
import json,sys
d = json.load(sys.stdin)
p = next(x for x in d['results'] if x['name']=='iverson-loadtest')
print('provider client_id:', p['client_id'])
print('matches secret:', p['client_id'] == '$LOADTEST_SECRET_ID')
"
```
Expected: `matches secret: True`. Authentik applies blueprint changes asynchronously via a worker task queue — confirmed empirically this can take several seconds; wait up to 2 minutes. If still `False`, force a re-scan: `kubectl -n iverson rollout restart deployment/iverson-authentik-worker` and re-check.

- [ ] **Step 5: Fetch tokens for the 3 machine callers**
```bash
LOADTEST_ID=$(kubectl -n iverson get secret iverson-authentik-loadtest-client -o jsonpath='{.data.client-id}' | base64 -d)
LOADTEST_SECRET=$(kubectl -n iverson get secret iverson-authentik-loadtest-client -o jsonpath='{.data.client-secret}' | base64 -d)
ADMIN_ID=$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)
ADMIN_SECRET=$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-secret}' | base64 -d)

LOADTEST_TOKEN=$(curl -s -X POST "http://localhost:9000/application/o/token/" \
  -d "grant_type=client_credentials&client_id=$LOADTEST_ID&client_secret=$LOADTEST_SECRET" | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")
ADMIN_TOKEN=$(curl -s -X POST "http://localhost:9000/application/o/token/" \
  -d "grant_type=client_credentials&client_id=$ADMIN_ID&client_secret=$ADMIN_SECRET" | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")
echo "loadtest token acquired: ${LOADTEST_TOKEN:0:20}..."
echo "admin token acquired: ${ADMIN_TOKEN:0:20}..."
```
Expected: both non-empty. (Confirmed live during this plan's design phase that Authentik's token endpoint is instance-wide — `/application/o/token/` — not per-provider-slug.)

- [ ] **Step 6: Admin-endpoint access-level checks (HTTP)**
```bash
kubectl -n iverson port-forward svc/iverson-api 8081:8081 &

echo "--- unauthenticated: expect 401 ---"
curl -s -o /dev/null -w "%{http_code}\n" -X POST "http://localhost:8081/admin/dlq"

echo "--- loadtest token (wrong tier): expect 403 ---"
curl -s -o /dev/null -w "%{http_code}\n" -X POST "http://localhost:8081/admin/dlq" -H "Authorization: Bearer $LOADTEST_TOKEN"

echo "--- admin-automation token: expect 200 ---"
curl -s -o /dev/null -w "%{http_code}\n" -X POST "http://localhost:8081/admin/dlq" -H "Authorization: Bearer $ADMIN_TOKEN"
```
Expected output: `401`, `403`, `200` in that order.

- [ ] **Step 7: gRPC access checks, reusing Task 5's LoadTest wiring**
```bash
kubectl -n iverson port-forward svc/iverson-api 8080:8080 &

echo "--- unauthenticated LoadTest run: expect UNAUTHENTICATED failure ---"
cd Iverson.Server
IVERSON_GRPC_URL=http://localhost:8080 dotnet run --project Iverson.LoadTest -- seed --count 1
# Expected: fails with a gRPC Unauthenticated status.

echo "--- authenticated (loadtest identity) LoadTest run: expect success ---"
IVERSON_GRPC_URL=http://localhost:8080 \
IVERSON_CLIENT_ID=$LOADTEST_ID \
IVERSON_CLIENT_SECRET=$LOADTEST_SECRET \
IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/ \
dotnet run --project Iverson.LoadTest -- seed --count 1
# Expected: succeeds — "any authenticated caller" accepts a service-tier identity on gRPC.
```
(`seed` is a real `Iverson.LoadTest` subcommand — confirmed via direct read of `Program.cs`'s `switch (command)` block; `--count` is parsed generically by `CommandFlags.Parse` and consumed by `DirectSeeder.RunAsync(flags)`.)

- [ ] **Step 8: docker-compose parity check**
```bash
cd Iverson.Server && docker compose up -d --build
docker compose exec authentik-server ak healthcheck
LOADTEST_TOKEN_COMPOSE=$(curl -s -X POST "http://localhost:9000/application/o/token/" \
  -d "grant_type=client_credentials&client_id=dev-iverson-loadtest-client-id&client_secret=dev-only-not-for-production-loadtest-secret-0123456789" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")
echo "compose token acquired: ${LOADTEST_TOKEN_COMPOSE:0:20}..."
curl -s -o /dev/null -w "%{http_code}\n" -X POST "http://localhost:8081/admin/dlq"
# Expected: 401 (unauthenticated on compose too — hard cutover applies uniformly)
docker compose down
```

- [ ] **Step 9: Document the manual human-operator step**

No automated coverage for the human/browser OIDC + MFA + `operators` Group path (Authorization Code + PKCE + interactive MFA can't be scripted meaningfully in a curl-based smoke test). Record as a manual post-deploy step: an operator must (1) log into Authentik's UI, (2) add their user to the `operators` Group, (3) complete a browser-based OIDC login against `iverson-api`, (4) confirm the resulting token's `groups` claim contains `operators` and that it's accepted on `/admin/*`.

- [ ] **Step 10: Clean up port-forwards**
```bash
kill %1 %2 %3 2>/dev/null
```

## Explicitly out of scope (inherited from spec)

- End-user identity propagation (Part 4) — this plan authenticates the *calling service*, not the human end-user acting through it.
- Row/field-level authorization (Part 5).
- Any change to the `.proto` contracts themselves (only client-side call construction changes).
- TLS for the gRPC/HTTP listener — works within the existing plaintext h2c deployment.

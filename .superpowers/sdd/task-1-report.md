# Task 1 Report: RegisterSchema authorization gate + DDL identifier allow-listing

## Summary

Both Critical findings fixed:

1. **Finding #1** — `RegisterSchema` had no authorization check beyond the global
   `RequireAuthenticatedUser()` fallback. Added a new, narrower `SchemaAdmin` authorization
   policy (satisfied by `operators` group membership OR a `schema_admin` scope claim) and
   gated `RegisterSchema` with `[Authorize(Policy = "SchemaAdmin")]`. Granted the new
   `schema_admin` scope to `iverson-loadtest` and (alongside the existing `admin` scope)
   `iverson-admin-automation` in both Authentik blueprint locations.

2. **Finding #2** — Client-controlled `TypeName`/property names flowed unescaped into DDL.
   Added a `ValidateIdentifier` helper in `ObjectMappingGrpcService` that enforces
   `^[A-Za-z][A-Za-z0-9]*$` on `TypeName` and every property name for both the root type and
   all dependents, before `SchemaBuilder.BuildDescriptor` (and therefore before any DDL) is
   reached. Throws `RpcException(InvalidArgument)` on failure.

## Files changed

- `Iverson.Server/Iverson.Api/SchemaAdminAuthorizationPolicy.cs` (new) — mirrors
  `OperatorAuthorizationPolicy`'s two-caller-shape pattern (`operators` group OR
  `schema_admin` scope).
- `Iverson.Server/Iverson.Api/Program.cs` — registers the `SchemaAdmin` policy next to
  `Operator` (lines ~139-145).
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` —
  - `using Microsoft.AspNetCore.Authorization;` and `using System.Text.RegularExpressions;`
    added (no ambiguity with existing usings — confirmed by reading the file's using list
    first).
  - `[Authorize(Policy = "SchemaAdmin")]` on `RegisterSchema`.
  - `ValidateIdentifier(typeDesc.TypeName, "type_name")` and a per-property
    `ValidateIdentifier(property.Name, ...)` call added at the top of the
    `foreach (var typeDesc in ...)` loop, before `SchemaBuilder.BuildDescriptor`.
  - New private `ValidateIdentifier(string name, string context)` helper + compiled
    `IdentifierPattern` regex, placed in a new "Identifier validation" section above the
    existing "SQL helpers" section.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
  — new `schema_admin` scopemapping entry (mirrors the `admin` scopemapping's shape); added
  `property_mappings` (previously absent) to `iverson-loadtest` granting `schema_admin`; added
  a second `property_mappings` entry to `iverson-admin-automation` alongside its existing
  `admin` entry.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
  — same `schema_admin` scopemapping entry; the per-client `{{ range ... }}` loop's existing
  `{{- if eq $name "admin-automation" }}` block now also emits the `schema_admin` mapping
  alongside `admin`, and a new `{{- else if eq $name "loadtest" }}` branch emits
  `property_mappings` with `schema_admin` for `iverson-loadtest` (previously had none).
  `iverson-webtest` untouched (falls through both conditions).
- `Iverson.Server/Iverson.Api.Tests/Helpers/TestJwtFactory.cs` — added an optional
  `extraClaims` parameter to `CreateToken` (backward compatible; all 3 existing call sites in
  `ActingUserInterceptorTests.cs` unaffected) so tests can mint tokens carrying `groups`/`scope`
  claims.
- `Iverson.Server/Iverson.Api.Tests/SchemaAdminAuthorizationPolicyTests.cs` (new) — mirrors
  `OperatorAuthorizationPolicyTests`'s shapes: operators-group case, schema_admin-scope case,
  neither case, plus an explicit "admin scope alone does NOT satisfy this narrower policy" case.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — added 4 tests:
  injection-relevant `TypeName` → `InvalidArgument`, injection-relevant property name →
  `InvalidArgument`, underscore-containing `TypeName` → `InvalidArgument` (allow-list, not just
  anti-injection), and a plain alphanumeric type → still succeeds.
- `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationPipelineTests.cs` (new) —
  full pipeline-level regression test (see below).

## Pipeline-level test (Finding #1's highest-severity coverage)

Per the brief, attempted the real-pipeline test rather than settling for the policy predicate's
unit test alone. `RegisterSchemaAuthorizationPipelineTests` boots the real `Program.cs` via
`AuthTestWebApplicationFactory` and calls `RegisterSchema` over a real `GrpcChannel`, so
`app.UseAuthorization()` middleware genuinely evaluates the `[Authorize(Policy = "SchemaAdmin")]`
attribute added to the RPC (unlike `RegisterSchemaAuthorizationIntegrationTests`, which
constructs `ObjectMappingGrpcService` directly and bypasses ASP.NET Core authorization
entirely — confirmed unaffected, see below).

Since none of Postgres/StarRocks/Qdrant are live in this pipeline-test host (only the startup
hydration calls are no-op'd by `AuthTestWebApplicationFactory`, not arbitrary per-request calls
a successful `RegisterSchema` body would make), I used an empty `SchemaRequest` (no `RootType`)
as the "authorized" probe: `RegisterSchema`'s very first line throws `InvalidArgument` before
touching any store. Reaching `InvalidArgument` (rather than `Unauthenticated`/`PermissionDenied`)
is therefore proof the authorization gate let the call through to the method body — the same
technique `ActingUserInterceptorTests` already uses (proving auth passed via a downstream
business-logic error rather than an auth error).

Five cases covered:
- No token → `Unauthenticated`.
- Authenticated, no `groups`/`scope` claims at all → `PermissionDenied`.
- Authenticated with `admin` scope alone (the existing broader Operator scope) → still
  `PermissionDenied` — proves the narrower policy doesn't accidentally inherit `admin`'s rights.
- Authenticated with `operators` group → `InvalidArgument` (gate passed).
- Authenticated with `schema_admin` scope → `InvalidArgument` (gate passed).

All 5 passed against the real middleware pipeline.

## Commands run and output

```
cd Iverson.Server
dotnet build Iverson.Api/Iverson.Api.csproj
# Build succeeded. 0 Warning(s) 0 Error(s)

dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"
# Passed! - Failed: 0, Passed: 343, Skipped: 0, Total: 343, Duration: 27 s
```

Docker (podman-backed) was available in this sandbox, so I also ran the container-backed
integration test per the brief's "run them too if Docker is available" note:

```
dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~RegisterSchemaAuthorizationIntegrationTests"
# Test Run Successful. Total tests: 1. Passed: 1. Total time: 34.6s
```

This confirms the brief's prediction: `RegisterSchemaAuthorizationIntegrationTests`'s
`ArticleWithAuth`/`Id`/`Title`/`OwnerId` test data is unaffected by the new identifier
validation, and (separately) that this test's direct-construction call pattern is indeed
unaffected by the new `[Authorize]` attribute (attributes are enforced by middleware the direct
construction never runs through).

YAML validation (both blueprint files, using a loader that registers a no-op constructor for
Authentik's custom `!Find` tag so it doesn't fail as an "unknown tag"):

```
python3 validate_yaml.py .../compose-only/service-clients.yaml
# OK: ... parsed successfully, 16 entries
```

For the Helm-templated ConfigMap, rendered it first (`helm template ... --show-only
templates/blueprints-configmap-service-clients.yaml --set global.ingressHost=example.test`),
extracted the `data["service-clients.yaml"]` string via `yaml.safe_load` on the ConfigMap
wrapper, then ran the same `!Find`-tolerant validator against the extracted inner YAML:

```
# OK: rendered inner service-clients.yaml parsed successfully, 16 entries
```

Both files produce structurally identical `entries` lists (16 each) after my edits, matching
the pre-existing 1:1 parity between the two blueprint locations.

## Deviations from the brief

None of substance. Minor implementation choices not fully spelled out in the brief:

- `ValidateIdentifier`'s error message format is `"{context} '{name}' is not a valid
  identifier — it must start with a letter and contain only letters and digits."` rather than
  a verbatim copy of the `owner_field` message shape, since the brief only asked to "mirror
  the existing error-message style," not match it character-for-character.
- Added a 4th test case beyond the brief's two ("rejects an identifier containing an
  injection-relevant character" / "accepts a normal alphanumeric one"): an underscore-only
  case, to prove the allow-list is genuinely an allow-list (rejects underscores even though
  they're not injection-relevant) rather than an incomplete blocklist — this matches the
  brief's own reasoning for why underscores are excluded (`ToSnakeCase` inserts its own).
- The pipeline-level test was written as its own new file
  (`RegisterSchemaAuthorizationPipelineTests.cs`) rather than added to
  `RegisterSchemaAuthorizationIntegrationTests.cs`, since the latter is explicitly
  `[Trait("Category", "Integration")]`/container-backed and this new test is fast/non-integration
  (no containers) — keeping them separate preserves the existing fast-vs-integration test-run
  filtering the brief's `--filter` commands rely on.

## Self-review

- Confirmed no `[AllowAnonymous]` exists anywhere else in the codebase that could conflict
  with `[Authorize]` semantics (`grep -rn AllowAnonymous` only shows the existing
  `MapGet`/`MapPost`/`MapPrometheusScrapingEndpoint` minimal-API calls in `Program.cs`, none on
  `ObjectMappingGrpcService`).
- Confirmed `Iverson.LoadTest`'s `SchemaRegistrar.RegisterAllAsync` path is not broken: it uses
  reflected C# type/property names (`descriptor.EntityName`, `prop.Name`), which are standard
  .NET identifiers and pass the new alphanumeric allow-list; and it runs under the
  `iverson-loadtest` client credential, which now carries `schema_admin` in both blueprint
  files.
- Verified via the rendered Helm output that the `iverson-webtest` provider is untouched (no
  `property_mappings` emitted for it, matching the brief's explicit exclusion) and that
  `iverson-oidc-default`/`iverson-loadtest-human` still only carry the `groups` scope mapping,
  unchanged.
- All 343 non-integration tests pass, plus the 1 container-backed integration test, plus both
  blueprint YAML files validate as well-formed and structurally parallel.

No open questions or blockers.

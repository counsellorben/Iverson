# LoadTest Acting-User Authorization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-15-loadtest-acting-user-authorization-design.md` (commit SHA: `5725cf9`)

**Goal:** `Iverson.LoadTest` natively mints and attaches real Authentik acting-user tokens to every gRPC call in `seed`/`write-path`/`read-path`/`all`, and the three benchmark entities gain real `AuthorizationRules` so those tokens are actually enforced — turning LoadTest into a load test of the row/field authorization system itself.

**Architecture:** Two Authentik identities (owner-restricted, reusing the existing smoke-test user; bypass, a new user in a new group) share the existing `iverson-loadtest-human` OAuth2 client. A native C# TOTP+PKCE+flow-executor client mints tokens from scratch and a caching/refresh wrapper avoids repeating the TOTP dance on every call. `BenchmarkArticle`/`BenchmarkAuthor`/`BenchmarkTag` gain an `OwnerId` field and `AuthorizationRules` (ownership + one field restricted to the bypass role); `write-path`/`read-path` pick one of the two identities per request/call.

**Tech stack:** C# / .NET 10, xUnit + FluentAssertions + NSubstitute (where a test project exists), Authentik OAuth2/OIDC (flow-executor API, PKCE, RFC 6238 TOTP), Helm blueprint YAML.

---

## File Structure

- Modify: `Iverson.Server/Iverson.LoadTest/Entities/{BenchmarkArticle,BenchmarkAuthor,BenchmarkTag}.cs` — new `OwnerId` field
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs` — optional authorization-supplement parameter
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs` — new test for the supplement
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs` — optional `headers` parameter on `PersistAsync`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/` — new test file for the `headers` threading
- Create: `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs` — TOTP+PKCE+flow-executor mint-from-scratch logic
- Create: `Iverson.Server/Iverson.LoadTest/Auth/ActingUserTokenProvider.cs` — caching/refresh wrapper, one instance per identity
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs` — DI wiring, new env vars, schema-registration call
- Modify: `Iverson.Server/Iverson.LoadTest/Seeding/DirectSeeder.cs` — `OwnerId` stamping
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/{WritePathScenario,KindWritePathScenario,WritePathRunner}.cs` — identity selection, header attachment, `OwnerId` setting, field-rejection tracking
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/ReadPathScenario.cs` — identity selection, header attachment, extension to `BenchmarkAuthor`/`BenchmarkTag`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` — new group/user, `access_token_validity`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml` — same, templated for kind
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml` — new lookup-guarded Secret for the bypass user's password

## Inherited from spec

The following were verified by `thorough-brainstorming` (and re-verified across two `critical-design-review` rounds) at spec-write time and are **not** re-verified here:

- Row/field authorization enforcement is wired into all four live gRPC services; a schema with `Authorization == null` is denied on every action (Read/Write/Delete) — deliberate, tested, pre-existing behavior for all three benchmark entities today.
- `RegisterSchema` is a blind replace at every layer; `Authorization` must ship in the same call as the complete `Properties`/`Relations`.
- `SchemaRegistrar.BuildTypeDescriptor` is private; no existing extension point supports layering `Authorization` on top of the attribute-driven build.
- `EntityCoordinator<T>.PersistAsync` and `ReadPathScenario`'s direct client calls both have unused `Metadata? headers` parameters already available on the generated client methods.
- Write-side field rejection throws `RpcException(StatusCode.InvalidArgument, ...)`, cleanly distinguishable from `PermissionDenied` (ownership).
- `RowPermission.Role`/groups-claim matching is case-sensitive — the blueprint group name and the schema's `RowPermission.Role` must match exactly (`"iverson-loadtest-bypass"` used consistently throughout).
- `StructConverter.ToStruct` reflects all public properties automatically — the new `OwnerId` property needs no special wiring to be serialized.
- StarRocks column propagation happens in the same `RegisterSchema` call via a second, StarRocks-specific schema manager (`ALTER TABLE ... ADD COLUMN` diffing) — no separate MV/migration step required.
- Refresh-token issuance requires `offline_access` in the granted scope, and works for `client_type: public` without a secret (confirmed against Authentik's own source).
- Authentik blueprint syntax for group creation and user-to-group assignment is `attrs.groups: [!Find [authentik_core.group, [name, ...]]]` (confirmed against Authentik's own `bootstrap.yaml`).
- `OAuth2Provider.access_token_validity` is a `"hours=N"`-style `TextField`.
- The TOTP secret cache path must map LoadTest's own `--target` value (`"containers"`/`"kind"`) to the Python script's vocabulary (`"compose"`/`"kind"`) — not use it as-is — so the cache converges on the same file the existing `mint_acting_user_token.py --target compose` workflow already produced for the reused `iverson-acting-user-smoke-test` identity.
- .NET's `HttpRequestMessage.Headers.Host` genuinely overrides the wire-level `Host` header sent by `HttpClient` (default `SocketsHttpHandler`) — confirmed empirically in the design session via a compiled probe.
- `ReadPathScenario` must be extended to also query `BenchmarkAuthor`/`BenchmarkTag` (not just `BenchmarkArticle`) so all three entities' authorization rules are read-tested, not just write-tested.

## Verified plan-level assumptions

Newly introduced by this plan (paths, signatures, commands, ordering, consumer impact) and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Server/Iverson.LoadTest/Entities/{BenchmarkArticle,BenchmarkAuthor,BenchmarkTag}.cs` exist at exactly these paths, none has an `OwnerId` member | `Read` of all three files |
| 2 | Function signature | `SchemaRegistrar.RegisterAllAsync(CancellationToken ct = default)` is the only current overload; `BuildTypeDescriptor` is `private static` at `SchemaRegistrar.cs:45` | `Read` of `SchemaRegistrar.cs:19-78` |
| 3 | Consumer impact (Cat 6) | `RegisterAllAsync()` has exactly 3 call sites repo-wide (`Iverson.Client.Sample/Program.cs:18`, `SchemaRegistrarTests.cs` ×9, `Iverson.LoadTest/Program.cs:77`), **all zero-arg** — inserting an optional parameter before the existing `ct` (matching .NET's "CancellationToken last" convention) breaks none of them | `grep -rn "RegisterAllAsync("` across the whole repo |
| 4 | Proto shape | `Iverson.Client.Contracts.AuthorizationRules`/`RowPermission`/`FieldPermission` proto-generated properties are `OwnerField`, `RowPermissions`, `FieldPermissions`, `Role`, `CanReadAll`/`CanWriteAll`/`CanDeleteAll`, `FieldName`, `ReadableRoles`/`WritableRoles` (PascalCase from the `.proto`'s `snake_case`); a working construction example already exists | `Read` of `Iverson.Clients/Common/Proto/object_mapping.proto:69-93`; construction pattern confirmed at `RegisterSchemaAuthorizationIntegrationTests.cs:223-227` (already cited in the source spec) |
| 5 | Test project / convention | `Iverson.Client.Core.Tests` exists, uses xUnit + FluentAssertions + NSubstitute, and `SchemaRegistrarTests.cs` already has a working `Arg.Do<SchemaRequest>` capture pattern for asserting on `RootType` | `Read` of `SchemaRegistrarTests.cs:1-113`; `dotnet build`/`dotnet test Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj` — 18/18 passing baseline |
| 6 | Test project / convention | `TestCoordinatorFactory.Create<T>(persistence: ...)` is the established way to construct an `EntityCoordinator<T>` in tests with one stubbed client and substitutes for the rest | `Read` of `TestCoordinatorFactory.cs` |
| 7 | Test/build command | `Iverson.LoadTest` has **no** test project (`Iverson.LoadTest.Tests` does not exist anywhere in the repo) — verification for Tasks 2-3, 5-8 is `dotnet build` only, not `dotnet test` | `find . -iname "*.Tests.csproj"` — repo-wide list contains no LoadTest entry |
| 8 | Test/build command | `cd Iverson.Clients/DotNet && dotnet build Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj` and `dotnet test Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj --no-build` are working invocations | Ran both this session; build succeeded, 18/18 tests passed |
| 9 | Test/build command | `cd Iverson.Server && dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj` is a working invocation | Ran this session; build succeeded, 0 warnings/errors |
| 10 | Consumer impact (Cat 6) | `EntityCoordinator<T>.PersistAsync` has exactly 6 call sites repo-wide: 4 zero-extra-arg calls in `Iverson.Client.Sample/Program.cs` (unaffected by inserting an optional `headers` param before `ct`) and 3 positional-`ct` calls in `WritePathRunner.cs` (already in this plan's own Task 7 scope, not extra breakage) | `grep -rn "\.PersistAsync("` across the whole repo |
| 11 | Function signature | Every generated gRPC client method (`ObjectPersistenceGrpc.cs:134` `PostAsync`, `ObjectRetrievalGrpc.cs:147` `GetMany`, `ObjectSearchGrpc.cs:194,234` `Search`/`AggregateAsync`) already exposes `(request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)` | Confirmed via the generated `*Grpc.cs` files under `Iverson.Client.Contracts/obj/{Debug,Release}/net10.0/` (already cited in the source spec's own verification) |
| 12 | Ordering | `Program.cs` registers schemas (line 77, `command is "seed" or "write-path" or "read-path" or "all"`) strictly before dispatching to any command's `RunAsync` (the `switch` at line 92) — the new `OwnerId` column exists in both stores before `DirectSeeder`/scenario code ever writes to it | `Read` of `Program.cs:72-107` |
| 13 | Existing behavior / recurrence | `DirectSeeder`'s three `Seed*Async` methods each independently check their own "already ≥95% seeded" skip condition before writing any rows (`SeedAuthorsAsync`, `SeedTagsAsync`, `SeedArticlesAsync`) — resolving the owner-restricted identity's `sub` (a live TOTP mint on first use) must happen *after* each method's own skip check, not eagerly at the top of `RunAsync`, or a `seed` re-run against an already-seeded DB would force an unnecessary TOTP flow | `Read` of `DirectSeeder.cs:21-33,40-56,93-109,141-157` |
| 14 | File path / existing pattern | `Iverson.Clients/DotNet/Iverson.Client.Core/CachedClientCredentialsTokenProvider.cs` is the established double-checked-locking token-cache pattern (`SemaphoreSlim`, cached `_token`/`_expiresAt`, refresh 60s early) — `ActingUserTokenProvider` mirrors this shape, and `AuthentikFlowExecutorClient` mirrors its "own a private `HttpClient`, `IDisposable`" convention, rather than routing through `IHttpClientFactory` | `Read` of `CachedClientCredentialsTokenProvider.cs` in full |
| 15 | Consumer impact / DI | `Program.cs`'s `ServiceCollection` already registers `AddGrpcClient<T>` for the four gRPC clients (line 63, via `AddIversonClient`), which transitively registers `IHttpClientFactory`; the new `AuthentikFlowExecutorClient`/`ActingUserTokenProvider`/`ActingUserIdentities` types are added as additional `AddSingleton` registrations in the same `ServiceCollection` chain — no new DI container or composition root | `Read` of `Program.cs:61-70` and `ServiceCollectionExtensions.cs` |
| 16 | File path | The "kind ConfigMap template" the source spec refers to is `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`, and its companion Secret template is `.../templates/secret-service-clients.yaml`; both already provision `iverson-loadtest-human`/`iverson-acting-user-smoke-test` via the exact lookup-guarded-Secret pattern the new bypass user must follow | `Read` of both files in full |
| 17 | Code-in-plan validity | `authentik_core.group`/`authentik_core.user` blueprint entries follow `blueprints/system/bootstrap.yaml`'s exact `attrs.groups: [!Find [...]]` shape (already an inherited assumption); this plan's Task 2 code blocks use that exact shape for both the compose-only static file and the kind Helm template | Re-confirmed against the same upstream source the design spec cited |
| 18 | Forced-but-resolved config gap | The source spec's §6 states OAuth2 `client_id` "defaults to the fixed `dev-iverson-loadtest-human-client-id`" — true only for the **compose** target. For **kind**, `blueprints-configmap-service-clients.yaml:90` generates a *random* `client_id` (`{{ if $loadtestHuman }}...{{ else }}{{ randAlphaNum 40 }}{{ end }}`), stored in Secret `<release>-authentik-loadtest-human-client` — there is no fixed value to default to. Similarly, `redirect_uri` differs per target (`http://localhost/placeholder-callback` for compose vs. `https://{{ .Values.global.ingressHost }}/placeholder-callback` for kind) and the spec's §6 lists no override for it at all. Resolved as a plan-level mechanical addition (not a design-shape change): two more env vars, `IVERSON_ACTING_USER_CLIENT_ID` and `IVERSON_ACTING_USER_REDIRECT_URI`, following the **exact same compose-fixed-default / kind-requires-override pattern** the spec already established for `IVERSON_ACTING_USER_HOST_HEADER` — see Task 5 | `Read` of `blueprints-configmap-service-clients.yaml:79-101` vs. `blueprints/compose-only/service-clients.yaml:79-101` (kind's random client_id / templated redirect_uri vs. compose's fixed literals) |
| 19 | Recurrence (sibling-set sweep) | All 4 scenario-facing files that need the two-identity split (`WritePathScenario.cs`, `KindWritePathScenario.cs`, `WritePathRunner.cs`, `ReadPathScenario.cs`) currently share the identical DI-constructor-injection pattern (`Microsoft.Extensions.DependencyInjection`, primary constructors, no manual `new`) — confirmed no sibling uses a different construction mechanism that would need special-casing | `Read` of all 4 files in full |
| 20 | Code-in-plan validity | `System.Web.HttpUtility.ParseQueryString` is usable in `Iverson.LoadTest` (plain `Microsoft.NET.Sdk`, no `Microsoft.AspNetCore.App` FrameworkReference, no `System.Web`-related PackageReference) — i.e. it's part of the base shared framework in net10.0, not an ASP.NET-only API requiring extra references | `Iverson.LoadTest.csproj` read in full (no ASP.NET references present); empirically compiled and ran a standalone net10.0 console app (`Microsoft.NET.Sdk`, zero extra references) calling `System.Web.HttpUtility.ParseQueryString(...)` — succeeded |

## Tasks

### Task 1: Schema & entity changes — `OwnerId` field + `SchemaRegistrar` authorization supplement

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Entities/BenchmarkArticle.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Entities/BenchmarkAuthor.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Entities/BenchmarkTag.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`

**Interfaces:**
- Produces: the `OwnerId` property on all 3 entities (consumed by Tasks 6-8); `SchemaRegistrar.RegisterAllAsync`'s new `authorizationByTypeName` parameter (consumed by Task 5)

- [ ] **Step 1: Add `OwnerId` to the three entities**

In `BenchmarkArticle.cs`, add after `PublishedAt` (before the `Author` nav property):
```csharp
public string OwnerId { get; set; } = "";
```

In `BenchmarkAuthor.cs`, add after `Bio`:
```csharp
public string OwnerId { get; set; } = "";
```

In `BenchmarkTag.cs`, add after `Category`:
```csharp
public string OwnerId { get; set; } = "";
```

- [ ] **Step 2: Extend `SchemaRegistrar.RegisterAllAsync` with an optional authorization supplement**

In `SchemaRegistrar.cs`, replace:
```csharp
public async Task RegisterAllAsync(CancellationToken ct = default)
{
    foreach (var descriptor in registry.All)
    {
        var typeDesc = BuildTypeDescriptor(descriptor);
        try
        {
```
with:
```csharp
public async Task RegisterAllAsync(
    IReadOnlyDictionary<string, AuthorizationRules>? authorizationByTypeName = null,
    CancellationToken ct = default)
{
    foreach (var descriptor in registry.All)
    {
        var typeDesc = BuildTypeDescriptor(descriptor);
        if (authorizationByTypeName is not null &&
            authorizationByTypeName.TryGetValue(descriptor.EntityName, out var authorization))
        {
            typeDesc.Authorization = authorization;
        }
        try
        {
```
(the rest of the method body is unchanged — `AuthorizationRules` resolves to `Iverson.Client.Contracts.AuthorizationRules` via the file's existing `using Iverson.Client.Contracts;`).

- [ ] **Step 3: Add a test for the supplement**

Append to `SchemaRegistrarTests.cs`, before the class's closing `}`:
```csharp
[Fact]
public async Task RegisterAllAsync_SetsAuthorization_WhenSupplementProvidesEntry()
{
    SchemaRequest? authorRequest = null;
    _mappingClient
        .RegisterSchemaAsync(
            Arg.Do<SchemaRequest>(r =>
            {
                if (r.RootType?.TypeName == "SchemaTestAuthor") authorRequest = r;
            }),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>())
        .Returns(new AsyncUnaryCall<SchemaResponse>(
            Task.FromResult(new SchemaResponse { Success = true }),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }));

    var rules = new AuthorizationRules
    {
        OwnerField = "OwnerId",
        RowPermissions = { new RowPermission { Role = "test-bypass", CanReadAll = true } },
    };

    await _sut.RegisterAllAsync(
        authorizationByTypeName: new Dictionary<string, AuthorizationRules> { ["SchemaTestAuthor"] = rules });

    authorRequest.Should().NotBeNull();
    authorRequest!.RootType!.Authorization.Should().NotBeNull();
    authorRequest.RootType.Authorization.OwnerField.Should().Be("OwnerId");
    authorRequest.RootType.Authorization.RowPermissions.Single().Role.Should().Be("test-bypass");
}

[Fact]
public async Task RegisterAllAsync_LeavesAuthorizationUnset_WhenSupplementHasNoEntryForType()
{
    SchemaRequest? tagRequest = null;
    _mappingClient
        .RegisterSchemaAsync(
            Arg.Do<SchemaRequest>(r =>
            {
                if (r.RootType?.TypeName == "SchemaTestTag") tagRequest = r;
            }),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>())
        .Returns(new AsyncUnaryCall<SchemaResponse>(
            Task.FromResult(new SchemaResponse { Success = true }),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }));

    await _sut.RegisterAllAsync(
        authorizationByTypeName: new Dictionary<string, AuthorizationRules> { ["SchemaTestAuthor"] = new() });

    tagRequest.Should().NotBeNull();
    tagRequest!.RootType!.Authorization.Should().BeNull();
}
```

- [ ] **Step 4: Build and test**
```bash
cd Iverson.Server
dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
cd ../Iverson.Clients/DotNet
dotnet build Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj
dotnet test Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj --filter "FullyQualifiedName~SchemaRegistrarTests"
git add ../../Iverson.Server/Iverson.LoadTest/Entities/BenchmarkArticle.cs ../../Iverson.Server/Iverson.LoadTest/Entities/BenchmarkAuthor.cs ../../Iverson.Server/Iverson.LoadTest/Entities/BenchmarkTag.cs Iverson.Client.Core/SchemaRegistrar.cs Iverson.Client.Core.Tests/SchemaRegistrarTests.cs
git commit -m "feat(loadtest): add OwnerId field to benchmark entities, authorization supplement to SchemaRegistrar"
```

---

### Task 2: Authentik blueprint changes — bypass identity + `access_token_validity`

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml`

No dependencies on Task 1; can run in parallel.

- [ ] **Step 1: Compose-only static blueprint — new group, new user, `access_token_validity`**

In `blueprints/compose-only/service-clients.yaml`, add `access_token_validity: "hours=2"` to the existing `iverson-loadtest-human` provider's `attrs` block (alongside `grant_types`), then append after the existing `iverson-acting-user-smoke-test` user entry:
```yaml
      - model: authentik_core.group
        identifiers:
          name: iverson-loadtest-bypass
        attrs: {}
      - model: authentik_core.user
        identifiers:
          username: iverson-loadtest-bypass-user
        attrs:
          username: iverson-loadtest-bypass-user
          name: "Iverson LoadTest Bypass"
          email: iverson-loadtest-bypass-user@example.invalid
          password: "dev-only-not-for-production-bypass-password-0123456789"
          is_active: true
          groups:
            - !Find [authentik_core.group, [name, iverson-loadtest-bypass]]
```

- [ ] **Step 2: kind ConfigMap template — same, with lookup-guarded Secret**

In `templates/secret-service-clients.yaml`, append a new Secret block after the existing `{{ .Release.Name }}-authentik-smoke-test-user` block, following the identical pattern:
```yaml
---
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-bypass-user
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existingBypassUser := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-bypass-user" .Release.Name) }}
{{- if $existingBypassUser }}
  password: {{ index $existingBypassUser.data "password" }}
{{- else }}
  password: {{ randAlphaNum 32 | b64enc }}
{{- end }}
```

In `templates/blueprints-configmap-service-clients.yaml`, add a new lookup at the top (alongside the existing 6):
```yaml
{{- $bypassUser := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-bypass-user" .Release.Name) }}
```
Add `access_token_validity: "hours=2"` to the `iverson-loadtest-human` provider's `attrs` block (alongside `grant_types`), then append after the existing `iverson-acting-user-smoke-test` user entry:
```yaml
      - model: authentik_core.group
        identifiers:
          name: iverson-loadtest-bypass
        attrs: {}
      - model: authentik_core.user
        identifiers:
          username: iverson-loadtest-bypass-user
        attrs:
          username: iverson-loadtest-bypass-user
          name: "Iverson LoadTest Bypass"
          email: iverson-loadtest-bypass-user@example.invalid
          password: {{ if $bypassUser }}{{ index $bypassUser.data "password" | b64dec }}{{ else }}{{ randAlphaNum 32 }}{{ end }}
          is_active: true
          groups:
            - !Find [authentik_core.group, [name, iverson-loadtest-bypass]]
```

- [ ] **Step 3: Validate YAML syntax and Helm template rendering**
```bash
cd Iverson.Server/deploy/helm/iverson
helm template . --show-only charts/authentik/templates/blueprints-configmap-service-clients.yaml --show-only charts/authentik/templates/secret-service-clients.yaml > /tmp/authentik-render-check.yaml
python3 -c "import yaml; list(yaml.safe_load_all(open('/tmp/authentik-render-check.yaml')))" && echo "YAML parses cleanly"
git add charts/authentik/blueprints/compose-only/service-clients.yaml charts/authentik/templates/blueprints-configmap-service-clients.yaml charts/authentik/templates/secret-service-clients.yaml
git commit -m "feat(authentik): provision iverson-loadtest-bypass group/user, extend loadtest-human token validity"
```

---

### Task 3: `AuthentikFlowExecutorClient` — native TOTP+PKCE+flow-executor mint logic

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs`

No dependencies on Tasks 1-2; can run in parallel. This is the largest, most novel unit in the plan — the C# equivalent of `deploy/scripts/mint_acting_user_token.py`'s core logic (TOTP RFC 6238, PKCE, the flow-executor JSON stage machine, authorization-code exchange, refresh). Kept as one task since it is one cohesive, independently testable capability (mint a token from scratch, or refresh one).

- [ ] **Step 1: Config record, TOTP, and PKCE helpers**

```csharp
namespace Iverson.LoadTest.Auth;

public sealed record AuthentikIdentityConfig(
    string Username,
    string Password,
    string ClientId,
    string RedirectUri,
    string BaseUrl,
    string? HostHeader,
    string CacheTargetToken); // already-mapped: "compose" or "kind"

public sealed record MintedToken(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

public sealed class AuthentikFlowExecutorClient(
    AuthentikIdentityConfig identity,
    ILogger<AuthentikFlowExecutorClient> logger) : IDisposable
{
    private const string FlowSlug = "default-authentication-flow";
    private const int MaxFlowStages = 20;
    private const int MaxTotpAttempts = 4;

    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new System.Net.CookieContainer(),
    });

    // RFC 6238 TOTP — HMAC-SHA1, 6 digits, 30s period. Mirrors mint_acting_user_token.py's `totp()`.
    private static string Totp(string secretBase32, DateTimeOffset? at = null)
    {
        var padded = secretBase32.ToUpperInvariant().PadRight(
            secretBase32.Length + (8 - secretBase32.Length % 8) % 8, '=');
        var key = Base32Decode(padded);
        var counter = (long)((at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30);
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);

        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24 | (hash[offset + 1] & 0xFF) << 16 |
                    (hash[offset + 2] & 0xFF) << 8 | (hash[offset + 3] & 0xFF)) % 1_000_000;
        return code.ToString("D6");
    }

    // RFC 4648 base32 decode (A-Z2-7 alphabet, 5 bits/char) — mirrors Python's base64.b32decode.
    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var trimmed = s.TrimEnd('=');
        var bits = new List<byte>();
        int buffer = 0, bitsInBuffer = 0;
        foreach (var ch in trimmed)
        {
            var val = alphabet.IndexOf(ch);
            if (val < 0) throw new FormatException($"Invalid base32 character '{ch}'");
            buffer = (buffer << 5) | val;
            bitsInBuffer += 5;
            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                bits.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
            }
        }
        return bits.ToArray();
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifierBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var verifier = Convert.ToBase64String(verifierBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var challengeBytes = sha256.ComputeHash(System.Text.Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
```

`Base32Decode` is a standard RFC 4648 base32 decoder (5 bits/char, `A-Z2-7` alphabet) — no external package; implement directly (mirrors Python's `base64.b32decode`).

- [ ] **Step 2: TOTP secret cache (read/write, 0600, target-vocabulary-mapped path)**

```csharp
    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "iverson");

    private string CachePath =>
        Path.Combine(CacheDir, $"acting-user-totp-secret-{identity.CacheTargetToken}-{identity.Username}.txt");

    private string? LoadCachedTotpSecret() =>
        File.Exists(CachePath) ? File.ReadAllText(CachePath).Trim() is { Length: > 0 } s ? s : null : null;

    private void SaveCachedTotpSecret(string secret)
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(CachePath, secret + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(CachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        logger.LogInformation("Cached new TOTP secret for future runs at {Path}", CachePath);
    }
```

`identity.CacheTargetToken` arrives already mapped ("compose"/"kind") — the `"containers"→"compose"` translation happens where `AuthentikIdentityConfig` is constructed (Task 5), not inside this client, keeping this class ignorant of LoadTest's own `--target` vocabulary.

- [ ] **Step 3: HTTP request helper with forced `Host` header**

```csharp
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        if (identity.HostHeader is { Length: > 0 } h) req.Headers.Host = h;
        return await _http.SendAsync(req);
    }
```

- [ ] **Step 4: Flow-executor stage driver**

Drives `GET {BaseUrl}/api/v3/flows/executor/{FlowSlug}/` repeatedly, branching on the JSON response's `component` field, until `component == "xak-flow-redirect"` (session authenticated) or `MaxFlowStages` is exceeded (throw). Mirrors `mint_acting_user_token.py`'s `drive_authentication_flow`/`submit_totp_code` exactly. `System.Web.HttpUtility.ParseQueryString` is available in this project without any extra package/FrameworkReference (verified empirically at plan-write time — see Verified plan-level assumptions).

```csharp
    private async Task DriveAuthenticationFlowAsync(string flowUrl)
    {
        string? cachedSecret = null;
        var totpState = new TotpAttemptState();

        for (var i = 0; i < MaxFlowStages; i++)
        {
            var getResp = await SendAsync(HttpMethod.Get, flowUrl);
            var challengeJson = await getResp.Content.ReadAsStringAsync();
            using var challenge = System.Text.Json.JsonDocument.Parse(challengeJson);
            var root = challenge.RootElement;
            var component = root.TryGetProperty("component", out var c) ? c.GetString() : null;
            logger.LogDebug("flow stage: {Component}", component);

            switch (component)
            {
                case "xak-flow-redirect":
                    return;

                case "ak-stage-identification":
                    await SendAsync(HttpMethod.Post, flowUrl, JsonBody(new { uid_field = identity.Username }));
                    continue;

                case "ak-stage-password":
                    await SendAsync(HttpMethod.Post, flowUrl, JsonBody(new { password = identity.Password }));
                    continue;

                case "ak-stage-authenticator-validate":
                {
                    var hasDeviceChallenges = root.TryGetProperty("device_challenges", out var dc) &&
                        dc.ValueKind == System.Text.Json.JsonValueKind.Array && dc.GetArrayLength() > 0;
                    if (hasDeviceChallenges)
                    {
                        cachedSecret ??= LoadCachedTotpSecret()
                            ?? throw new InvalidOperationException(
                                $"This user already has an enrolled TOTP device on the server, but no locally " +
                                $"cached secret exists at {CachePath}. Authentik never re-exposes an enrolled " +
                                "device's secret — restore the cached secret file, or reset the user's TOTP " +
                                "device in Authentik to force re-enrollment.");
                        await SubmitTotpCodeAsync(flowUrl, cachedSecret, totpState);
                        continue;
                    }

                    System.Text.Json.JsonElement totpStage = default;
                    if (root.TryGetProperty("configuration_stages", out var stages))
                    {
                        foreach (var s in stages.EnumerateArray())
                        {
                            if (s.TryGetProperty("meta_model_name", out var m) &&
                                (m.GetString() ?? "").EndsWith("authenticatortotpstage"))
                            {
                                totpStage = s;
                                break;
                            }
                        }
                    }
                    if (totpStage.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                        throw new InvalidOperationException(
                            "No TOTP configuration stage is offered by the authenticator-validate stage.");
                    var pk = totpStage.GetProperty("pk");
                    await SendAsync(HttpMethod.Post, flowUrl, JsonBody(new { selected_stage = pk.ToString() }));
                    continue;
                }

                case "ak-stage-authenticator-totp":
                {
                    var configUrl = root.GetProperty("config_url").GetString()!;
                    var secret = ParseTotpSecretFromConfigUrl(configUrl);
                    SaveCachedTotpSecret(secret);
                    cachedSecret = secret;
                    await SubmitTotpCodeAsync(flowUrl, secret, totpState);
                    continue;
                }

                default:
                    throw new InvalidOperationException($"Unhandled flow-executor component '{component}': {challengeJson}");
            }
        }

        throw new InvalidOperationException($"Authentication flow did not complete after {MaxFlowStages} stages");
    }

    // Tracks TOTP replay-window state across DriveAuthenticationFlowAsync's loop. A plain class
    // (not `ref` locals) because async methods cannot have `ref`/`out`/`in` parameters in C#
    // (CS1988) — mirrors mint_acting_user_token.py's own TotpAttemptState class.
    private sealed class TotpAttemptState
    {
        public int? LastCounter;
        public int Attempts;
    }

    // Authentik marks a TOTP code "used" on any submission attempt within its 30s window, success
    // or failure — so submitting twice in the same window always fails the second time. Wait out
    // the rest of the window rather than misreport that as a real validation failure.
    private async Task SubmitTotpCodeAsync(string flowUrl, string secret, TotpAttemptState state)
    {
        if (state.Attempts >= MaxTotpAttempts)
            throw new InvalidOperationException(
                $"TOTP code was rejected {MaxTotpAttempts} times in a row; giving up. If this isn't just " +
                "window-reuse, the cached secret is likely stale.");

        var now = DateTimeOffset.UtcNow;
        var counter = (int)(now.ToUnixTimeSeconds() / 30);
        if (state.LastCounter == counter)
        {
            var waitMs = (int)((counter + 1) * 30 - now.ToUnixTimeSeconds()) * 1000 + 500;
            logger.LogInformation("Waiting {Ms}ms for a fresh TOTP time-step...", waitMs);
            await Task.Delay(waitMs);
        }
        var code = Totp(secret);
        state.LastCounter = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        state.Attempts++;
        await SendAsync(HttpMethod.Post, flowUrl, JsonBody(new { code }));
    }

    private static StringContent JsonBody(object payload) =>
        new(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

    private static string ParseTotpSecretFromConfigUrl(string configUrl)
    {
        var uri = new Uri(configUrl);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return query["secret"] ?? throw new InvalidOperationException("config_url has no 'secret' param");
    }
```

- [ ] **Step 5: PKCE authorize + token exchange + refresh**

```csharp
    public async Task<MintedToken> MintAsync(CancellationToken ct = default)
    {
        var flowUrl = $"{identity.BaseUrl}/api/v3/flows/executor/{FlowSlug}/";
        await DriveAuthenticationFlowAsync(flowUrl);

        var (verifier, challenge) = GeneratePkce();
        var state = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var query = $"client_id={Uri.EscapeDataString(identity.ClientId)}" +
                    $"&redirect_uri={Uri.EscapeDataString(identity.RedirectUri)}" +
                    "&response_type=code&scope=openid%20groups%20offline_access" +
                    $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";
        var authorizeResp = await SendAsync(HttpMethod.Get, $"{identity.BaseUrl}/application/o/authorize/?{query}");
        if (authorizeResp.StatusCode != System.Net.HttpStatusCode.Redirect &&
            (int)authorizeResp.StatusCode != 302)
            throw new InvalidOperationException($"Expected a 302 from /application/o/authorize/, got {authorizeResp.StatusCode}");
        var location = authorizeResp.Headers.Location
            ?? throw new InvalidOperationException("302 from /application/o/authorize/ had no Location header");
        var code = System.Web.HttpUtility.ParseQueryString(location.Query)["code"]
            ?? throw new InvalidOperationException($"302 Location had no 'code': {location}");

        var tokenResp = await SendAsync(HttpMethod.Post, $"{identity.BaseUrl}/application/o/token/",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = identity.RedirectUri,
                ["client_id"] = identity.ClientId,
                ["code_verifier"] = verifier,
            }));
        return await ParseTokenResponseAsync(tokenResp);
    }

    public async Task<MintedToken> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var resp = await SendAsync(HttpMethod.Post, $"{identity.BaseUrl}/application/o/token/",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = identity.ClientId,
            }));
        return await ParseTokenResponseAsync(resp);
    }

    private static async Task<MintedToken> ParseTokenResponseAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request failed ({(int)resp.StatusCode}): {body}");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new MintedToken(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            root.GetProperty("expires_in").GetInt32());
    }

    public void Dispose() => _http.Dispose();
```

Per the inherited assumption, `client_type: public` means neither `MintAsync` nor `RefreshAsync` sends a `client_secret` — matches the provider config.

- [ ] **Step 6: Build**
```bash
cd Iverson.Server
dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
git add Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs
git commit -m "feat(loadtest): add native Authentik flow-executor client (TOTP+PKCE+token exchange)"
```

---

### Task 4: `ActingUserTokenProvider` — caching/refresh wrapper

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Auth/ActingUserTokenProvider.cs`

**Interfaces:**
- Consumes: `AuthentikFlowExecutorClient` (Task 3)
- Produces: `ActingUserTokenProvider.GetTokenAsync`/`GetSubAsync` (consumed by Tasks 6-8)

- [ ] **Step 1: Implement, mirroring `CachedClientCredentialsTokenProvider`'s double-checked-locking shape**

```csharp
namespace Iverson.LoadTest.Auth;

public sealed class ActingUserTokenProvider(AuthentikFlowExecutorClient flowClient) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _accessToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _accessToken;

            MintedToken minted;
            if (_refreshToken is not null)
            {
                try
                {
                    minted = await flowClient.RefreshAsync(_refreshToken, ct);
                }
                catch (Exception)
                {
                    // Refresh token rejected/expired — fall back to the full TOTP flow.
                    minted = await flowClient.MintAsync(ct);
                }
            }
            else
            {
                minted = await flowClient.MintAsync(ct);
            }

            _accessToken = minted.AccessToken;
            _refreshToken = minted.RefreshToken ?? _refreshToken;
            // Refresh 60s early so no call ever races a token that expires mid-flight.
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(minted.ExpiresInSeconds - 60);
            return _accessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetSubAsync(CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct);
        var payload = token.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
            .Replace('-', '+').Replace('_', '/');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("sub").GetString()!;
    }

    public void Dispose() => flowClient.Dispose();
}

public sealed record ActingUserIdentities(ActingUserTokenProvider Regular, ActingUserTokenProvider Bypass)
{
    // Used identically at every per-request/per-call site in WritePathRunner and ReadPathScenario —
    // centralized here rather than repeating the ternary at all 5 call sites.
    public ActingUserTokenProvider PickRandom() => Random.Shared.Next(2) == 0 ? Regular : Bypass;
}
```

- [ ] **Step 2: Build**
```bash
cd Iverson.Server
dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
git add Iverson.LoadTest/Auth/ActingUserTokenProvider.cs
git commit -m "feat(loadtest): add ActingUserTokenProvider caching/refresh wrapper"
```

---

### Task 5: `Program.cs` DI wiring — env vars, provider registration, schema-registration call

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs`

**Interfaces:**
- Consumes: Task 1's `SchemaRegistrar` supplement parameter; Task 3/4's `AuthentikFlowExecutorClient`/`ActingUserTokenProvider`/`ActingUserIdentities`
- Produces: `ActingUserIdentities` registered in DI (consumed by Tasks 6-8)

- [ ] **Step 1: New env vars**

After the existing `actingUserToken` read (line 30), add:
```csharp
var actingUserHostHeader = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_HOST_HEADER") ?? "authentik-server:9000";
// Compose-fixed defaults; kind requires an explicit override (client_id and redirect_uri are
// randomly-generated/ingress-derived per Helm release for kind — see the blueprint templates).
var actingUserClientId    = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_CLIENT_ID")    ?? "dev-iverson-loadtest-human-client-id";
var actingUserRedirectUri = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_REDIRECT_URI") ?? "http://localhost/placeholder-callback";
var actingUserUsername = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_USERNAME") ?? "iverson-acting-user-smoke-test";
var actingUserPassword = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_PASSWORD") ?? "dev-only-not-for-production-smoke-test-password-0123456789";
var actingUserBypassUsername = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_BYPASS_USERNAME") ?? "iverson-loadtest-bypass-user";
var actingUserBypassPassword = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_BYPASS_PASSWORD") ?? "dev-only-not-for-production-bypass-password-0123456789";
var actingUserCacheTarget = flags.Target == "kind" ? "kind" : "compose"; // maps LoadTest's own "containers"/"kind" to the Python script's "compose"/"kind" cache-path vocabulary
var actingUserBaseUrl = tokenEndpoint is not null
    ? tokenEndpoint[..tokenEndpoint.IndexOf("/application/o/token/", StringComparison.Ordinal)]
    : "http://localhost:9000";
```

- [ ] **Step 2: Register the Authentik auth stack in the `ServiceCollection`**

In the `services` builder chain (after `.AddSingleton(kafkaOptions)`, before `.AddSingleton<DirectSeeder>()`):
```csharp
    .AddSingleton(sp => new ActingUserIdentities(
        new ActingUserTokenProvider(new AuthentikFlowExecutorClient(
            new AuthentikIdentityConfig(
                actingUserUsername, actingUserPassword, actingUserClientId, actingUserRedirectUri,
                actingUserBaseUrl, actingUserHostHeader, actingUserCacheTarget),
            sp.GetRequiredService<ILogger<AuthentikFlowExecutorClient>>())),
        new ActingUserTokenProvider(new AuthentikFlowExecutorClient(
            new AuthentikIdentityConfig(
                actingUserBypassUsername, actingUserBypassPassword, actingUserClientId, actingUserRedirectUri,
                actingUserBaseUrl, actingUserHostHeader, actingUserCacheTarget),
            sp.GetRequiredService<ILogger<AuthentikFlowExecutorClient>>()))))
```
This is registered unconditionally alongside the other singletons (cheap — `AddSingleton` only registers a descriptor; the two `AuthentikFlowExecutorClient`/`ActingUserTokenProvider` instances are constructed eagerly inside the factory the *first* time `ActingUserIdentities` is resolved, but no network call happens until a consumer calls `GetTokenAsync`/`GetSubAsync` — so `reset-starrocks`/`acting-user-smoke-test`/`--help`, which never resolve `ActingUserIdentities`, never touch Authentik at all).

Add `using Iverson.LoadTest.Auth;` to the top of the file.

- [ ] **Step 3: Wire the authorization supplement into the schema-registration call**

Replace:
```csharp
await services.GetRequiredService<SchemaRegistrar>().RegisterAllAsync();
```
with:
```csharp
var authorizationByTypeName = new Dictionary<string, AuthorizationRules>
{
    ["BenchmarkArticle"] = BuildAuthorizationRules("Body"),
    ["BenchmarkAuthor"]  = BuildAuthorizationRules("Email"),
    ["BenchmarkTag"]     = BuildAuthorizationRules("Category"),
};
await services.GetRequiredService<SchemaRegistrar>().RegisterAllAsync(authorizationByTypeName);
```
and add, near the other local functions at the bottom of the file:
```csharp
static AuthorizationRules BuildAuthorizationRules(string restrictedField) => new()
{
    OwnerField = "OwnerId",
    RowPermissions =
    {
        new RowPermission { Role = "iverson-loadtest-bypass", CanReadAll = true, CanWriteAll = true, CanDeleteAll = true },
    },
    FieldPermissions =
    {
        new FieldPermission
        {
            FieldName = restrictedField,
            ReadableRoles = { "iverson-loadtest-bypass" },
            WritableRoles = { "iverson-loadtest-bypass" },
        },
    },
};
```
(`RowPermission`/`FieldPermission`/`AuthorizationRules` resolve via the file's existing `using Iverson.Client.Contracts;`.)

- [ ] **Step 4: Build**
```bash
cd Iverson.Server
dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
git add Iverson.LoadTest/Program.cs
git commit -m "feat(loadtest): wire acting-user token providers into DI, register AuthorizationRules on benchmark entities"
```

---

### Task 6: `DirectSeeder` — `OwnerId` stamping

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Seeding/DirectSeeder.cs`

**Interfaces:**
- Consumes: `ActingUserIdentities` (Task 5, DI-injected)

- [ ] **Step 1: Take `ActingUserIdentities` as a constructor dependency**

Change:
```csharp
public sealed class DirectSeeder(LoadTestConfig config)
```
to:
```csharp
public sealed class DirectSeeder(LoadTestConfig config, ActingUserIdentities identities)
```
Add `using Iverson.LoadTest.Auth;` to the top of the file.

- [ ] **Step 2: Stamp `OwnerId` in each `Seed*Async` method — lazily, only if that method is actually about to write rows**

In each of `SeedAuthorsAsync`, `SeedTagsAsync`, `SeedArticlesAsync`, immediately *after* the existing "already seeded, skip" early-return (so a `seed` re-run against fully-seeded data never triggers a TOTP mint) and *before* the `COPY`/`Guid[] ids` loop begins, add:
```csharp
var ownerSub = await identities.Regular.GetSubAsync(ct);
```
Then, inside each per-row loop, compute:
```csharp
var ownerId = i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString();
```
and:
- Add `"OwnerId"` to each Postgres `COPY` column list (`benchmark_authors`, `benchmark_tags`, `benchmark_articles`) and add the corresponding `await writer.WriteAsync(ownerId, NpgsqlDbType.Text, ct);` in each row-write block.
- Add `"OwnerId"` to each StarRocks `SrBatchInsertAsync` column array and append `ownerId` (or, for Articles, recompute it consistently — see note below) to each row's `object[]`.

For `SeedArticlesAsync` specifically: the Postgres loop and the StarRocks `ids.Select((id, i) => ...)` projection are two separate passes over the same `i` range — compute `ownerId` the same way (`i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString()`) independently in both; a StarRocks row's `OwnerId` matching or not matching its Postgres counterpart exactly does not matter for this design (the ~1% ownership signal only needs to be present in whichever store a given read path actually queries — `GetMany`/`Get` reads Postgres, `Search`/`Aggregate` reads StarRocks), but both must still land in the *same 1-in-100* stride so each store's own ~1% is meaningful; use the identical `i % 100 == 0` condition (not independently-random selection) in both passes so the two stores agree on *which* rows are owned, even though the non-owned rows' exact filler string may differ between stores.

- [ ] **Step 3: Build**
```bash
cd Iverson.Server
dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
git add Iverson.LoadTest/Seeding/DirectSeeder.cs
git commit -m "feat(loadtest): stamp ~1%% of seeded rows with the acting-user's OwnerId"
```

---

### Task 7: `WritePathRunner` + `EntityCoordinator.PersistAsync` headers param

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorPersistAsyncTests.cs` (new file)
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/WritePathScenario.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/KindWritePathScenario.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/WritePathRunner.cs`

**Interfaces:**
- Consumes: `ActingUserIdentities` (Task 5, DI-injected); `OwnerId` (Task 1)

- [ ] **Step 1: `EntityCoordinator<T>.PersistAsync` gains an optional `headers` parameter**

Change:
```csharp
    public async Task<string?> PersistAsync(T entity, CancellationToken ct = default)
    {
        logger.LogDebug("ObjectPersistence.Post {Entity}", _descriptor.EntityName);
        var response = await persistence.PostAsync(
            new PersistRequest
            {
                TypeName = _descriptor.EntityName,
                Payload  = StructConverter.ToStruct(entity),
                TraceId  = CurrentTraceId()
            },
            cancellationToken: ct);
```
to:
```csharp
    public async Task<string?> PersistAsync(T entity, Grpc.Core.Metadata? headers = null, CancellationToken ct = default)
    {
        logger.LogDebug("ObjectPersistence.Post {Entity}", _descriptor.EntityName);
        var response = await persistence.PostAsync(
            new PersistRequest
            {
                TypeName = _descriptor.EntityName,
                Payload  = StructConverter.ToStruct(entity),
                TraceId  = CurrentTraceId()
            },
            headers, cancellationToken: ct);
```
(`Grpc.Core` is already imported at the top of the file as `using Grpc.Core;`, so `Metadata` can be used unqualified — write it unqualified to match the file's existing style.)

- [ ] **Step 2: Test the new parameter**

Create `EntityCoordinatorPersistAsyncTests.cs`:
```csharp
using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

[IversonEntity]
internal sealed class PersistAsyncTestEntity
{
    [IversonKey] public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class EntityCoordinatorPersistAsyncTests
{
    [Fact]
    public async Task PersistAsync_PassesSuppliedHeaders_ToPostAsync()
    {
        var persistence = Substitute.For<ObjectPersistenceService.ObjectPersistenceServiceClient>();
        Metadata? capturedHeaders = null;
        persistence
            .PostAsync(
                Arg.Any<PersistRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<PersistResponse>(
                Task.FromResult(new PersistResponse { Success = true, Key = "k" }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        var sut = TestCoordinatorFactory.Create<PersistAsyncTestEntity>(persistence: persistence);
        var headers = new Metadata { { "x-acting-user-authorization", "Bearer test-token" } };

        await sut.PersistAsync(new PersistAsyncTestEntity { Id = Guid.NewGuid(), Name = "x" }, headers);

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-acting-user-authorization")!.Value.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task PersistAsync_WithNoHeaders_PassesNull()
    {
        var persistence = Substitute.For<ObjectPersistenceService.ObjectPersistenceServiceClient>();
        Metadata? capturedHeaders = null;
        persistence
            .PostAsync(
                Arg.Any<PersistRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<PersistResponse>(
                Task.FromResult(new PersistResponse { Success = true, Key = "k" }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        var sut = TestCoordinatorFactory.Create<PersistAsyncTestEntity>(persistence: persistence);

        await sut.PersistAsync(new PersistAsyncTestEntity { Id = Guid.NewGuid(), Name = "x" });

        capturedHeaders.Should().BeNull();
    }
}
```

- [ ] **Step 3: `WritePathScenario`/`KindWritePathScenario` take `ActingUserIdentities` and thread it through**

In `WritePathScenario.cs`, add `ActingUserIdentities identities` to the primary constructor (after `EntityCoordinator<BenchmarkTag> tags`, before `ILogger`), and change the `RunAsync` call to:
```csharp
    public Task RunAsync(CommandFlags flags, CancellationToken ct = default) =>
        WritePathRunner.RunAsync(config, articles, authors, tags, identities, logger, flags, applyKafkaSecurity: null, ct);
```
Apply the identical change to `KindWritePathScenario.cs` (add the constructor param, pass `identities` into `WritePathRunner.RunAsync` in the same position). Add `using Iverson.LoadTest.Auth;` to both files.

- [ ] **Step 4: `WritePathRunner` — identity selection, header attachment, `OwnerId`, field-rejection tracking**

Add `ActingUserIdentities identities` to `RunAsync`'s parameter list — after the `EntityCoordinator<BenchmarkTag> tags` parameter, before `ILogger logger` — matching the same position used in `WritePathScenario`/`KindWritePathScenario`'s constructors (Step 3). Add `using Iverson.LoadTest.Auth;` / `using Grpc.Core;` (the latter likely already present via `Iverson.Client.Core`) to the top of the file.

Replace the per-iteration body:
```csharp
                try
                {
                    var t0 = BenchmarkReport.NowMicros();
                    string key;

                    switch (flags.Type)
                    {
                        case "Author":
                            var u = new BenchmarkAuthor
                            {
                                Id    = Guid.NewGuid(),
                                Name  = $"WPAuthor {seed}",
                                Email = $"wpauthor{seed}@benchmark.dev",
                                Bio   = new string('x', 200),
                            };
                            await authors.PersistAsync(u, ct);
                            key = u.Id.ToString();
                            break;

                        case "Tag":
                            var tg = new BenchmarkTag
                            {
                                Id       = Guid.NewGuid(),
                                Name     = $"wptag-{seed}",
                                Category = Categories[seed % Categories.Length],
                            };
                            await tags.PersistAsync(tg, ct);
                            key = tg.Id.ToString();
                            break;

                        default: // Article
                            var a = new BenchmarkArticle
                            {
                                Id                = Guid.NewGuid(),
                                Title             = $"WP Article {seed}",
                                Body              = GenerateBody(seed),
                                BenchmarkAuthorId = authorIds.Length > 0
                                    ? authorIds[seed % authorIds.Length]
                                    : Guid.NewGuid(),
                                Category          = Categories[seed % Categories.Length],
                                WordCount         = seed % 1000,
                                PublishedAt       = DateTimeOffset.UtcNow,
                            };
                            await articles.PersistAsync(a, ct);
                            key = a.Id.ToString();
                            break;
                    }

                    report.Record(BenchmarkReport.NowMicros() - t0);
                    postedKeys.Add(key);
                }
                catch (RpcException ex)
                {
                    report.RecordError();
                    logger.LogDebug(ex, "Post failed at seed={Seed}", seed);
                }
```
with:
```csharp
                var identity = identities.PickRandom();
                try
                {
                    var t0 = BenchmarkReport.NowMicros();
                    var headers = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));
                    // The server force-sets OwnerId for the owner-restricted identity on create; the
                    // bypass identity's writes are never ownership-checked, so it must set its own OwnerId.
                    var ownerId = identity == identities.Bypass ? await identity.GetSubAsync(ct) : "";
                    string key;

                    switch (flags.Type)
                    {
                        case "Author":
                            var u = new BenchmarkAuthor
                            {
                                Id      = Guid.NewGuid(),
                                Name    = $"WPAuthor {seed}",
                                Email   = $"wpauthor{seed}@benchmark.dev",
                                Bio     = new string('x', 200),
                                OwnerId = ownerId,
                            };
                            await authors.PersistAsync(u, headers, ct);
                            key = u.Id.ToString();
                            break;

                        case "Tag":
                            var tg = new BenchmarkTag
                            {
                                Id       = Guid.NewGuid(),
                                Name     = $"wptag-{seed}",
                                Category = Categories[seed % Categories.Length],
                                OwnerId  = ownerId,
                            };
                            await tags.PersistAsync(tg, headers, ct);
                            key = tg.Id.ToString();
                            break;

                        default: // Article
                            var a = new BenchmarkArticle
                            {
                                Id                = Guid.NewGuid(),
                                Title             = $"WP Article {seed}",
                                Body              = GenerateBody(seed),
                                BenchmarkAuthorId = authorIds.Length > 0
                                    ? authorIds[seed % authorIds.Length]
                                    : Guid.NewGuid(),
                                Category          = Categories[seed % Categories.Length],
                                WordCount         = seed % 1000,
                                PublishedAt       = DateTimeOffset.UtcNow,
                                OwnerId           = ownerId,
                            };
                            await articles.PersistAsync(a, headers, ct);
                            key = a.Id.ToString();
                            break;
                    }

                    report.Record(BenchmarkReport.NowMicros() - t0);
                    postedKeys.Add(key);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
                {
                    // Expected: the owner-restricted identity's write always includes the field
                    // restricted to the bypass role, which the server rejects — tracked separately
                    // from genuine failures, not mixed into `report`'s error count.
                    Interlocked.Increment(ref fieldRejections);
                }
                catch (RpcException ex)
                {
                    report.RecordError();
                    logger.LogDebug(ex, "Post failed at seed={Seed}", seed);
                }
```
Declare `long fieldRejections = 0;` alongside the existing `var report = new BenchmarkReport();` (line 55), and print it after the existing "Post wave complete" line:
```csharp
        Console.WriteLine($"[write-path] Post wave complete — {flags.Count:N0} records in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"[write-path] Expected field-rejections (owner-restricted identity writing a bypass-only field): {Interlocked.Read(ref fieldRejections):N0}");
```

- [ ] **Step 5: Build and test**
```bash
cd Iverson.Clients/DotNet
dotnet build Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj
dotnet test Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj --filter "FullyQualifiedName~EntityCoordinatorPersistAsyncTests"
cd ../../Iverson.Server
dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
git add ../Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs ../Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorPersistAsyncTests.cs Iverson.LoadTest/Scenarios/WritePathScenario.cs Iverson.LoadTest/Scenarios/KindWritePathScenario.cs Iverson.LoadTest/Scenarios/WritePathRunner.cs
git commit -m "feat(loadtest): attach acting-user identity per write-path request, track expected field-rejections separately"
```

---

### Task 8: `ReadPathScenario` — identity selection, header attachment, `BenchmarkAuthor`/`BenchmarkTag` extension

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/ReadPathScenario.cs`

**Interfaces:**
- Consumes: `ActingUserIdentities` (Task 5, DI-injected)

- [ ] **Step 1: Take `ActingUserIdentities` as a constructor dependency**

Add `ActingUserIdentities identities` to the primary constructor (after `ObjectSearchService.ObjectSearchServiceClient search`, before `ILogger<ReadPathScenario> logger`). Add `using Iverson.LoadTest.Auth;` / `using Grpc.Core;` to the top of the file (`Grpc.Core` is already present for `RpcException`).

- [ ] **Step 2: Attach acting-user headers to the existing `BenchmarkArticle` calls**

At the top of each of the three existing loops (`GetMany`, `Search`, `Aggregate`), before building the request, add:
```csharp
                var identity = identities.PickRandom();
                var headers  = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));
```
and change the three call sites:
- `var call = retrieval.GetMany(req);` → `var call = retrieval.GetMany(req, headers);`
- `var call = search.Search(req);` → `var call = search.Search(req, headers);`
- `await search.AggregateAsync(req, cancellationToken: ct);` → `await search.AggregateAsync(req, headers, cancellationToken: ct);`

- [ ] **Step 3: Extend to `BenchmarkAuthor`/`BenchmarkTag` via `GetMany`**

After the existing `GetMany` block (batch-scaling loop, before the `// ── Search — filter profiles ──` comment), add one parallel block per new entity type. Load IDs the same way `articleIds` is loaded (raw Postgres query against each table), then run the identical `GetManyBatches` loop shape:
```csharp
        // ── GetMany — BenchmarkAuthor / BenchmarkTag (per CDR round 1 §3.1: read-path must
        // exercise all three entities' authorization rules, not just BenchmarkArticle's) ──
        foreach (var (typeName, tableName) in new[]
                 { ("BenchmarkAuthor", "benchmark_authors"), ("BenchmarkTag", "benchmark_tags") })
        {
            Guid[] ids;
            await using (var pg = new NpgsqlConnection(config.PostgresCs))
            {
                await pg.OpenAsync(ct);
                ids = [.. (await pg.QueryAsync<Guid>($"SELECT \"Id\" FROM {tableName} LIMIT 10000"))];
            }
            if (ids.Length == 0)
            {
                Console.WriteLine($"[GetMany] no {tableName} found — skipping.");
                continue;
            }

            foreach (var batchSize in GetManyBatches)
            {
                var report = new BenchmarkReport();
                Console.WriteLine($"[GetMany] type={typeName} batch={batchSize} iterations={flags.Iterations}...");

                for (var iter = 0; iter < flags.Iterations; iter++)
                {
                    ct.ThrowIfCancellationRequested();
                    var keys = SampleKeys(ids, batchSize, iter);
                    var req  = new RetrievalManyRequest { TypeName = typeName, TraceId = Guid.NewGuid().ToString() };
                    req.Keys.AddRange(keys.Select(k => k.ToString()));

                    var identity = identities.PickRandom();
                    var headers  = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));

                    var t0 = BenchmarkReport.NowMicros();
                    try
                    {
                        var call = retrieval.GetMany(req, headers);
                        await foreach (var _ in call.ResponseStream.ReadAllAsync(ct)) { }
                        report.Record(BenchmarkReport.NowMicros() - t0);
                    }
                    catch (RpcException ex)
                    {
                        report.RecordError();
                        logger.LogDebug(ex, "GetMany failed for {Type}", typeName);
                    }
                }

                report.Print($"GetMany | type={typeName} | batch={batchSize} | iterations={flags.Iterations}", file);
            }
        }
```
(Placed after `file`/`path` are already declared, since it writes to the same report file as the existing sections.) `Search`/`Aggregate` coverage for `BenchmarkAuthor`/`BenchmarkTag` is explicitly out of this task's scope per the source spec (§5: "left to the implementation plan to size... any filter/aggregate profile added for them must explicitly decide whether to reference `Email`/`Category`") — not added here; `GetMany` alone already exercises both ownership-row-filtering and (via the response payload) field masking for these two entities.

- [ ] **Step 4: Build**
```bash
cd Iverson.Server
dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
git add Iverson.LoadTest/Scenarios/ReadPathScenario.cs
git commit -m "feat(loadtest): attach acting-user identity per read-path call, extend GetMany coverage to BenchmarkAuthor/BenchmarkTag"
```

## Known issues inherited from spec

- **Read-path is very likely already broken for these entities on current `main`, independent of this design** — this plan's Task 1/5 schema changes fix it as a side effect; no separate remediation task exists in this plan.
- `Search`/`Aggregate` coverage for `BenchmarkAuthor`/`BenchmarkTag` is deliberately left unsized by this plan (Task 8, Step 3) — a future plan can add it if desired.
- Long-running write-path/read-path invocations that outlast both the access token and the 30-day refresh token are out of scope (already a generous window; not a realistic concern for a single LoadTest invocation).

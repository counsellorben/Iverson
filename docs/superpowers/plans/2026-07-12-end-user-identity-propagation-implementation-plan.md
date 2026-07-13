# End-user identity propagation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/superpowers/specs/2026-07-12-end-user-identity-propagation-design.md` (commit SHA: `8c79d45114610bb73ed58c7688d044df031d0b85`)

**Goal:** Propagate and validate an optional "acting human user" identity alongside the existing service-credential auth on the 4 gRPC services, expose it to service-layer code, and prove the plumbing works end-to-end with a structured log line — laying the foundation Part 5's row/field-level authorization will consume later.

**Architecture:** A second ASP.NET Core JwtBearer scheme (`"ActingUser"`) validates a human's own Authentik-issued access token, sent as a second gRPC metadata entry (`x-acting-user-authorization`) alongside the existing service-credential Bearer token. A global gRPC interceptor reads that metadata key, validates it against the new scheme when present, and exposes the result via a scoped accessor. All 5 client SDKs gain a per-call mechanism (idiomatic per language) for a calling application to attach the acting-user token per request, since — unlike the service token — it varies per call.

**Tech stack:** ASP.NET Core 10 / Grpc.AspNetCore 2.80.0 (server), Authentik (IdP, blueprint-provisioned), 5 client SDKs (.NET/Java/Python/Go/TypeScript).

---

## Global Constraints

Copied verbatim from the spec — binding across every task that touches them:

- Metadata key: `x-acting-user-authorization`, value `Bearer <token>`. Never a proto field.
- The token is **optional**. Its absence must not change existing behavior (no acting user → proceed exactly as today).
- Present-and-invalid (bad signature, expired, wrong issuer, audience not in the `ActingUser` allowlist) → reject the call with `RpcException(StatusCode.Unauthenticated, ...)`. Never silently downgrade to "no acting user."
- Log line format (in the interceptor's valid-token branch): `service {ServiceAccountSubject} acting as user {ActingUserSub} called {Method}`, where `ServiceAccountSubject` is the **default** scheme's `sub` claim (not `client_id`) and `ActingUserSub` is the **ActingUser** scheme's `sub` claim.
- `Authentication:ActingUser:ValidAudiences` is a separate config list from `Authentication:ValidAudiences` — never merge them.
- No `.proto` contract changes anywhere in this plan.

## File Structure

**Create:**
- `Iverson.Server/Iverson.Api/IActingUserAccessor.cs` — scoped accessor interface + implementation
- `Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs` — the validation interceptor
- `Iverson.Server/Iverson.Api.Tests/Helpers/TestJwtFactory.cs` — hand-signed JWT helper for tests (no existing precedent in this repo)
- `Iverson.Server/Iverson.Api.Tests/Grpc/ActingUserInterceptorTests.cs` — interceptor integration tests
- `Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserMetadata.cs` — per-call header helper
- `Iverson.Server/deploy/scripts/mint_acting_user_token.py` — scripted flow-executor harness (identification → password → TOTP → PKCE token)

**Modify:**
- `Iverson.Server/Iverson.Api/Program.cs` — second JwtBearer scheme, interceptor registration, DI registration
- `Iverson.Server/Iverson.Api/appsettings.json` — `Authentication:ActingUser` config block
- `Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs` — test signing key for both schemes
- `Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` — add `Grpc.Net.Client` package reference
- `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` — `iverson-loadtest-human` client + test human user (docker-compose target)
- `Iverson.Server/docker-compose.yml` — `Authentication__ActingUser__*` env vars
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml` — parallel additions (kind/cloud target)
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml` — new secret for `iverson-loadtest-human` client-id + test user password
- `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` — `Authentication__ActingUser__*` env vars
- `Iverson.Clients/Go/iverson/auth.go` — `WithActingUserToken` + `GetRequestMetadata` update
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java` — `ACTING_USER_TOKEN` key + `applyRequestMetadata` update
- `Iverson.Clients/Python/iverson_client/auth.py` — `acting_user_metadata()` helper
- `Iverson.Clients/TypeScript/src/auth.ts` — `createActingUserMetadata()` helper
- `Iverson.Server/Iverson.LoadTest/Program.cs` — new `acting-user-smoke-test` command

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time and are **not** re-verified here (see the spec's own "Verified assumptions" section for evidence):

- The 4 gRPC services (`ObjectMappingGrpcService`, `ObjectPersistenceGrpcService`, `ObjectRetrievalGrpcService`, `ObjectSearchGrpcService`) are the complete set; 2 of them use server-streaming RPCs.
- No gRPC interceptor is registered today.
- ASP.NET Core supports multiple named JwtBearer schemes with independent config and a custom token source via `OnMessageReceived`.
- Grpc.AspNetCore interceptors register globally via `AddGrpc(options.Interceptors.Add<T>())`, with per-request DI lifetime.
- The default scheme's `HttpContext.User` is populated before any interceptor executes.
- Go's `PerRPCCredentials.GetRequestMetadata` receives the caller's real per-call `context.Context`.
- Java's `CallCredentials.RequestInfo.getCallOptions()` exposes per-call `CallOptions`.
- Authentik has no RFC 8693 token-exchange grant.
- `authentik_core.user` blueprint model supports a plaintext `password` attr.
- Authentik's flow-executor API is a documented get-challenge/solve-challenge JSON mechanism; the TOTP enrollment challenge includes the shared secret in `config_url`.
- `default-authentication-flow`'s stage order is identification (10) → password (20) → MFA validation (30) → login (100).
- Authentik's `client_credentials` grant issues tokens to a synthesized service account (`ak-<provider_name>-client_credentials`), not to an account identified by the literal `client_id` — this is why the log line uses `ServiceAccountSubject`/`sub`, not `client_id`.
- Python's and TypeScript's per-call explicit metadata arguments combine additively with `CallCredentials`-supplied metadata.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Server/Iverson.Api/IActingUserAccessor.cs` does not exist; no `IActingUserAccessor`/`IHttpContextAccessor` usage anywhere in `Iverson.Server` yet | `grep -rln "IHttpContextAccessor\|IActingUserAccessor" Iverson.Server --include=*.cs` → no hits |
| 2 | Code validity | `Grpc.Core.Interceptors.Interceptor` exposes `public virtual Task<TResponse> UnaryServerHandler<TRequest,TResponse>(TRequest, ServerCallContext, UnaryServerMethod<TRequest,TResponse>)` and `public virtual Task ServerStreamingServerHandler<TRequest,TResponse>(TRequest, IServerStreamWriter<TResponse>, ServerCallContext, ServerStreamingServerMethod<TRequest,TResponse>)` | Fetched `grpc.github.io/grpc/csharp-dotnet/api/Grpc.Core.Interceptors.Interceptor.html` |
| 3 | Code validity | `Grpc.Core.Metadata.Get(string key)` exists, returns the last matching entry or `null` | Confirmed via grpc-csharp API docs |
| 4 | Consumer impact / code validity | `System.IdentityModel.Tokens.Jwt` and `Microsoft.IdentityModel.JsonWebTokens` (8.0.1) already resolve transitively into `Iverson.Api.Tests` via the `Iverson.Api` ProjectReference → `Microsoft.AspNetCore.Authentication.JwtBearer` — no new PackageReference needed for `TestJwtFactory.cs` | `grep` of `Iverson.Api.Tests/obj/project.assets.json` → both packages present at 8.0.1 |
| 5 | File path / consumer impact | `Grpc.Net.Client` is **not** currently referenced by `Iverson.Api.Tests.csproj` — required for `GrpcChannel.ForAddress` in Task 3's interceptor tests; must be added at version `2.80.0` to match the already-referenced `Grpc.AspNetCore 2.80.0` | Read `Iverson.Api.Tests.csproj` (no `Grpc.Net.Client` entry) and `Iverson.Api.csproj` (`Grpc.AspNetCore` at `2.80.0`) |
| 6 | Consumer impact | `Iverson.Client.Contracts.csproj` generates C# client stubs (`GrpcServices="Both"`), so `ObjectSearchService.ObjectSearchServiceClient` etc. are available to `Iverson.Api.Tests` transitively; only the client transport (`Grpc.Net.Client`) was missing | Read `Iverson.Client.Contracts.csproj` |
| 7 | Ordering / consumer impact | Testing the interceptor's acting-user branches over a real `Grpc.Net.Client` channel requires **both** JwtBearer schemes to accept a test-signed token — not just `ActingUser` — because `FallbackPolicy.RequireAuthenticatedUser()` on the **default** scheme blocks every call before the interceptor's acting-user logic runs. `AuthTestWebApplicationFactory` must therefore override both schemes with a shared test signing key. Existing "no token → 401" tests in `AuthenticationPipelineTests` are unaffected (no token means no validation attempt regardless of signing key). | Re-read `Program.cs:100-104` (`FallbackPolicy`) and `AuthenticationPipelineTests.cs` (only exercises the no-token path) |
| 8 | Command | `dotnet build <Project>/<Project>.csproj` and `dotnet test <TestProject>/<TestProject>.csproj --filter <Name>`, run from `Iverson.Server/`, are this repo's established commands (no `.sln` at that level) | `find Iverson.Server -maxdepth 1 -iname "*.sln"` → none; cross-checked against `docs/superpowers/plans/2026-07-11-grpc-and-admin-authentication-implementation-plan.md`'s own build/test commands |
| 9 | Signature / consumer impact | `ILogger<T>` primary-constructor injection is the established logging convention in `Iverson.Api/Grpc/*.cs` | `grep -n "ILogger" ObjectMappingGrpcService.cs` → `ILogger<ObjectMappingGrpcService> _logger` in the primary constructor |
| 10 | Command / file path | `Iverson.LoadTest/Program.cs` is a top-level-statements CLI reading `IVERSON_*` env vars and `--flag` args via `CommandFlags.Parse`, dispatching on `args[0]`; adding an `acting-user-smoke-test` command means one more `case` in the existing `switch` plus one new env var (`IVERSON_ACTING_USER_TOKEN`) — no new parsing infrastructure needed | Read `Iverson.LoadTest/Program.cs` in full |
| 11 | Consumer impact | Extending `OAuth2ClientCredentials.GetRequestMetadata`/`applyRequestMetadata` (Go/Java) and adding new exported helpers (Python/TS) are pure additions — no existing exported signature changes in any of the 5 SDKs, so no existing caller anywhere in this repo (`Iverson.LoadTest`'s `.NET` usage, and each language's own sample dirs) breaks | Re-read all 5 auth files; every proposed change is additive (new field/const/function), none removes or retypes an existing member |
| 12 | Consumer impact | Both blueprint YAML edits (compose-only and helm) are pure additions of new `model:` entries (`iverson-loadtest-human` provider/application/user) — no existing `identifiers:`-matched entry is modified, so no existing client (`iverson-loadtest`, `iverson-webtest`, `iverson-admin-automation`, `iverson-oidc-default`) is affected | Re-read both blueprint files in full; new entries use distinct `name:`/`slug:`/`username:` identifiers |
| 13 | Consumer impact | `charts/authentik/templates/*.tgz`-style dependency caching (from this repo's own established gotcha) means `helm dependency build` must be re-run after editing `charts/authentik/templates/blueprints-configmap-service-clients.yaml` or `secret-service-clients.yaml`, or the live edits are silently shadowed during a kind smoke test | Established repo convention (memory: `helm dependency build` re-run required after every subchart template edit) |
| 14 | Sibling-set sweep | All 5 SDKs' new per-call mechanisms use the **same** metadata key string (`x-acting-user-authorization`) and the **same** `Bearer <token>` value format — checked each of the 5 code blocks below for the literal string; no language uses a different key or omits the `Bearer ` prefix | Cross-read all 5 draft code blocks against the Global Constraints' metadata key |
| 15 | Sibling-set sweep | Both blueprint targets (compose-only, helm) provision `iverson-loadtest-human` as `client_type: public` with PKCE and no `client_secret` (public clients never have one) — checked both YAML blocks for this; the compose-only block uses a fixed dev `client_id`, the helm block uses `randAlphaNum`/`lookup`, matching each file's own existing convention for `iverson-oidc-default` | Re-read both existing `iverson-oidc-default` entries as the templates for the new entries |
| 16 | Code validity | `ObjectSearchGrpcService`'s `Search`, `SearchSimilar`, `SearchChunks`, and `GroupBy` are all server-streaming (`Task`-returning, `IServerStreamWriter<T>` parameter); `Aggregate` is the sole true-unary RPC (`Task<AggregateResponse>`) — Tasks 3 and 11 use `Aggregate`, not `Search`, for auth-layer-only assertions | `grep -n "public override.*Search\|public override.*Aggregate" ObjectSearchGrpcService.cs` → confirmed signatures |
| 17 | Code validity | `Aggregate` calls `RequireSchema(request.TypeName)` before anything else, throwing `RpcException(StatusCode.FailedPrecondition, ...)` for an empty/unregistered `TypeName` — an empty `AggregateRequest()` therefore always throws a **non**-`Unauthenticated` `RpcException` once past the auth layer, which is exactly the signal Task 3/11's assertions rely on (not "no exception at all") | Read `ObjectSearchGrpcService.cs:217-243` (`Aggregate` method body) and `:354-356` (`RequireSchema`'s own throw) |
| 18 | Consumer impact | `Program.cs`'s top-level command dispatch (`switch (command)`) already calls `services.GetRequiredService<T>()` directly for every case (e.g. `"seed"` → `GetRequiredService<DirectSeeder>()`); constructor injection is the convention for the *scenario classes themselves* (e.g. `ReadPathScenario`'s primary constructor), not for `Program.cs`'s dispatch — Task 11's new case matches the dispatch-level convention | Read `Iverson.LoadTest/Program.cs` (dispatch `switch`) and `Scenarios/ReadPathScenario.cs:1-15` (constructor) |
| 19 | Code validity | `JwtBearerOptions.MapInboundClaims` defaults to `true`, which remaps the JWT `"sub"` claim to `ClaimTypes.NameIdentifier` on the resulting `ClaimsPrincipal` — without explicitly setting it `false` on both schemes, Task 2's `FindFirst("sub")` calls always return `null`. Round-1 critical-implementation-review finding; fixed by setting `options.MapInboundClaims = false` on both `AddJwtBearer` registrations in Task 1. | Confirmed via Microsoft Learn's `JwtBearerOptions.MapInboundClaims` docs (default `true`, current through ASP.NET Core 10.0) and cross-checked that `OperatorAuthorizationPolicy.cs`'s existing `"groups"`/`"scope"` claim reads were never affected because neither name is in the default remapping table |

## Tasks

### Task 1: `ActingUser` JwtBearer scheme, config, and accessor

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Modify: `Iverson.Server/Iverson.Api/appsettings.json`
- Create: `Iverson.Server/Iverson.Api/IActingUserAccessor.cs`

**Interfaces:**
- Produces: `IActingUserAccessor` (registered scoped in DI), the `"ActingUser"` JwtBearer scheme name — consumed by Task 2.

- [ ] **Step 1: Create the accessor**

`Iverson.Server/Iverson.Api/IActingUserAccessor.cs`:
```csharp
using System.Security.Claims;

namespace Iverson.Api;

public interface IActingUserAccessor
{
    ClaimsPrincipal? ActingUser { get; set; }
}

public sealed class ActingUserAccessor : IActingUserAccessor
{
    public ClaimsPrincipal? ActingUser { get; set; }
}
```

- [ ] **Step 2: Add the config block**

In `appsettings.json`, extend the existing `Authentication` object:
```json
  "Authentication": {
    "Authority": "",
    "ValidAudiences": [],
    "ActingUser": {
      "Authority": "",
      "ValidAudiences": []
    }
  }
```

- [ ] **Step 3: Register the second scheme and the accessor**

In `Program.cs`, chain a second `.AddJwtBearer(...)` onto the existing `AddAuthentication` call (lines 86-98):
```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = cfg["Authentication:Authority"];
        options.TokenValidationParameters.ValidAudiences = cfg.GetSection("Authentication:ValidAudiences").Get<string[]>();
        options.RequireHttpsMetadata = false;
        // Without this, ASP.NET Core's default claim-type mapping silently renames the "sub"
        // claim to ClaimTypes.NameIdentifier (a legacy WS-Federation-era remapping table that
        // JwtBearerOptions.MapInboundClaims applies by default) — ActingUserInterceptor's
        // FindFirst("sub") would then always return null. Confirmed: this repo's only other
        // direct-claim-read code (OperatorAuthorizationPolicy) reads "groups"/"scope", neither
        // of which is in that remapping table, which is why this was never hit before.
        options.MapInboundClaims = false;
    })
    .AddJwtBearer("ActingUser", options =>
    {
        options.Authority = cfg["Authentication:ActingUser:Authority"];
        options.TokenValidationParameters.ValidAudiences = cfg.GetSection("Authentication:ActingUser:ValidAudiences").Get<string[]>();
        options.RequireHttpsMetadata = false;
        options.MapInboundClaims = false;
        // gRPC metadata IS the HTTP/2 header set — same mechanism the default scheme already
        // relies on for the "authorization" key. This scheme reads a different key so it
        // doesn't collide with the service credential on the same call.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // "x-acting-user-authorization" is the Global Constraints' metadata key,
                // also defined as ActingUserInterceptor.MetadataKey (Task 2) — kept as a
                // literal here rather than referencing that constant so this task builds
                // standalone without a forward dependency on Task 2's file.
                var header = context.Request.Headers["x-acting-user-authorization"].ToString();
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    context.Token = header["Bearer ".Length..];
                return Task.CompletedTask;
            }
        };
    });
```

Add near the existing `AddAuthorization` block (after line 109):
```csharp
builder.Services.AddScoped<IActingUserAccessor, ActingUserAccessor>();
```

- [ ] **Step 4: Build**
```bash
cd Iverson.Server
dotnet build Iverson.Api/Iverson.Api.csproj
```
Expected: 0 errors. This task is self-contained and does not depend on Task 2.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/appsettings.json Iverson.Server/Iverson.Api/IActingUserAccessor.cs
git commit -m "feat(api): add ActingUser JwtBearer scheme, config, and accessor"
```

---

### Task 2: gRPC interceptor

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`

**Interfaces:**
- Consumes: `IActingUserAccessor` (Task 1), `"ActingUser"` scheme name (Task 1).
- Produces: `ActingUserInterceptor.MetadataKey` constant (consumed by Task 1's `OnMessageReceived`, all 5 SDK tasks, and Task 3's tests).

- [ ] **Step 1: Create the interceptor**

`Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs`:
```csharp
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Authentication;

namespace Iverson.Api.Grpc;

public sealed class ActingUserInterceptor(ILogger<ActingUserInterceptor> logger) : Interceptor
{
    public const string MetadataKey = "x-acting-user-authorization";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await ValidateActingUserAsync(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await ValidateActingUserAsync(context);
        await continuation(request, responseStream, context);
    }

    private async Task ValidateActingUserAsync(ServerCallContext context)
    {
        var header = context.RequestHeaders.Get(MetadataKey)?.Value;
        if (string.IsNullOrEmpty(header))
            return;

        var httpContext = context.GetHttpContext();
        var result = await httpContext.AuthenticateAsync("ActingUser");
        if (!result.Succeeded || result.Principal is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Acting-user token is invalid."));

        httpContext.RequestServices.GetRequiredService<IActingUserAccessor>().ActingUser = result.Principal;

        var serviceSubject    = httpContext.User.FindFirst("sub")?.Value ?? "unknown";
        var actingUserSubject = result.Principal.FindFirst("sub")?.Value ?? "unknown";
        logger.LogInformation(
            "service {ServiceAccountSubject} acting as user {ActingUserSub} called {Method}",
            serviceSubject, actingUserSubject, context.Method);
    }
}
```

- [ ] **Step 2: Register the interceptor globally**

In `Program.cs`, change line 84 from `builder.Services.AddGrpc();` to:
```csharp
builder.Services.AddGrpc(options => options.Interceptors.Add<ActingUserInterceptor>());
```

- [ ] **Step 3: Build**
```bash
cd Iverson.Server
dotnet build Iverson.Api/Iverson.Api.csproj
```
Expected: 0 errors — this also resolves Task 1's forward reference to `ActingUserInterceptor.MetadataKey`.

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs Iverson.Server/Iverson.Api/Program.cs
git commit -m "feat(api): add ActingUserInterceptor validating the acting-user token on all 4 gRPC services"
```

---

### Task 3: Automated interceptor tests

**Files:**
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj`
- Create: `Iverson.Server/Iverson.Api.Tests/Helpers/TestJwtFactory.cs`
- Create: `Iverson.Server/Iverson.Api.Tests/Grpc/ActingUserInterceptorTests.cs`

**Interfaces:**
- Consumes: `ActingUserInterceptor.MetadataKey` (Task 2), `IActingUserAccessor` (Task 1).

- [ ] **Step 1: Add the `Grpc.Net.Client` package**

In `Iverson.Api.Tests.csproj`, add to the existing `<ItemGroup>` of `PackageReference`s:
```xml
    <PackageReference Include="Grpc.Net.Client" Version="2.80.0" />
```

- [ ] **Step 2: Create the JWT test helper**

`Iverson.Server/Iverson.Api.Tests/Helpers/TestJwtFactory.cs`:
```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Iverson.Api.Tests.Helpers;

public static class TestJwtFactory
{
    public static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("test-signing-key-at-least-32-bytes-long-for-hs256"));

    public static string CreateToken(string audience, string subject, DateTime? expires = null)
    {
        var credentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            audience: audience,
            claims: [new Claim("sub", subject)],
            expires: expires ?? DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
```

- [ ] **Step 3: Override both schemes with the test signing key**

In `AuthTestWebApplicationFactory.ConfigureWebHost`, inside the existing `builder.ConfigureServices(services => { ... })` block, add:
```csharp
services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Authority = null;
    options.TokenValidationParameters.ValidateIssuer = false;
    options.TokenValidationParameters.ValidAudiences = ["test-service-audience"];
    options.TokenValidationParameters.IssuerSigningKey = TestJwtFactory.SigningKey;
});
services.PostConfigure<JwtBearerOptions>("ActingUser", options =>
{
    options.Authority = null;
    options.TokenValidationParameters.ValidateIssuer = false;
    options.TokenValidationParameters.ValidAudiences = ["test-actinguser-audience"];
    options.TokenValidationParameters.IssuerSigningKey = TestJwtFactory.SigningKey;
});
```
Add `using Microsoft.AspNetCore.Authentication.JwtBearer;` to the file's usings.

- [ ] **Step 4: Write the interceptor tests**

`Iverson.Server/Iverson.Api.Tests/Grpc/ActingUserInterceptorTests.cs`:
`Aggregate` is the one true-unary RPC on `ObjectSearchGrpcService` (`Search`/`SearchSimilar`/`SearchChunks`/`GroupBy` are all server-streaming — confirmed by reading `ObjectSearchGrpcService.cs`'s method signatures), so it's the simplest call for auth-layer-only assertions. An empty `AggregateRequest` fails business-logic validation inside the service method (`RequireSchema` throws `FailedPrecondition` for an empty `TypeName`) — that's expected and irrelevant here; these tests assert only on the **auth layer's** behavior (does the call reach business logic at all, i.e. does it *not* fail with `Unauthenticated`), not on a clean business-logic result:
```csharp
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Grpc;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class ActingUserInterceptorTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly ObjectSearchService.ObjectSearchServiceClient _client;

    public ActingUserInterceptorTests(AuthTestWebApplicationFactory factory)
    {
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });
        _client = new ObjectSearchService.ObjectSearchServiceClient(channel);
    }

    private static Metadata ServiceOnlyHeaders() => new()
    {
        { "authorization", $"Bearer {TestJwtFactory.CreateToken("test-service-audience", "ak-test-service")}" }
    };

    private async Task<RpcException?> TryAggregateAsync(Metadata headers)
    {
        try
        {
            await _client.AggregateAsync(new AggregateRequest(), headers);
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task Call_NoActingUserToken_DoesNotFailAuth()
    {
        var ex = await TryAggregateAsync(ServiceOnlyHeaders());

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — expected, business logic not auth
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Call_ValidActingUserToken_DoesNotFailAuth()
    {
        var headers = ServiceOnlyHeaders();
        headers.Add(ActingUserInterceptor.MetadataKey,
            $"Bearer {TestJwtFactory.CreateToken("test-actinguser-audience", "test-human-sub")}");

        var ex = await TryAggregateAsync(headers);

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Call_InvalidActingUserToken_ThrowsUnauthenticated()
    {
        var headers = ServiceOnlyHeaders();
        headers.Add(ActingUserInterceptor.MetadataKey,
            $"Bearer {TestJwtFactory.CreateToken("wrong-audience", "test-human-sub")}");

        var ex = await TryAggregateAsync(headers);

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }
}
```

- [ ] **Step 5: Run the tests**
```bash
cd Iverson.Server
dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj
dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~ActingUserInterceptorTests|FullyQualifiedName~AuthenticationPipelineTests"
```
Expected: all `ActingUserInterceptorTests` pass, and all pre-existing `AuthenticationPipelineTests` still pass unchanged (verifies assumption #7 — the test-signing-key override doesn't affect the no-token 401 path).

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs Iverson.Server/Iverson.Api.Tests/Helpers/TestJwtFactory.cs Iverson.Server/Iverson.Api.Tests/Grpc/ActingUserInterceptorTests.cs Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
git commit -m "test(api): cover ActingUserInterceptor's absent/valid/invalid branches"
```

---

### Task 4: Authentik provisioning — docker-compose target

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- Modify: `Iverson.Server/docker-compose.yml`

- [ ] **Step 1: Add the `iverson-loadtest-human` client and a dedicated test human user**

Append to `compose-only/service-clients.yaml` (following the existing `iverson-oidc-default` entry as the template for a public/PKCE client):
```yaml
  - model: authentik_providers_oauth2.oauth2provider
    identifiers:
      name: iverson-loadtest-human
    attrs:
      authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
      invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
      client_type: public
      redirect_uris:
        - matching_mode: strict
          url: "http://localhost/placeholder-callback"
          redirect_uri_type: authorization
      client_id: "dev-iverson-loadtest-human-client-id"
      signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
      issuer_mode: global
  - model: authentik_core.application
    identifiers:
      slug: iverson-loadtest-human
    attrs:
      name: iverson-loadtest-human
      provider: !Find [authentik_providers_oauth2.oauth2provider, [name, iverson-loadtest-human]]
  - model: authentik_core.user
    identifiers:
      username: iverson-acting-user-smoke-test
    attrs:
      username: iverson-acting-user-smoke-test
      name: "Iverson Acting-User Smoke Test"
      email: iverson-acting-user-smoke-test@example.invalid
      password: "dev-only-not-for-production-smoke-test-password-0123456789"
      is_active: true
```

- [ ] **Step 2: Add the API's `ActingUser` scheme env vars**

In `docker-compose.yml`'s `iverson-api` service `environment:` block, after the existing `Authentication__ValidAudiences__3` line:
```yaml
      - Authentication__ActingUser__Authority=http://authentik-server:9000/application/o/iverson-api/
      - Authentication__ActingUser__ValidAudiences__0=dev-iverson-loadtest-human-client-id
```

- [ ] **Step 3: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml Iverson.Server/docker-compose.yml
git commit -m "feat(deploy): provision iverson-loadtest-human client and smoke-test user (docker-compose)"
```

---

### Task 5: Authentik provisioning — kind/helm target

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`

- [ ] **Step 1: Add a secret for the new client-id and test-user password**

Append to `secret-service-clients.yaml` (following the existing `iverson-authentik-human-oidc-client` secret's pattern — public client, so client-id only, no secret):
```yaml
---
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-loadtest-human-client
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existingHuman := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-loadtest-human-client" .Release.Name) }}
{{- if $existingHuman }}
  client-id: {{ index $existingHuman.data "client-id" }}
{{- else }}
  client-id: {{ randAlphaNum 40 | b64enc }}
{{- end }}
---
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-smoke-test-user
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existingSmokeUser := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-smoke-test-user" .Release.Name) }}
{{- if $existingSmokeUser }}
  password: {{ index $existingSmokeUser.data "password" }}
{{- else }}
  password: {{ randAlphaNum 32 | b64enc }}
{{- end }}
```

- [ ] **Step 2: Add the blueprint entries**

In `blueprints-configmap-service-clients.yaml`, add lookups near the top (alongside the existing 4):
```yaml
{{- $loadtestHuman := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-loadtest-human-client" .Release.Name) }}
{{- $smokeTestUser := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-smoke-test-user" .Release.Name) }}
```
And append entries (following the existing `iverson-oidc-default` block as the template):
```yaml
      - model: authentik_providers_oauth2.oauth2provider
        identifiers:
          name: iverson-loadtest-human
        attrs:
          authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
          invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
          client_type: public
          redirect_uris:
            - matching_mode: strict
              url: "https://{{ .Values.global.ingressHost }}/placeholder-callback"
              redirect_uri_type: authorization
          client_id: {{ if $loadtestHuman }}{{ index $loadtestHuman.data "client-id" | b64dec }}{{ else }}{{ randAlphaNum 40 }}{{ end }}
          signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
          issuer_mode: global
      - model: authentik_core.application
        identifiers:
          slug: iverson-loadtest-human
        attrs:
          name: iverson-loadtest-human
          provider: !Find [authentik_providers_oauth2.oauth2provider, [name, iverson-loadtest-human]]
      - model: authentik_core.user
        identifiers:
          username: iverson-acting-user-smoke-test
        attrs:
          username: iverson-acting-user-smoke-test
          name: "Iverson Acting-User Smoke Test"
          email: iverson-acting-user-smoke-test@example.invalid
          password: {{ if $smokeTestUser }}{{ index $smokeTestUser.data "password" | b64dec }}{{ else }}{{ randAlphaNum 32 }}{{ end }}
          is_active: true
```

- [ ] **Step 3: Add the API's `ActingUser` scheme env vars**

In `charts/api/templates/deployment.yaml`, after the existing `Authentication__ValidAudiences__3` block:
```yaml
            - name: Authentication__ActingUser__Authority
              value: "http://{{ .Release.Name }}-authentik:9000/application/o/iverson-api/"
            - name: Authentication__ActingUser__ValidAudiences__0
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-loadtest-human-client, key: client-id }
```

- [ ] **Step 4: Rebuild Helm chart dependencies**

Editing files under `charts/authentik/templates/` is silently shadowed by the cached `charts/*.tgz` archive `helm dependency build` produces (established repo gotcha — see Verified plan-level assumption #13). Before any live kind test (Task 11):
```bash
cd Iverson.Server/deploy/helm/iverson
helm dependency build
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml
git commit -m "feat(deploy): provision iverson-loadtest-human client and smoke-test user (kind/helm)"
```

---

### Task 6: .NET SDK — per-call header helper

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserMetadata.cs`

- [ ] **Step 1: Add the helper**
```csharp
using Grpc.Core;

namespace Iverson.Client.Core;

public static class ActingUserMetadata
{
    public const string MetadataKey = "x-acting-user-authorization";

    public static Metadata WithActingUser(this Metadata headers, string token)
    {
        headers.Add(MetadataKey, $"Bearer {token}");
        return headers;
    }
}
```
Usage at a call site (documented in the class's own doc comment, no wrapper needed — grpc-dotnet's generated methods already accept a `Metadata? headers` parameter): `client.SearchAsync(request, headers: new Metadata().WithActingUser(token))`.

- [ ] **Step 2: Build**
```bash
cd Iverson.Clients/DotNet
dotnet build Iverson.Client.Core/Iverson.Client.Core.csproj
```

- [ ] **Step 3: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserMetadata.cs
git commit -m "feat(dotnet-client): add per-call acting-user metadata helper"
```

---

### Task 7: Go SDK — context-based propagation

**Files:**
- Modify: `Iverson.Clients/Go/iverson/auth.go`

- [ ] **Step 1: Add the context key and helper**

Add near the top of `auth.go`, after the imports:
```go
type actingUserTokenKey struct{}

// ActingUserMetadataKey is the gRPC metadata key carrying the acting-user's
// own Authentik-issued access token, set via WithActingUserToken.
const ActingUserMetadataKey = "x-acting-user-authorization"

// WithActingUserToken attaches a per-call acting-user token to ctx, read by
// OAuth2ClientCredentials.GetRequestMetadata and forwarded as a second gRPC
// metadata entry alongside the service credential.
func WithActingUserToken(ctx context.Context, token string) context.Context {
	return context.WithValue(ctx, actingUserTokenKey{}, token)
}
```

- [ ] **Step 2: Read it in `GetRequestMetadata`**

Replace the existing method body:
```go
func (c *OAuth2ClientCredentials) GetRequestMetadata(ctx context.Context, _ ...string) (map[string]string, error) {
	token, err := c.getToken(ctx)
	if err != nil {
		return nil, err
	}
	md := map[string]string{"authorization": "Bearer " + token}
	if actingUserToken, ok := ctx.Value(actingUserTokenKey{}).(string); ok && actingUserToken != "" {
		md[ActingUserMetadataKey] = "Bearer " + actingUserToken
	}
	return md, nil
}
```

- [ ] **Step 3: Build and run existing tests**
```bash
cd Iverson.Clients/Go
go build ./...
go test ./iverson/...
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Clients/Go/iverson/auth.go
git commit -m "feat(go-client): add per-call acting-user token propagation via context"
```

---

### Task 8: Java SDK — `CallOptions.Key` propagation

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java`

- [ ] **Step 1: Add the `CallOptions.Key` and metadata key constant**

Add as public static fields on `OAuth2ClientCredentials`:
```java
public static final io.grpc.CallOptions.Key<String> ACTING_USER_TOKEN =
    io.grpc.CallOptions.Key.create("acting-user-token");
public static final String ACTING_USER_METADATA_KEY = "x-acting-user-authorization";
```

- [ ] **Step 2: Read it in `applyRequestMetadata`**

Modify the existing method:
```java
@Override
public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
    executor.execute(() -> {
        try {
            Metadata headers = new Metadata();
            headers.put(Metadata.Key.of("Authorization", Metadata.ASCII_STRING_MARSHALLER), "Bearer " + getToken());
            String actingUserToken = requestInfo.getCallOptions().getOption(ACTING_USER_TOKEN);
            if (actingUserToken != null) {
                headers.put(Metadata.Key.of(ACTING_USER_METADATA_KEY, Metadata.ASCII_STRING_MARSHALLER), "Bearer " + actingUserToken);
            }
            applier.apply(headers);
        } catch (Exception e) {
            applier.fail(Status.UNAUTHENTICATED.withCause(e));
        }
    });
}
```
Usage at a call site: `stub.withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, token).search(request)`.

- [ ] **Step 2: Build**
```bash
cd Iverson.Clients/Java/client
mvn -q compile
```

- [ ] **Step 3: Commit**
```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java
git commit -m "feat(java-client): add per-call acting-user token propagation via CallOptions"
```

---

### Task 9: Python SDK — per-call metadata helper

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/auth.py`

- [ ] **Step 1: Add the helper**

Append to `auth.py`:
```python
ACTING_USER_METADATA_KEY = "x-acting-user-authorization"


def acting_user_metadata(token: str) -> tuple[tuple[str, str], ...]:
    """Per-call metadata tuple carrying the acting-user's own Authentik-issued
    token. Pass via a stub call's `metadata=` kwarg alongside the service
    credential, e.g. `stub.Search(request, metadata=acting_user_metadata(token))`.
    """
    return ((ACTING_USER_METADATA_KEY, f"Bearer {token}"),)
```

- [ ] **Step 2: Run existing tests**
```bash
cd Iverson.Clients/Python
python -m pytest tests/test_auth.py
```

- [ ] **Step 3: Commit**
```bash
git add Iverson.Clients/Python/iverson_client/auth.py
git commit -m "feat(python-client): add per-call acting-user metadata helper"
```

---

### Task 10: TypeScript SDK — per-call metadata helper

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/auth.ts`

- [ ] **Step 1: Add the helper**

Append to `auth.ts`:
```typescript
export const ACTING_USER_METADATA_KEY = 'x-acting-user-authorization';

/**
 * Per-call metadata carrying the acting-user's own Authentik-issued token.
 * Merge into a call's metadata argument alongside the service credential.
 */
export function createActingUserMetadata(token: string): grpc.Metadata {
    const metadata = new grpc.Metadata();
    metadata.add(ACTING_USER_METADATA_KEY, `Bearer ${token}`);
    return metadata;
}
```

- [ ] **Step 2: Run existing tests**
```bash
cd Iverson.Clients/TypeScript
npm test -- auth.test.ts
```

- [ ] **Step 3: Commit**
```bash
git add Iverson.Clients/TypeScript/src/auth.ts
git commit -m "feat(ts-client): add per-call acting-user metadata helper"
```

---

### Task 11: Scripted TOTP smoke-test harness + live verification

**Files:**
- Create: `Iverson.Server/deploy/scripts/mint_acting_user_token.py`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs`

**Interfaces:**
- Consumes: Task 2's deployed interceptor, Task 4/5's provisioned `iverson-loadtest-human` client + test user, Task 6's `.NET` helper.

- [ ] **Step 1: Write the flow-executor harness**

`Iverson.Server/deploy/scripts/mint_acting_user_token.py` — walks
`default-authentication-identification` → `default-authentication-password` →
`default-authentication-mfa-validation` (enrolling TOTP on first run, solving
on subsequent runs) → Authorization Code + PKCE against `iverson-loadtest-human`,
against the flow-executor API (`/api/v3/flows/executor/<slug>/`), printing the
resulting access token to stdout.

The script must accept a `--target compose|kind` selector, since the two
environments differ in every one of the following (needed regardless of the
live JSON-shape question below):
- **Base URL:** `compose` → `http://localhost:9000` directly (matches the
  existing Part 2+3 precedent, which already mints service-credential tokens
  this way against docker-compose with no Host-header override needed).
  `kind` → reached via `kubectl port-forward`, requiring the same Host-header
  override already documented in `docs/runbooks/kind-cluster-troubleshooting.md`
  for the admin-operator human-login path (Authentik derives the `iss` claim
  from the request's Host header).
- **`client_id` for the PKCE flow:** `compose` → the fixed dev value
  `dev-iverson-loadtest-human-client-id` (Task 4). `kind` → not known until
  deploy time; retrieve via `kubectl get secret
  <release>-authentik-loadtest-human-client -o jsonpath='{.data.client-id}' |
  base64 -d` (Task 5).

Implementer note: the *response-parsing* side (exact challenge/response JSON
field names) still needs to be confirmed against a live Authentik instance —
`GET /api/v3/flows/executor/default-authentication-flow/` on the target
environment before finalizing the script — since the design spec's
verification confirmed the *mechanism* (get-challenge/solve-challenge, TOTP
secret in `config_url`) but not this repo's exact live JSON payloads. This is
a separate, narrower gap than the target-selection interface above, which
this plan can and does specify without needing live access.

- [ ] **Step 2: Add a LoadTest command to exercise it**

In `Iverson.LoadTest/Program.cs`, add one more env var near the existing ones (after `tokenEndpoint`):
```csharp
var actingUserToken = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_TOKEN");
```
Add one more `case` to the `switch (command)` block. `Aggregate` is used for the same reason as Task 3's tests (the only true-unary RPC on `ObjectSearchService`); `services.GetRequiredService<T>()` matches `Program.cs`'s own existing top-level dispatch style (e.g. the `"seed"` case already does `services.GetRequiredService<DirectSeeder>()`) — `AddGrpcClient<ObjectSearchService.ObjectSearchServiceClient>` in `ServiceCollectionExtensions.cs` registers the client as resolvable this way, same mechanism `ReadPathScenario`'s constructor already relies on:
```csharp
    case "acting-user-smoke-test":
        if (actingUserToken is null)
        {
            Console.Error.WriteLine("acting-user-smoke-test requires IVERSON_ACTING_USER_TOKEN.");
            return 1;
        }
        var searchClient = services.GetRequiredService<ObjectSearchService.ObjectSearchServiceClient>();
        var headers = new Metadata().WithActingUser(actingUserToken);
        try
        {
            await searchClient.AggregateAsync(new AggregateRequest(), headers);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Unauthenticated)
        {
            // Expected: an empty AggregateRequest fails business-logic validation
            // (RequireSchema/FailedPrecondition). The auth layer already accepted the
            // call by the time this throws — that's what this command is checking.
        }
        Console.WriteLine("acting-user-smoke-test: call passed the auth layer — check API logs for the structured log line.");
        break;
```
Add `using Grpc.Core;`.
Add the new command to the `Usage:` help text block alongside the existing ones.

- [ ] **Step 3: Run live against docker-compose**
```bash
cd Iverson.Server
docker compose up -d
python3 deploy/scripts/mint_acting_user_token.py --target compose > /tmp/acting-user-token.txt
export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id
export IVERSON_CLIENT_SECRET=dev-only-not-for-production-loadtest-secret-0123456789
export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/
export IVERSON_ACTING_USER_TOKEN=$(cat /tmp/acting-user-token.txt)
dotnet run --project Iverson.LoadTest -- acting-user-smoke-test
docker compose logs iverson-api | grep "acting as user"
```
Expected: the smoke-test command succeeds, and the API logs contain the structured log line naming the `iverson-loadtest` service account and the test human user's subject.

- [ ] **Step 4: Run live against kind**

Same shape as Step 3, but through `kubectl port-forward` to the kind-deployed `iverson-api` and `iverson-authentik`, following the Host-header workaround already documented in `docs/runbooks/kind-cluster-troubleshooting.md` from Part 2+3 (Authentik's issuer claim is derived from the request's Host header, so `mint_acting_user_token.py` needs a `-H "Host: <in-cluster-authentik-service>"`-equivalent override when port-forwarded). Re-run `helm dependency build` first (Task 5 Step 4) if this is the first live test since Task 5's edits.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/deploy/scripts/mint_acting_user_token.py Iverson.Server/Iverson.LoadTest/Program.cs
git commit -m "feat(loadtest): add scripted TOTP smoke-test harness and acting-user-smoke-test command"
```

## Tasks NOT in this plan

- Any authorization decision based on *who* the acting user is. This design only validates that the token is well-formed and genuinely issued by Authentik to a real user — it does not change whether any call succeeds or what data it returns. That's Part 5's job.
- The 3 `/admin/*` HTTP endpoints. Those already authenticate the human directly (Part 2+3); there is no "calling on behalf of" concept there — the operator *is* the caller.
- Any `.proto` contract change.

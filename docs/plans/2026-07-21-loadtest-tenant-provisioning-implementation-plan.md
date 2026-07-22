# LoadTest Tenant Provisioning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-21-loadtest-tenant-provisioning-design.md` (commit SHA: `f27abd4`)

**Goal:** Have `Iverson.LoadTest` provision its own tenant at startup via `TenantLifecycleGrpcService.CreateTenant` (dogfooding Part D's admin API) and use the resulting tenant-admin login as the identity behind its data-plane gRPC traffic, while fixing the pre-existing `DirectSeeder` gap that leaves seeded rows with no `TenantId`.

**Architecture:** `Iverson.Client.Core.AddIversonClient` splits its single shared channel credential into two — one for schema registration (`iverson-admin-automation`, unchanged), one for data-plane calls (the newly-provisioned tenant-admin login). LoadTest's `Program.cs` gains a startup bootstrap step (mint admin-automation token → ensure tenant exists → mint tenant-admin login) gated behind the same commands that already trigger schema registration. `DirectSeeder` gains `TenantId` stamping for Postgres (a column value, matching the existing `OwnerId` pattern) and switches its StarRocks half from raw batch `INSERT` to gRPC `PersistAsync` calls, so the server's existing lazy per-tenant-database provisioning applies.

**Tech stack:** .NET 10, gRPC (`Grpc.Net.Client`), `IdentityModel.Client` (OAuth2 client_credentials), Npgsql/MySqlConnector (unchanged for the Postgres/StarRocks-DDL-adjacent parts).

---

## File Structure

- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs` — split `AddIversonClient`'s channel credential into a mapping-only one and a data-plane one.
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs` — tenant provisioning bootstrap, tenant-admin login minting, wiring into `AddIversonClient`.
- Modify: `Iverson.Server/Iverson.LoadTest/README.md` — document the 4 new env vars.
- Modify: `Iverson.Server/Iverson.LoadTest/Seeding/DirectSeeder.cs` — stamp `TenantId` on Postgres rows; replace StarRocks batch `INSERT` with gRPC posting; delete the now-dead `SrBatchInsertAsync`/`FlushBatchAsync`.
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/WritePathRunner.cs` — make `PrintKafkaLagAsync` `internal` with an optional `file` param so `DirectSeeder` can reuse it.

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time and are trusted as ground truth here (not re-verified):

1. `CreateTenant`/`ListTenants` require the `Operator` policy (`Program.cs:399`).
2. A `tenant-admins`-group login never satisfies `SchemaAdmin`.
3. `iverson-admin-automation` already carries `admin`+`schema_admin`+`tenant_id_admin` scope mappings — one token request with `scope=admin schema_admin` covers both `CreateTenant`/`ListTenants` and `RegisterSchema`.
4. `AddIversonClient` (pre-plan) attaches one shared credential to all 4 typed clients.
5. Tenant/row/field authorization reads only `IActingUserAccessor.ActingUser`, populated only from the acting-user header — the base channel identity's own tenant never gates `ReadPathScenario`/`WritePathRunner`'s row visibility.
6. `ReadPathScenario`/`WritePathRunner` attach the acting-user header on every call.
7. `DirectSeeder` never writes `TenantId` (pre-existing gap, independent of this plan).
8. `TenantId` column is nullable — `seed` doesn't crash today, it just seeds unreadable rows.
9. `ActingUserIdentities.PickRandom()` is a uniform 50/50 split.
10. Proposed tenant id `iverson-loadtest-dynamic` matches `TenantIdentifier.IsValid`'s pattern.
11. `CreateTenant` is not idempotent — a duplicate `tenant_id` throws, so provisioning must check `ListTenants` first.
12. `Iverson.Client.Contracts` (tenant proto clients) already available to LoadTest transitively.
13. `AuthentikFlowExecutorClient`'s TOTP enroll-or-solve logic is generic — works for a brand-new user with no prior enrollment.
14. StarRocks tenant isolation is per-tenant physical databases (`iverson_tenant_{tenantId}`), provisioned lazily only via the gRPC write path — a `TenantId` column value on a shared table has no effect on `Search`/`Aggregate`.
15. `ObjectPersistenceGrpcService.Post` always assigns its own server-generated key (ignores client-supplied `Id`) and unconditionally writes to Postgres regardless of `targetStores` — StarRocks-routed gRPC posts also create an independently-keyed Postgres population, which is harmless since neither `GetMany` nor `Search`/`Aggregate` needs cross-store ID correlation.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs` is the exact file for Task 1 | Read in full |
| 2 | File path | `Iverson.Server/Iverson.LoadTest/Program.cs` is the exact file for Task 2 | Read in full |
| 3 | File path | `Iverson.Server/Iverson.LoadTest/Seeding/DirectSeeder.cs`, `Scenarios/WritePathRunner.cs` are the exact files for Task 3 | Read in full |
| 4 | Signature | `AddIversonClient`'s current signature/body exactly as shown in Task 1's before-code | Read of `ServiceCollectionExtensions.cs:22-75` |
| 5 | Signature | `EntityCoordinator<T>.PersistAsync(T entity, Metadata? headers = null, CancellationToken ct = default)` returns `Task<string?>` | `EntityCoordinator.cs:98` |
| 6 | Signature | `AuthentikIdentityConfig(string Username, string Password, string ClientId, string RedirectUri, string BaseUrl, string? HostHeader, string CacheTargetToken)`; `AuthentikFlowExecutorClient(AuthentikIdentityConfig identity, ILogger<AuthentikFlowExecutorClient> logger)` | `AuthentikFlowExecutorClient.cs:11-24` |
| 7 | Signature | `KafkaClientConfigFactory.ApplySecurity(ClientConfig config, KafkaOptions options)` — static void, namespace `Iverson.Events` | `Iverson.Server/Iverson.Events/KafkaClientConfigFactory.cs:13` |
| 8 | Signature | `WritePathRunner.PrintKafkaLagAsync(...)` is currently `private static`, `file` param is non-nullable `StreamWriter`, writes to both `Console` and `file` | `WritePathRunner.cs:202-283` |
| 9 | Signature | `TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient` is constructible from a `Grpc.Net.Client.GrpcChannel` via `GrpcChannel.ForAddress(...)` | `Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceAuthorizationPipelineTests.cs:29-33` (same exact construction pattern already used against this same service in this repo) |
| 10 | Signature | `CreateTenantRequest{TenantId,DisplayName,AdminUsername,AdminEmail,AdminInitialPassword}`, `Tenant{TenantId,DisplayName,Status}`, `ListTenantsResponse.Tenants` (repeated `Tenant`) | `Iverson.Clients/Common/Proto/tenant_lifecycle.proto` |
| 11 | Consumer impact | `CachedClientCredentialsTokenProvider` is `internal` to `Iverson.Client.Core`, with no `InternalsVisibleTo` covering `Iverson.LoadTest` — the bootstrap admin-automation token mint needs its own direct `IdentityModel.Client` call, not this type | `CachedClientCredentialsTokenProvider.cs:5`, `Iverson.Client.Core.csproj:24-26` (only `Iverson.Client.Core.Tests` listed) |
| 12 | Code validity | `Grpc.Net.Client` (2.80.0) and `IdentityModel` (7.0.0) are already in `Iverson.LoadTest`'s resolved dependency graph transitively via `Iverson.Client.Core` — no new `PackageReference` needed | `grep` of `Iverson.LoadTest/obj/project.assets.json` |
| 13 | Code validity | `IdentityModel.Client`'s `RequestClientCredentialsTokenAsync(HttpClient, ClientCredentialsTokenRequest)` extension exists, returns a response with `.IsError`/`.AccessToken`/`.ExpiresIn` | `Iverson.Client.Core/CachedClientCredentialsTokenProvider.cs:23-37` (identical usage) |
| 14 | Code validity | Raw `GrpcChannel.ForAddress("http://...")` works against the same plaintext h2c endpoint the DI-registered `AddGrpcClient` clients already use successfully in this exact codebase, without an explicit `Http2UnencryptedSupport` switch | No such switch exists anywhere in the repo (`grep` found nothing) and the DI-registered clients already work against `http://localhost:8080` today — verified by analogy, not a direct isolated test; if wrong, the fix is a one-line `AppContext.SetSwitch` addition |
| 15 | Consumer impact (Cat 6) | `AddIversonClient`'s only other consumers are `Iverson.Client.Sample/Program.cs` (named args, unaffected by a new positional optional param) and `Iverson.LoadTest/Program.cs` (updated by this same plan's Task 2) | `grep -rln "AddIversonClient"` across the repo — exactly 3 files: the definition, Sample, LoadTest |
| 16 | Consumer impact (Cat 6) | `PrintKafkaLagAsync`'s only existing caller (`WritePathRunner.cs:197`, inside its own `RunAsync`) already passes a non-null `file` — unaffected by making the param nullable | `grep` for `PrintKafkaLagAsync` — one call site |
| 17 | Consumer impact (Cat 6) | `SrBatchInsertAsync`/`FlushBatchAsync` have no callers besides the 3 `SeedXAsync` methods being changed — safe to delete once those 3 call sites are replaced | `grep -rn "SrBatchInsertAsync\|FlushBatchAsync"` — all hits inside `DirectSeeder.cs` |
| 18 | Consumer impact (Cat 6) | `DirectSeeder` is only ever resolved via `services.AddSingleton<DirectSeeder>()` — no other `new DirectSeeder(...)` call site needs updating for the new constructor params | `grep -rn "new DirectSeeder"` — no hits outside DI registration |
| 19 | Code validity | `WritePathScenario`/`KindWritePathScenario` already inject `EntityCoordinator<BenchmarkArticle/Author/Tag>` directly into a `AddSingleton`-registered class's primary constructor — the same pattern is safe to reuse for `DirectSeeder` (also `AddSingleton`) | `WritePathScenario.cs:12-18`, `KindWritePathScenario.cs:15-22` |
| 20 | Code validity | `System.Linq` (`Enumerable.Range`/`.Select`) is available in `Iverson.LoadTest` without an explicit `using` | `WritePathRunner.cs` uses `Enumerable.Range(...).Select(...)` with no `using System.Linq;` in its using list — confirmed compiling today |
| 21 | Sibling set | All 3 `SeedXAsync` methods (`SeedAuthorsAsync`/`SeedTagsAsync`/`SeedArticlesAsync`) follow the identical Postgres-COPY-then-`SrBatchInsertAsync` structure — the `TenantId`/gRPC-routing change must be applied to all 3, not just Articles | Full read of `DirectSeeder.cs` |
| 22 | Entity fields | `BenchmarkArticle{Id,Title,Body,BenchmarkAuthorId,Category,WordCount,PublishedAt,OwnerId,TenantId}`, `BenchmarkAuthor{Id,Name,Email,Bio,OwnerId,TenantId}`, `BenchmarkTag{Id,Name,Category,OwnerId,TenantId}` | `Entities/BenchmarkArticle.cs`, `BenchmarkAuthor.cs`, `BenchmarkTag.cs` |
| 23 | Task ordering | Task 2's code calls `AddIversonClient`'s new signature — must run after Task 1. Task 3 (DirectSeeder) uses only pre-existing `EntityCoordinator<T>`/`ActingUserIdentities`/`KafkaOptions` DI registrations, unaffected by Tasks 1-2 — independent, can run in parallel | Traced Task 2's and Task 3's actual code dependencies against Task 1's change |
| 24 | Consumer impact | `EntityCoordinator<T>.PersistAsync` does not catch `RpcException` itself — it propagates to the caller uncaught; combined with `Program.cs`'s `BuildAuthorizationRules` restricting `Body`(Article)/`Email`(Author)/`Category`(Tag) to the bypass role only, any gRPC-posting loop that unconditionally sets those fields regardless of posting identity would see guaranteed rejections for the non-bypass identity's share of posts | `EntityCoordinator.cs:98-112` (no try/catch in `PersistAsync`); `Program.cs:105-107` (`BuildAuthorizationRules` call sites) — this is why Task 3's entity construction (Steps 5-7) only sets the restricted field when `identities.Bypass` is posting |

## Tasks

### Task 1: Split `AddIversonClient`'s channel credentials

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Add the `dataPlaneTokenProvider` parameter and split credential attachment**

Replace the whole file's body with:

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
    /// OAuth2 client-credentials identity attached to the schema-registration (mapping) client as
    /// a Bearer token, and as the data-plane fallback when <paramref name="dataPlaneTokenProvider"/>
    /// is omitted. Omit only for calls to endpoints that don't require authentication.
    /// </param>
    /// <param name="dataPlaneTokenProvider">
    /// Token source attached to the persistence/retrieval/search clients instead of
    /// <paramref name="credentials"/>, when supplied. Lets a caller use a different identity
    /// (e.g. a human/acting-user login) for data-plane calls than for schema registration.
    /// </param>
    /// <param name="entityAssemblies">
    /// Assemblies to scan for <c>[IversonEntity]</c> classes.
    /// Defaults to the calling assembly if none are provided.
    /// </param>
    public static IServiceCollection AddIversonClient(
        this IServiceCollection services,
        string grpcEndpoint,
        IversonClientCredentials? credentials = null,
        Func<Task<string>>? dataPlaneTokenProvider = null,
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
            services.AddSingleton(sp => new CachedClientCredentialsTokenProvider(credentials));
            AttachCredentials(mappingBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync());
        }

        if (dataPlaneTokenProvider is not null)
        {
            AttachCredentials(persistenceBuilder, _ => dataPlaneTokenProvider());
            AttachCredentials(retrievalBuilder, _ => dataPlaneTokenProvider());
            AttachCredentials(searchBuilder, _ => dataPlaneTokenProvider());
        }
        else if (credentials is not null)
        {
            AttachCredentials(persistenceBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync());
            AttachCredentials(retrievalBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync());
            AttachCredentials(searchBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync());
        }

        services.AddTransient(typeof(EntityCoordinator<>));

        services.AddSingleton<SchemaRegistrar>();

        return services;
    }

    private static void AttachCredentials(IHttpClientBuilder builder, Func<IServiceProvider, Task<string>> getToken)
    {
        // Without UnsafeUseInsecureChannelCallCredentials=true, CallCredentials are silently
        // dropped over this repo's plaintext h2c channel — no exception, no Authorization
        // header. Confirmed via Microsoft's own docs and a real listening-server test.
        builder
            .ConfigureChannel(o => o.UnsafeUseInsecureChannelCallCredentials = true)
            .AddCallCredentials(async (_, metadata, serviceProvider) =>
            {
                var token = await getToken(serviceProvider);
                metadata.Add("Authorization", $"Bearer {token}");
            });
    }
}
```

- [ ] **Step 2: Build and commit**
```bash
dotnet build Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj
dotnet build Iverson.Clients/DotNet/Iverson.Client.Sample/Iverson.Client.Sample.csproj
git add Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs
git commit -m "feat(client-core): split AddIversonClient credentials for mapping vs data-plane calls"
```

---

### Task 2: LoadTest tenant provisioning and channel wiring

**Depends on:** Task 1 (calls the new `AddIversonClient` signature).

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/README.md`

- [ ] **Step 1: Add new env vars and usings**

In `Program.cs`, add to the `using` list at the top:
```csharp
using Grpc.Net.Client;
using IdentityModel.Client;
```

After the existing `actingUserBypassPassword` line (currently line 41), add:
```csharp
var tenantProvisionId   = Environment.GetEnvironmentVariable("IVERSON_LOADTEST_TENANT_ID") ?? "iverson-loadtest-dynamic";
var tenantAdminUsername = Environment.GetEnvironmentVariable("IVERSON_LOADTEST_TENANT_ADMIN_USERNAME") ?? "iverson-loadtest-tenant-admin";
var tenantAdminEmail    = Environment.GetEnvironmentVariable("IVERSON_LOADTEST_TENANT_ADMIN_EMAIL") ?? "iverson-loadtest-tenant-admin@iverson.local";
var tenantAdminPassword = Environment.GetEnvironmentVariable("IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD") ?? "dev-only-not-for-production-tenant-admin-password-0123456789";
```

- [ ] **Step 2: Add the tenant-provisioning bootstrap before the `ServiceCollection` is built**

Immediately before the existing `var services = new ServiceCollection()` line (currently line 76), insert:

```csharp
var needsTenantAndSchema = command is "seed" or "write-path" or "read-path" or "all";

ActingUserTokenProvider? tenantAdminTokenProvider = null;
if (needsTenantAndSchema && clientCredentials is not null)
{
    Console.WriteLine("Ensuring LoadTest tenant is provisioned...");
    try
    {
        var adminToken = await MintClientCredentialsTokenAsync(clientCredentials);
        await EnsureTenantProvisionedAsync(
            grpcUrl, adminToken, tenantProvisionId, "Iverson LoadTest (dynamic)",
            tenantAdminUsername, tenantAdminEmail, tenantAdminPassword);

        var tenantAdminLoggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        tenantAdminTokenProvider = new ActingUserTokenProvider(new AuthentikFlowExecutorClient(
            new AuthentikIdentityConfig(
                tenantAdminUsername, tenantAdminPassword, actingUserClientId, actingUserRedirectUri,
                actingUserBaseUrl, actingUserHostHeader, actingUserCacheTarget),
            tenantAdminLoggerFactory.CreateLogger<AuthentikFlowExecutorClient>()));
        Console.WriteLine($"Tenant '{tenantProvisionId}' ready.\n");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Tenant provisioning failed: {ex.Message}");
        Console.Error.WriteLine("Is the Iverson API running, and does IVERSON_CLIENT_SCOPE include 'admin schema_admin'?");
        return 1;
    }
}
```

- [ ] **Step 3: Update the `AddIversonClient` call and reuse `needsTenantAndSchema` for schema registration**

Change:
```csharp
    .AddIversonClient(grpcUrl, clientCredentials, entityAssemblies: [typeof(BenchmarkArticle).Assembly])
```
to:
```csharp
    .AddIversonClient(
        grpcUrl, clientCredentials,
        tenantAdminTokenProvider is not null ? () => tenantAdminTokenProvider.GetTokenAsync() : null,
        entityAssemblies: [typeof(BenchmarkArticle).Assembly])
```

Change the existing schema-registration gate from:
```csharp
if (command is "seed" or "write-path" or "read-path" or "all")
```
to:
```csharp
if (needsTenantAndSchema)
```

- [ ] **Step 4: Add the two new local functions**

Near the other local functions at the bottom of the file (alongside `Env`/`BuildAuthorizationRules`/`ClearDataAsync`), add:

```csharp
static async Task<string> MintClientCredentialsTokenAsync(IversonClientCredentials creds)
{
    using var http = new HttpClient();
    var response = await http.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
    {
        Address      = creds.TokenEndpoint,
        ClientId     = creds.ClientId,
        ClientSecret = creds.ClientSecret,
        Scope        = creds.Scope,
    });
    if (response.IsError)
        throw new InvalidOperationException($"Failed to acquire admin-automation token: {response.Error}");
    return response.AccessToken!;
}

static async Task EnsureTenantProvisionedAsync(
    string grpcUrl, string adminToken, string tenantId, string displayName,
    string adminUsername, string adminEmail, string adminPassword)
{
    using var channel = GrpcChannel.ForAddress(grpcUrl);
    var client = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
    var headers = new Metadata { { "authorization", $"Bearer {adminToken}" } };

    var existing = await client.ListTenantsAsync(new ListTenantsRequest(), headers);
    if (existing.Tenants.Any(t => t.TenantId == tenantId))
        return;

    await client.CreateTenantAsync(new CreateTenantRequest
    {
        TenantId             = tenantId,
        DisplayName          = displayName,
        AdminUsername        = adminUsername,
        AdminEmail           = adminEmail,
        AdminInitialPassword = adminPassword,
    }, headers);
}
```

- [ ] **Step 5: Document the new env vars in the README**

In `Iverson.Server/Iverson.LoadTest/README.md`'s "Other environment variables" table, after the `IVERSON_CLIENT_SCOPE` row, add:
```markdown
| `IVERSON_LOADTEST_TENANT_ID` | `iverson-loadtest-dynamic` (only used when `IVERSON_CLIENT_ID` etc. are set — provisioned via `TenantLifecycleGrpcService.CreateTenant` if not already registered) |
| `IVERSON_LOADTEST_TENANT_ADMIN_USERNAME` / `_EMAIL` / `_PASSWORD` | dev-only defaults — the tenant-admin login LoadTest mints and uses as the data-plane gRPC channel's credential |
```

- [ ] **Step 6: Build and commit**
```bash
dotnet build Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj
git add Iverson.Server/Iverson.LoadTest/Program.cs Iverson.Server/Iverson.LoadTest/README.md
git commit -m "feat(loadtest): provision LoadTest's own tenant and use it as the gRPC channel identity"
```

---

### Task 3: `DirectSeeder` `TenantId` fix

**Depends on:** none (independent of Tasks 1-2).

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Seeding/DirectSeeder.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/WritePathRunner.cs`

- [ ] **Step 1: Make `PrintKafkaLagAsync` reusable**

In `WritePathRunner.cs`, change:
```csharp
    private static async Task PrintKafkaLagAsync(
        LoadTestConfig config,
        ILogger logger,
        Action<ClientConfig>? applyKafkaSecurity,
        StreamWriter file,
        CancellationToken ct)
```
to:
```csharp
    internal static async Task PrintKafkaLagAsync(
        LoadTestConfig config,
        ILogger logger,
        Action<ClientConfig>? applyKafkaSecurity,
        StreamWriter? file,
        CancellationToken ct)
```
and change:
```csharp
            var line = $"  Kafka lag: {totalLag:N0} messages ({DateTime.UtcNow:HH:mm:ss})";
            Console.WriteLine(line);
            file.WriteLine(line);
```
to:
```csharp
            var line = $"  Kafka lag: {totalLag:N0} messages ({DateTime.UtcNow:HH:mm:ss})";
            Console.WriteLine(line);
            file?.WriteLine(line);
```

- [ ] **Step 2: Update `DirectSeeder`'s usings and constructor**

Replace the using list at the top of `DirectSeeder.cs` with:
```csharp
using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Dapper;
using Grpc.Core;
using Iverson.Client.Core;
using Iverson.Events;
using Iverson.LoadTest.Auth;
using Iverson.LoadTest.Entities;
using Iverson.LoadTest.Scenarios;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;
```

Change the class declaration from:
```csharp
public sealed class DirectSeeder(LoadTestConfig config, ActingUserIdentities identities)
```
to:
```csharp
public sealed class DirectSeeder(
    LoadTestConfig config,
    ActingUserIdentities identities,
    EntityCoordinator<BenchmarkArticle> articleCoordinator,
    EntityCoordinator<BenchmarkAuthor> authorCoordinator,
    EntityCoordinator<BenchmarkTag> tagCoordinator,
    KafkaOptions kafkaOptions,
    ILogger<DirectSeeder> logger)
```

- [ ] **Step 3: Thread `CommandFlags` through `RunAsync` and the 3 `SeedXAsync` methods**

`SeedAuthorsAsync`/`SeedTagsAsync`/`SeedArticlesAsync` need `flags.Concurrency` for the new gRPC fan-out. Change their signatures and `RunAsync`'s call sites:

```csharp
    public async Task RunAsync(CommandFlags flags, CancellationToken ct = default)
    {
        await using var pg = new NpgsqlConnection(config.PostgresCs);
        await pg.OpenAsync(ct);
        await using var sr = new MySqlConnection(config.StarRocksCs);
        await sr.OpenAsync(ct);

        var authorIds = await SeedAuthorsAsync(pg, sr, flags, flags.ForceReseed, ct);
        await SeedTagsAsync(pg, sr, flags, flags.ForceReseed, ct);
        await SeedArticlesAsync(pg, sr, authorIds, flags, flags.ForceReseed, ct);

        Console.WriteLine("\nWaiting for StarRocks projections to catch up...");
        Action<Confluent.Kafka.ClientConfig>? applyKafkaSecurity = flags.Target == "kind"
            ? c => KafkaClientConfigFactory.ApplySecurity(c, kafkaOptions)
            : null;
        await WritePathRunner.PrintKafkaLagAsync(config, logger, applyKafkaSecurity, file: null, ct);

        Console.WriteLine("\nSeeding complete.");
    }
```

And update each method's own signature (bodies unchanged except where Steps 5-7 say otherwise):
```csharp
    private async Task<Guid[]> SeedAuthorsAsync(
        NpgsqlConnection pg, MySqlConnection sr, CommandFlags flags, bool force, CancellationToken ct)
```
```csharp
    private async Task SeedTagsAsync(
        NpgsqlConnection pg, MySqlConnection sr, CommandFlags flags, bool force, CancellationToken ct)
```
```csharp
    private async Task SeedArticlesAsync(
        NpgsqlConnection pg, MySqlConnection sr, Guid[] authorIds, CommandFlags flags, bool force, CancellationToken ct)
```

- [ ] **Step 4: Add the shared fan-out helper**

Add as a new private static method (near the other helpers at the bottom of the class). Each individual post is wrapped in its own try/catch so one failure (transient or otherwise) doesn't stop the rest of the fan-out or crash the whole `seed` command:
```csharp
    private static async Task PostToStarRocksAsync(
        int count, int concurrency, ILogger logger, Func<int, Task> postOneAsync, CancellationToken ct)
    {
        var perTask = count / concurrency;
        var tasks = Enumerable.Range(0, concurrency).Select(taskIdx => Task.Run(async () =>
        {
            for (var i = 0; i < perTask; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await postOneAsync(taskIdx * perTask + i);
                }
                catch (RpcException ex)
                {
                    logger.LogDebug(ex, "StarRocks post failed at index {Index}", taskIdx * perTask + i);
                }
            }
        }, ct));
        await Task.WhenAll(tasks);
    }
```

- [ ] **Step 5: `SeedAuthorsAsync` — Postgres `TenantId` column + StarRocks via gRPC**

Change the COPY statement from:
```csharp
        await using var writer = await pg.BeginBinaryImportAsync(
            "COPY benchmark_authors (\"Id\", \"Name\", \"Email\", \"Bio\", \"OwnerId\") FROM STDIN (FORMAT BINARY)",
            ct);
```
to:
```csharp
        await using var writer = await pg.BeginBinaryImportAsync(
            "COPY benchmark_authors (\"Id\", \"Name\", \"Email\", \"Bio\", \"OwnerId\", \"TenantId\") FROM STDIN (FORMAT BINARY)",
            ct);
```

In the row-writing loop, change:
```csharp
        for (var i = 0; i < AuthorTarget; i++)
        {
            ids[i] = Guid.NewGuid();
            var ownerId = i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString();
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ids[i],                      NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync($"Author {i}",               NpgsqlDbType.Text, ct);
            await writer.WriteAsync($"author{i}@benchmark.dev",  NpgsqlDbType.Text, ct);
            await writer.WriteAsync(new string('x', 200),        NpgsqlDbType.Text, ct);
            await writer.WriteAsync(ownerId,                     NpgsqlDbType.Text, ct);
            if (i % 5_000 == 0) PrintProgress("Authors", i, AuthorTarget, sw);
        }
```
to:
```csharp
        for (var i = 0; i < AuthorTarget; i++)
        {
            ids[i] = Guid.NewGuid();
            var ownerId  = i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString();
            var tenantId = i % 2 == 0 ? "tenant-smoke-test" : "tenant-bypass";
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ids[i],                      NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync($"Author {i}",               NpgsqlDbType.Text, ct);
            await writer.WriteAsync($"author{i}@benchmark.dev",  NpgsqlDbType.Text, ct);
            await writer.WriteAsync(new string('x', 200),        NpgsqlDbType.Text, ct);
            await writer.WriteAsync(ownerId,                     NpgsqlDbType.Text, ct);
            await writer.WriteAsync(tenantId,                    NpgsqlDbType.Text, ct);
            if (i % 5_000 == 0) PrintProgress("Authors", i, AuthorTarget, sw);
        }
```

Replace the `SrBatchInsertAsync` call:
```csharp
        // ── StarRocks batch INSERT ──
        await SrBatchInsertAsync(sr, "benchmark_authors",
            ["Id", "Name", "Email", "Bio", "OwnerId"],
            ids.Select((id, i) => new object[]
            {
                id.ToString(), $"Author {i}", $"author{i}@benchmark.dev", new string('x', 200),
                i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString()
            }),
            sw, ct);
```
with:
```csharp
        // ── StarRocks via gRPC (server lazily provisions the per-tenant database) ──
        // Email is the field BuildAuthorizationRules restricts to the bypass role (Program.cs) —
        // only set it when the bypass identity is posting, or identities.Regular's posts would
        // always be rejected.
        await PostToStarRocksAsync(AuthorTarget, flags.Concurrency, logger, async i =>
        {
            var identity = i % 2 == 0 ? identities.Regular : identities.Bypass;
            var entity = new BenchmarkAuthor
            {
                Id      = Guid.NewGuid(),
                Name    = $"Author {i}",
                Email   = identity == identities.Bypass ? $"author{i}@benchmark.dev" : "",
                Bio     = new string('x', 200),
                OwnerId = identity == identities.Bypass ? await identity.GetSubAsync(ct) : "",
            };
            var headers = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));
            await authorCoordinator.PersistAsync(entity, headers, ct);
        }, ct);
```

(the `CommandFlags flags` parameter this uses was added to `SeedAuthorsAsync`'s signature in Step 3.)

- [ ] **Step 6: `SeedTagsAsync` — same pattern**

Apply the identical shape from Step 5 to `SeedTagsAsync`: add `"TenantId"` to the COPY column list and a `tenantId = i % 2 == 0 ? "tenant-smoke-test" : "tenant-bypass"` write, and replace its `SrBatchInsertAsync` call with (`Category` is the field restricted to the bypass role for this entity — same conditional-set reasoning as `SeedAuthorsAsync`'s `Email`):
```csharp
        await PostToStarRocksAsync(TagTarget, flags.Concurrency, logger, async i =>
        {
            var identity = i % 2 == 0 ? identities.Regular : identities.Bypass;
            var entity = new BenchmarkTag
            {
                Id       = Guid.NewGuid(),
                Name     = $"tag-{i}",
                Category = identity == identities.Bypass ? Categories[i % Categories.Length] : "",
                OwnerId  = identity == identities.Bypass ? await identity.GetSubAsync(ct) : "",
            };
            var headers = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));
            await tagCoordinator.PersistAsync(entity, headers, ct);
        }, ct);
```
(`CommandFlags flags` again added to `SeedTagsAsync`'s signature per Step 3.)

- [ ] **Step 7: `SeedArticlesAsync` — same pattern, referencing existing `authorIds`**

Add `"TenantId"` to the COPY column list and the same per-row `tenantId` write. Replace its `SrBatchInsertAsync` call with (`Body` is the field restricted to the bypass role for this entity — `Category` is a search-key field, not restricted, and stays unconditional):
```csharp
        await PostToStarRocksAsync(ArticleTarget, flags.Concurrency, logger, async i =>
        {
            var identity = i % 2 == 0 ? identities.Regular : identities.Bypass;
            var cat = Categories[i % Categories.Length];
            var entity = new BenchmarkArticle
            {
                Id                = Guid.NewGuid(),
                Title             = $"Benchmark Article {i}: {cat}",
                Body              = identity == identities.Bypass ? GenerateBody(i) : "",
                BenchmarkAuthorId = authorIds[i % authorIds.Length],
                Category          = cat,
                WordCount         = GenerateBody(i).Length / 5,
                PublishedAt       = baseDate.AddDays(i % 2190),
                OwnerId           = identity == identities.Bypass ? await identity.GetSubAsync(ct) : "",
            };
            var headers = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));
            await articleCoordinator.PersistAsync(entity, headers, ct);
        }, ct);
```
(`CommandFlags flags` added the same way per Step 3; the Kafka-lag-probe wait after all three methods complete is already part of `RunAsync` per Step 3's code block, so no separate step is needed for it here.)

- [ ] **Step 8: Delete the now-dead `SrBatchInsertAsync`/`FlushBatchAsync` methods**

Remove both methods entirely from `DirectSeeder.cs`.

- [ ] **Step 9: Build and commit**
```bash
dotnet build Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj
git add Iverson.Server/Iverson.LoadTest/Seeding/DirectSeeder.cs Iverson.Server/Iverson.LoadTest/Scenarios/WritePathRunner.cs
git commit -m "feat(loadtest): stamp DirectSeeder rows with TenantId, route StarRocks seeding through gRPC"
```

## Tasks NOT in this plan

- Fixing the pre-existing `DirectSeeder` `TenantId` gap for the two already-existing acting-user identities is included here (§4) because it's needed to make this design's own read-path traffic meaningful; deeper load-test data-distribution changes are not part of this work.
- No change to `TenantLifecycleGrpcService`/`TenantAdminGrpcService` themselves, or to Authentik blueprints — this design only adds a caller.

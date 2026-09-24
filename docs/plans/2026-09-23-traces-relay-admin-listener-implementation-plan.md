# Traces Relay on the Admin Listener — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-23-traces-relay-admin-listener-design.md` (commit SHA: `ebf00bf4`)

**Goal:** Get the admin console's browser span export to Jaeger again, in compose and on every Helm profile that runs the console. Authentication and rate limiting stay exactly as they are.

**Architecture:** Two changes, one on the server and one in Helm.
- **Server.** The `/v1/traces` relay loses its `RequireListenerPort(8080)` pin and gains `HttpMethodMetadata(["POST"], acceptCorsPreflight: true)`. The unpin lets the relay answer on the `Http1` listener (8081) that the admin-api Ingress targets. The metadata makes the browser's CORS preflight select the relay; without it, the preflight hits gRPC's unimplemented-service catch-all, which is pinned to 8080 and gets a 404.
- **Helm.** The admin-api Ingress gets its `/v1/traces` → 8081 rule back.

**Tech stack:** .NET 10 minimal APIs (`Iverson.Server/Iverson.Api`), with tests in xUnit, FluentAssertions and `WebApplicationFactory` (`Iverson.Api.Tests`). Charts are on Helm 3.16.4; renders are checked with `kubeconform`.

---

## Global Constraints

- **Branch from LOCAL `main`, not `origin/main`.** Run `git worktree add .worktrees/traces-relay-admin-listener -b traces-relay-admin-listener main`. Local `main` is about 20 commits ahead of `origin/main`, including the admin-console merge `bc2be1c9`, so a worktree based on `origin/main` would lack every file this plan edits.
- **Never push, and never merge into `main`.** Integration is the user's decision.
- **Commits:** subjects are lowercase imperative with no type prefix, the body says why, and each ends with the executing agent's `Co-Authored-By:` trailer. Stage files by name, never `git add -A`.
- **Before every `helm template`,** run `helm dependency update .` in `Iverson.Server/deploy/helm/iverson`; otherwise packaged `.tgz` subcharts render stale.

## File Structure

- **Modify** `Iverson.Server/Iverson.Api/Program.cs`: the relay's comment block (starting at `:724`) and its registration line (`:769`).
- **Modify** `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`: add the real-endpoint gate test after `HealthListenerEndpoint_IsMarkedForHealthListenerPort`.
- **Modify** `Iverson.Server/Iverson.Api.Tests/AdminConsoleCorsPipelineTests.cs`: a third class fixture, plus the preflight test after `ConfiguredOrigin_PreflightOptions_AdminDlq_AnsweredByCorsNotFallbackPolicy`.
- **Modify** `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleCorsTestWebApplicationFactories.cs`: add `CorsConfiguredAdminListenerTestWebApplicationFactory`.
- **Modify** `Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml`: header comment `:14`, plus the restored rule.
- **Modify** `Iverson.Server/deploy/helm/iverson/charts/api/values.yaml`: the comment at `:45-46`.

## Inherited from spec

`thorough-brainstorming` and two CDR rounds verified these; they are not re-verified here. See the spec's "Verified assumptions" table for full evidence.

1. There is exactly one `RequireListenerPort(8080)` on the relay (`Program.cs:769`).
2. The endpoint a request selects passes the port gate on any listener when it is unpinned. A preflight selects the relay only with row 20's metadata (`ListenerPortGateAsync`, `Program.cs:793-803`).
3. Unpinned endpoints stay under both global limiters (`Program.cs:117`, `:461`).
4. The `traces` policy is attached to the endpoint and doesn't depend on the listener (`Program.cs:144-152`, keyed by `sub`).
5. JWT authentication doesn't depend on the listener (`:182`, `:206`, `:235`).
6. 8081 is `Http1` in every deployment (`appsettings.json`; there are no overrides).
7. The relay is mapped in the `api` role, and the api Service exposes 8081 (`Program.cs:715`; `charts/api/templates/service.yaml:13-14`).
8. The branch-tip rule restores cleanly (`87914794:…/admin-api-ingress.yaml:51-57`).
9. Browsers and ingress-nginx can't reach 8080 over HTTP/1.1 (Kestrel probe; `backend-protocol` appears only at `values-aws.yaml:91`).
10. Exporter transport and headers: `fetch`, `keepalive`, `mode: 'cors'`, `Content-Type: application/json`, and a bearer header.
11. Keepalive plus a CORS preflight works in current browsers (documentation tier).
12. Both CSPs allow the admin-api origin.
13. Compose wiring: ports 8080/8081, `Jaeger__OtlpHttpUrl`, and `AdminConsole__Origin=http://localhost:5173`.
14. Nothing asserts or blocks the pin; the NetworkPolicy admits ingress-nginx and `clusterCidrs` on 8081.
15. The console calls no other 8080-pinned route.
16. Laptop renders no admin-api Ingress.
17. Helm's Jaeger (`all-in-one:1.62.0`) accepts OTLP/HTTP on 4318, and the Service exposes 4318.
18. `Jaeger__OtlpHttpUrl` renders on local, aws, azure and gcp.
19. The api → Jaeger:4318 NetworkPolicy path exists.
20. The `/v1/traces` CORS preflight on 8081 reaches CORS only with the relay's preflight metadata.

## Verified plan-level assumptions

Every row below was checked on 2026-09-23 against `main` at `ebf00bf4`. Rows 4–13 were checked by applying this plan's exact code blocks in a throwaway worktree and running them.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Path / text | The relay's registration line is `    }).RequireAuthorization().RequireRateLimiting("traces").WithMetadata(new RequireListenerPort(8080));`, exactly once | `command grep -c` → `1` (`Program.cs:769`) |
| 2 | Symbol | `HttpMethodMetadata(IEnumerable<string>, bool acceptCorsPreflight)` resolves in `Program.cs` without a new `using` | `Iverson.Api.csproj`: `Sdk="Microsoft.NET.Sdk.Web"` with `ImplicitUsings` enabled, whose implicit usings include `Microsoft.AspNetCore.Routing`; the plan's code compiled |
| 3 | Path / text | The relay's comment block contains the line `    // Same-origin so the browser never needs Jaeger's own network address, and` once, and ends with `    // unbounded payload at Jaeger.` directly before `const long MaxTraceBodyBytes` | `Program.cs:724-741`, read |
| 4 | Symbol | `AuthenticationPipelineTests` has `_factory` (`AuthTestWebApplicationFactory`) and the usings needed for `RouteEndpoint`, `DefaultHttpContext`, `StatusCodes` and `GetRequiredService` | `:1-18` usings include `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing` and `Microsoft.Extensions.DependencyInjection`; `:32` field; the test compiled |
| 5 | Symbol | `RoutePattern.RawText == "/v1/traces"` matches exactly one endpoint | Precedent at `AuthenticationPipelineTests.cs:201-207`; the planned `.Single(...)` passed |
| 6 | Symbol | `AuthTestWebApplicationFactory` is non-sealed, and its `ConfigureWebHost` is `protected override`, so a subclass can extend it and call `base` | `Helpers/AuthTestWebApplicationFactory.cs:18`, `:44`; the planned factory compiled and ran |
| 7 | Code validity | The test project doesn't implicitly import ASP.NET namespaces, so the helper file needs explicit `Microsoft.AspNetCore.Builder`, `Microsoft.AspNetCore.Hosting` and `Microsoft.Extensions.DependencyInjection` usings | `Iverson.Api.Tests.csproj`: `Sdk="Microsoft.NET.Sdk"`; with the three usings it compiled |
| 8 | Code validity | An `IStartupFilter` registered in the factory's `ConfigureWebHost` stamps `LocalPort = 8081` before the listener gate | The preflight test returned 404 before the change (the gate on 8081) and 204 after it |
| 9 | Ordering / race | Only `AdminConsoleCorsPipelineTests` uses the CORS factories, so all three of its fixtures, the new one included, are built one after another by one class. The `AdminConsole__Origin` environment-variable write and `CreateClient()` happen together in each constructor, as the existing file documents | `command grep -rln` over the test project finds only `AdminConsoleCorsPipelineTests.cs` and the helpers file |
| 10 | Command | The focused filter selects exactly the three new test cases | `--list-tests` lists `TracesRelay_PassesTheListenerPortGate(8081)`, `(8080)` and `ConfiguredOrigin_PreflightOptions_TracesRelay_OnAdminListener_AnsweredByCors` |
| 11 | TDD | Before the change, the filter gives 2 failed and 1 passed: the gate on 8081 (`Expected nextInvoked to be True`) and the preflight (`… NoContent … but found … NotFound`). After it, 3/3 pass | Ran both, in the throwaway worktree |
| 12 | Mutation | Restoring the pin while keeping the metadata fails the 8081 gate case and the preflight. Removing the metadata while keeping the unpin fails only the preflight | Ran both |
| 13 | Consumer impact | The neighbouring classes still pass with the change: `AdminConsoleCorsPipelineTests`, `AuthenticationPipelineTests` and `TracesRelayEndpointTests` | Filter over the three classes → `Passed: 34, Failed: 0` |
| 14 | Consumer impact (totality) | No test asserts pins across all endpoints | `command grep -rn RequireListenerPort` in `Iverson.Api.Tests`: only the `/admin/dlq` (8080) and health-listener (8081) assertions, plus synthetic gate tests (`AuthenticationPipelineTests.cs:186-255`) |
| 15 | Consumer impact (totality) | Nothing checks the admin-api Ingress's path list | `command grep -rln admin-api` over `.github/`, `deploy/`, `scripts`: `admin-ui.yml` names only the origin (the CSP check); the chart and values files are the only others |
| 16 | Path / text | The rule restores verbatim at the end of the gate; the two comment texts match | `admin-api-ingress.yaml` ends with the `/health` rule's `number: 8081` and then `{{- end }}`; `:14` and `values.yaml:45-46` read as quoted in Task 2; the restored rule `diff`s clean against `87914794:…:51-57` |
| 17 | Command | The render loop, YAML check and kubeconform give the expected results | Ran them. `/v1/traces` routes: local, aws, azure, gcp → `t-admin-api:8081`, `t-api:8080`; laptop → `t-api:8080` only. `kubeconform` `Invalid: 0` on all five. `kubeconform` at `~/.local/bin`; PyYAML 6.0.3 |
| 18 | Setup | `.worktrees/` is gitignored, and there is no `traces-relay-admin-listener` branch yet | `git check-ignore` succeeds; `git branch --list` is empty |

## Tasks

### Task 1: The relay answers on the admin listener, preflight included

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (the comment block starting at `:724`; the registration at `:769`)
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleCorsTestWebApplicationFactories.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/AdminConsoleCorsPipelineTests.cs`

- [ ] **Step 1: Add the admin-listener test factory**

In `Helpers/AdminConsoleCorsTestWebApplicationFactories.cs`, replace the first line, `namespace Iverson.Api.Tests.Helpers;`, with:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Iverson.Api.Tests.Helpers;
```

and append at the end of the file:

```csharp

// The configured origin again, plus a startup filter that stamps every request's LocalPort as
// 8081 -- the Http1 listener the admin-api Ingress targets -- so ListenerPortGateAsync runs
// exactly as it does for a real browser request there (TestServer otherwise reports
// LocalPort 0, which the gate lets through). Built the same way as its siblings, and used only
// by AdminConsoleCorsPipelineTests, for the env-var reason documented above.
public sealed class CorsConfiguredAdminListenerTestWebApplicationFactory : AuthTestWebApplicationFactory
{
    public HttpClient Client { get; }

    public CorsConfiguredAdminListenerTestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("AdminConsole__Origin", CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);
        Client = CreateClient();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, AdminListenerPortStamp>());
    }

    private sealed class AdminListenerPortStamp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.LocalPort = 8081;
                return nextMiddleware();
            });
            next(app);
        };
    }
}
```

- [ ] **Step 2: Write the preflight test**

In `AdminConsoleCorsPipelineTests.cs`, add the third fixture to the class declaration, a field for it, and a constructor parameter:

```csharp
public class AdminConsoleCorsPipelineTests :
    IClassFixture<CorsConfiguredTestWebApplicationFactory>,
    IClassFixture<CorsDisabledTestWebApplicationFactory>,
    IClassFixture<CorsConfiguredAdminListenerTestWebApplicationFactory>
{
    private const string UnlistedOrigin = "https://evil.example";

    private readonly HttpClient _configured;
    private readonly HttpClient _disabled;
    private readonly HttpClient _configuredOnAdminListener;

    public AdminConsoleCorsPipelineTests(
        CorsConfiguredTestWebApplicationFactory configured,
        CorsDisabledTestWebApplicationFactory disabled,
        CorsConfiguredAdminListenerTestWebApplicationFactory configuredOnAdminListener)
    {
        _configured = configured.Client;
        _disabled = disabled.Client;
        _configuredOnAdminListener = configuredOnAdminListener.Client;
    }
```

Then add the test directly after `ConfiguredOrigin_PreflightOptions_AdminDlq_AnsweredByCorsNotFallbackPolicy`:

```csharp

    // The browser's span export is a cross-origin POST to /v1/traces on the admin-api host,
    // which the admin-api Ingress sends to the Http1 listener (8081), so its preflight must be
    // answered by CORS there. A MapPost endpoint does not accept preflights: without the relay's
    // HttpMethodMetadata(acceptCorsPreflight: true) the preflight selects gRPC's
    // unimplemented-service catch-all, which is pinned to 8080, and the listener gate answers
    // 404 (docs/specs/2026-09-23-traces-relay-admin-listener-design.md).
    [Fact]
    public async Task ConfiguredOrigin_PreflightOptions_TracesRelay_OnAdminListener_AnsweredByCors()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/traces");
        request.Headers.Add("Origin", CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await _configuredOnAdminListener.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowOrigin).Should().BeTrue();
        allowOrigin!.Should().ContainSingle().Which.Should().Be(CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);
    }
```

- [ ] **Step 3: Write the real-endpoint gate test**

In `AuthenticationPipelineTests.cs`, add directly after `HealthListenerEndpoint_IsMarkedForHealthListenerPort`:

```csharp

    [Theory]
    // The browser posts spans to the admin-api host, which the admin-api Ingress sends to the
    // Http1 listener (8081) -- 8080 is h2c-only, which no browser speaks -- while main's api
    // Ingress still routes /v1/traces to 8080. So the relay must pass the gate on both
    // listeners (docs/specs/2026-09-23-traces-relay-admin-listener-design.md).
    [InlineData(8081)]
    [InlineData(8080)]
    public async Task TracesRelay_PassesTheListenerPortGate(int localPort)
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var relay = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/v1/traces");
        var context = new DefaultHttpContext { Connection = { LocalPort = localPort } };
        context.SetEndpoint(relay);
        var nextInvoked = false;
        Task Next() { nextInvoked = true; return Task.CompletedTask; }

        await Program.ListenerPortGateAsync(context, Next);

        nextInvoked.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }
```

- [ ] **Step 4: Watch the new tests fail**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests --nologo --filter "FullyQualifiedName~TracesRelay_PassesTheListenerPortGate|FullyQualifiedName~PreflightOptions_TracesRelay"
```

Expected: `Failed: 2, Passed: 1`.
- `TracesRelay_PassesTheListenerPortGate(localPort: 8081)` fails with `Expected nextInvoked to be True, but found False`.
- The preflight test fails with `Expected response.StatusCode to be HttpStatusCode.NoContent … but found HttpStatusCode.NotFound`.
- The 8080 case passes.

- [ ] **Step 5: Change the relay**

In `Iverson.Server/Iverson.Api/Program.cs`, make three edits.

(a) In the relay's comment block, change the second line from

```csharp
    // Same-origin so the browser never needs Jaeger's own network address, and
```

to

```csharp
    // Served by the API so the browser never needs Jaeger's own network address, and
```

(b) Extend the same block. Replace

```csharp
    // unbounded payload at Jaeger.
    const long MaxTraceBodyBytes
```

with

```csharp
    // unbounded payload at Jaeger.
    //
    // Deliberately NOT pinned to a listener, and accepting CORS preflights. The admin console
    // calls this cross-origin on its admin-api host, which the admin-api Ingress sends to the
    // Http1 listener (8081); 8080 is h2c-only, which no browser can speak. A MapPost endpoint
    // does not accept preflights, so without the HttpMethodMetadata below the browser's
    // OPTIONS /v1/traces would select gRPC's unimplemented-service catch-all, which carries
    // the gRPC services' RequireListenerPort(8080), and the listener gate would answer it 404
    // on 8081. Authentication, the "traces" per-user policy and both global limiters still
    // apply: the limiters exempt only endpoints pinned to 8081.
    const long MaxTraceBodyBytes
```

(c) Replace the registration line

```csharp
    }).RequireAuthorization().RequireRateLimiting("traces").WithMetadata(new RequireListenerPort(8080));
```

with

```csharp
    }).RequireAuthorization().RequireRateLimiting("traces")
        .WithMetadata(new HttpMethodMetadata(new[] { "POST" }, acceptCorsPreflight: true));
```

- [ ] **Step 6: Watch them pass, then prove each change is load-bearing**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests --nologo --filter "FullyQualifiedName~TracesRelay_PassesTheListenerPortGate|FullyQualifiedName~PreflightOptions_TracesRelay"
```

Expected: `Passed: 3`.

Run two temporary mutations of the edit (c) line, re-running the same filter each time and restoring the file afterwards. After both, `git diff` must show exactly edits (a)–(c).
1. **Pin restored:** append `.WithMetadata(new RequireListenerPort(8080))` after the `HttpMethodMetadata` call. Expected: `Failed: 2`, the 8081 gate case and the preflight.
2. **Metadata removed:** delete the `.WithMetadata(new HttpMethodMetadata(...))` line and end the previous line with `;`. Expected: `Failed: 1`, the preflight only.

- [ ] **Step 7: Run the neighbours, the build and the full suite**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests --nologo --filter "FullyQualifiedName~AdminConsoleCorsPipelineTests|FullyQualifiedName~AuthenticationPipelineTests|FullyQualifiedName~TracesRelayEndpointTests"
dotnet build Iverson.slnx --nologo 2>&1 | grep -E "^\s*[0-9]+ Error\(s\)"
dotnet test Iverson.Server/Iverson.Api.Tests --nologo 2>&1 | grep -E "^\s+Failed |Failed!|Passed!"
```

Expected:
- neighbours: `Passed: 34, Failed: 0`;
- build: `0 Error(s)`;
- full suite: `Failed: 0`, with the total 3 higher than `main`'s (1172 at `bc2be1c9`, so 1175). It takes about 7 minutes.

Any failure is a defect to report, not a flake to re-run away.

- [ ] **Step 8: Commit**

```bash
git add Iverson.Server/Iverson.Api/Program.cs \
        Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs \
        Iverson.Server/Iverson.Api.Tests/AdminConsoleCorsPipelineTests.cs \
        Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleCorsTestWebApplicationFactories.cs
git commit -F - <<'EOF'
let the traces relay answer on the admin listener, preflight included

The console posts spans cross-origin to the admin-api host, which reaches the Http1 listener
(8081); the relay was pinned to the h2c-only 8080, and its preflight fell through to the gRPC
catch-all pinned there. Unpinning it and accepting CORS preflights lets the export through
with authentication and rate limits unchanged.

Co-Authored-By: <executing agent's trailer>
EOF
```

### Task 2: Restore the admin-api Ingress rule for the relay

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml` (`:14`, end of file)
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/values.yaml` (`:45-46`)

- [ ] **Step 1: Restore the rule and correct the path lists**

(a) In `admin-api-ingress.yaml`, change the header line

```
  Why guard at all: this Ingress publishes /admin and /health on a public
```

to

```
  Why guard at all: this Ingress publishes /admin, /health and /v1/traces on a public
```

(b) The file ends with the `/health` rule's `number: 8081`, followed by `{{- end }}`. Insert the rule from `87914794` `:51-57` verbatim between them:

```yaml
          - path: /v1/traces
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-api
                port:
                  number: 8081
```

(c) In `charts/api/values.yaml`, replace the two lines

```
# not render, so a console-less profile does not expose /admin and /health (ANONYMOUS) on a
# public hostname for nobody to call.
```

with

```
# not render, so a console-less profile does not expose /admin, /health (ANONYMOUS) and
# /v1/traces on a public hostname for nobody to call.
```

- [ ] **Step 2: Render every profile and check the routes**

```bash
cd Iverson.Server/deploy/helm/iverson && helm dependency update . >/dev/null
for p in local laptop aws azure gcp; do
  extra=""; [ -f "values-$p.ci-override.yaml" ] && extra="-f values-$p.ci-override.yaml"
  helm template t . -f "values-$p.yaml" $extra > "/tmp/traces-render-$p.yaml" || { echo "RENDER FAILED: $p"; exit 1; }
  printf '%s kubeconform %s\n' "$p" "$(kubeconform -ignore-missing-schemas -summary /tmp/traces-render-$p.yaml 2>&1 | grep -o 'Invalid: [0-9]*')"
done
python3 - <<'PY'
import yaml
for p in ["local", "laptop", "aws", "azure", "gcp"]:
    routes = sorted(
        f'{d["metadata"]["name"]}:{path["backend"]["service"]["port"]["number"]}'
        for d in yaml.safe_load_all(open(f"/tmp/traces-render-{p}.yaml"))
        if d and d.get("kind") == "Ingress"
        for r in d["spec"]["rules"] for path in r["http"]["paths"] if path["path"] == "/v1/traces")
    print(p, routes)
PY
cd -
```

Expected:
- every render exits 0;
- `kubeconform Invalid: 0` on all five profiles;
- `/v1/traces` routes:
  - `local`, `aws`, `azure` and `gcp` show `['t-admin-api:8081', 't-api:8080']`;
  - `laptop` shows `['t-api:8080']` only.

If `helm dependency update` modifies tracked files, restore them with `git checkout -- <path>` before committing.

- [ ] **Step 3: Commit**

```bash
git add Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml \
        Iverson.Server/deploy/helm/iverson/charts/api/values.yaml
git commit -F - <<'EOF'
restore the admin-api ingress rule for the traces relay

The relay now answers on the Http1 listener, so the admin-api host routes /v1/traces to 8081
again, the rule the admin-console merge dropped while the relay was 8080-only.

Co-Authored-By: <executing agent's trailer>
EOF
```

## Known issues inherited from spec

- **Threat model (the user's decision: note it, don't edit).** The uncommitted rewrite of `docs/security/tma.md` in the main checkout adds a "Two-listener Kestrel topology" section. That section will be false after this change, and is already partly false after the admin-console merge, which serves authenticated `/admin/console/*` on 8081 through the admin-api Ingress. Its claims:
  - `/v1/traces` is served on `:8080` only;
  - `:8081` is health/observability, "passive-read-only", with "no writes reachable".

  After this change, `/v1/traces` (an authenticated POST relayed to Jaeger) answers on both listeners. Whoever finishes that rewrite should reflect both facts. This work does not touch `tma.md`.
- **Main's api Ingress on the nginx profiles forwards HTTP/1.1 to the h2c-only 8080.** No chart value sets `backend-protocol: GRPC` outside AWS, so per the probe above, everything that Ingress routes to 8080 on local should get a 400, gRPC included. This is pre-existing on main and outside this design's path.
- **UNVERIFIED, and moot:** how an AWS ALB `GRPC` target group treats a non-gRPC POST. It only mattered for option C, which fails on compose and nginx regardless.

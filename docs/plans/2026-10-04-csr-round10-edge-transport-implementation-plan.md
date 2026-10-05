# CSR Round 10 — Edge and Transport Remediation (Sub-project B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-04-csr-round10-edge-transport-design.md` (commit SHA: `31a91d6e`)

**Goal:** close CSR round-10 #3 (the rate limiters key on the proxy's IP), #5 for the API→Authentik hop (plaintext discovery, JWKS and admin-token traffic), and #21 (quadratic enrichment regex, uncapped response body).

**Architecture:**
- **#3.** A limiter-local `ClientPartitionKey` reads `X-Forwarded-For` only from per-profile trusted proxy CIDRs, at a per-profile hop count.
- **#5.**
  - An nginx sidecar terminates TLS on 8443 in front of Authentik. Helm generates its certificate, and compose's secrets script generates the compose copy.
  - The API fetches metadata over https and trusts only that CA. It still accepts the in-cluster `http://…:9000/` issuer through a new `InternalIssuer` setting.
  - Outside Development, the api role refuses to start without TLS settings.
- **#21.** `NonBacktracking`, plus a body read bounded in both size and time.

**Tech stack:** .NET 10 (ASP.NET Core, `System.Threading.RateLimiting`, JwtBearer), xUnit + FluentAssertions + NSubstitute, Helm 3.16 (CI 3.15), nginx-unprivileged 1.27, docker compose on podman, kind with Calico.

---

## Global Constraints

Verbatim from the spec, plus execution rules from the conversation:
- **One branch, one merge.** No proto change, no SDK change.
- **Security write-ups** (comments, test names, docs) stay at the level of conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored**, so commit docs files with `git add -f`.
- **The user's running compose stack** (`iversonserver` containers, volumes, networks, images) is never started, stopped or modified. Live checks use isolated compose projects (`-p <name>`, `--project-directory`, `container_name:` stripped, `down -v`) and a throwaway kind cluster that the check creates and deletes.
  - The user's image tags `iverson-api:latest` and `iverson-api:0.1.0` are never rebuilt or retagged. Live checks build under the tags `csr10edge-api` (compose) and `iverson-api:csr10edge` (kind), and remove them at teardown.
- **Compose secrets** are read from `Iverson.Server/.env` and never printed. Task 7 reads only the worktree's own freshly generated `.env`, never the main checkout's.
- **Process limits:**
  - One `dotnet` process at a time (`pgrep -a dotnet` first).
  - Use absolute paths, because the shell's working directory resets between calls.
  - Never `pkill -f`; kill by PID.
- **Before any `helm template` or `helm lint` in the worktree,** run `helm dependency build Iverson.Server/deploy/helm/iverson`. Otherwise the stale `charts/*.tgz` shadow edits to the subcharts.
- **Commit messages** are lowercase imperative sentences with no prefix, ending with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Never stage anything under `.superpowers/`.

**Names used below:**
- `MAIN=/home/ben/repositories/Iverson`
- `BR=$MAIN/.worktrees/csr10-edge-transport`, the worktree on branch `csr10-edge-transport`, cut from local `main`
- `SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad`

---

## File Structure

**Create:**
- `Iverson.Server/Iverson.Api/RateLimiting/TrustedProxyOptions.cs`: binds and validates `RateLimiting:TrustedProxies`.
- `Iverson.Server/Iverson.Api/RateLimiting/ClientPartitionKey.cs`: the client address the limiters partition on.
- `Iverson.Server/Iverson.Api/Tenancy/AuthentikTrust.cs`: the CA-only trust handler and the api-role startup guard.
- `Iverson.Server/Iverson.Api.Tests/RateLimiting/ClientPartitionKeyTests.cs`: key and partition tests.
- `Iverson.Server/Iverson.Api.Tests/RateLimiting/TrustedProxyOptionsTests.cs`: configuration validation tests.
- `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs`: trust handler against a local Kestrel TLS server, the startup guard, and the JwtBearer wiring.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-internal-tls.yaml`: the generated internal certificate and CA.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap-tls-proxy.yaml`: the sidecar's `default.conf`.
- `Iverson.Server/deploy/compose-tls-proxy.conf`: compose's copy of the same `default.conf`. Helm cannot read files outside the chart.

**Modify:**
- `Iverson.Server/Iverson.Api/Program.cs`:
  - the two limiters move into static partition functions;
  - the trusted-proxy binding;
  - the Authentik TLS wiring;
  - the startup guard.
- `Iverson.Server/Iverson.Embeddings/EnrichmentService.cs`: the regex options, and the bounded, timed body read.
- `Iverson.Server/Iverson.Embeddings.Tests/EnrichmentServiceTests.cs`: new tests.
- `Iverson.Server/deploy/helm/iverson/charts/api/values.yaml` and `charts/api/templates/deployment.yaml`: `trustedProxies`; the Authentik env vars and CA volume.
- `Iverson.Server/deploy/helm/iverson/values-{local,laptop,aws,azure,gcp}.yaml`: `api.trustedProxies`.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml`, `templates/deployment-server.yaml` and `templates/service.yaml`: the sidecar and port 8443.
- `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml`: the two Authentik rules move from 9000 to 8443.
- `scripts/generate-compose-secrets.sh`, `.gitignore`, `Iverson.Server/docker-compose.yml`: compose TLS.

---

## Inherited from spec

The spec's `Verified assumptions` rows 1–43 were verified by `thorough-brainstorming`, CDR-1 and CDR-2. They are trusted as ground truth here and not re-verified. They cover:

| Rows | Subject |
|---|---|
| 1–2 | The `RemoteIpAddress` sites; why the pre-auth limiter stays before authentication |
| 3–7 | `X-Forwarded-For` parsing and the vendor header formats |
| 8–11, 37 | kind, laptop, AWS and Azure peer ranges |
| 12–13 | The `fail` idiom; the CI lint loop |
| 14–19 | Authentik native TLS versus the sidecar; Authentik's trusted proxies and ports; nginx as uid 1000 read-only; the pod's uid and fsGroup |
| 20 | The AdminUI digest |
| 21 | `genCA` and the lookup idiom |
| 22 | NetworkPolicy rules |
| 23–26 | Issuer mode; JwtBearer's merging of the metadata issuer and its https enforcement; ActingUser's empty `ValidIssuers` |
| 27–30 | The worker and compose settings; the test environment; the config consumers |
| 31 | `network_mode: service:` |
| 32 | The secrets script |
| 33–36 | NonBacktracking; transient classification; the enrichment client; `IPNetwork` |
| 38–43 | IPv4-mapped peers; custom-root chains against `genCA` output; recovery links; TestServer has no peer address; `ResponseHeadersRead` timeout behaviour; the internal Secret name |

---

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Api/RateLimiting/` is new; `Iverson.Api/Tenancy/` exists; `Iverson.Api.Tests/` has `Tenancy/` and `Helpers/` | `ls Iverson.Server/Iverson.Api` (no `RateLimiting`); `ls Iverson.Server/Iverson.Api.Tests` |
| 2 | File path | The worktree directory `.worktrees` exists and is gitignored | `ls -d $MAIN/.worktrees`; `git check-ignore -q .worktrees` → ignored |
| 3 | Signature | `Program` is a `public partial class` with `internal static` helpers, and `Iverson.Api.Tests` sees internals | `Program.cs:841-879` (`RejectRevokedTokenAsync`, `ListenerPortGateAsync`); `Iverson.Api.csproj:10-12` `InternalsVisibleTo Iverson.Api.Tests` |
| 4 | Signature | `cfg`, `workloadRole` and `builder.Environment` are in scope before the rate limiter and JwtBearer registration | `Program.cs:28-38` (`cfg`, `workloadRole`); `:47` uses `builder.Environment`; `AddRateLimiter` at `:103`, `AddJwtBearer` at `:181` |
| 5 | Signature | The post-auth limiter is `options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => …)` at `Program.cs:110-137`; the pre-auth one is `preAuthOptions.GlobalLimiter = …` at `:489-512` | Read of `Program.cs:103-153`, `:486-520` |
| 6 | Signature | `EnrichmentServiceOptions.Timeout` is a `TimeSpan`, default 2 min | `Iverson.Embeddings/EnrichmentServiceOptions.cs` |
| 7 | Signature | Embeddings tests build `EnrichmentService` through an NSubstitute `IHttpClientFactory` and a `FakeHttpMessageHandler(HttpResponseMessage)`; `Iverson.Embeddings` has no `InternalsVisibleTo`, so tests go through `GenerateAsync` and `GenerateJsonAsync` | `EnrichmentServiceTests.cs:14-53`; `command grep InternalsVisibleTo Iverson.Embeddings/*.csproj` → no hits |
| 8 | Consumer | `EnrichmentConsumer`'s skip path for `TaskCanceledException(inner TimeoutException)` is already pinned by a test, so Task 3 adds only the service-level test | `Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs:545-553` |
| 9 | Signature | Tests stamp the connection through an `IStartupFilter`, and per-test configuration goes through `WithWebHostBuilder`/`UseSetting` | `Helpers/AdminConsoleCorsTestWebApplicationFactories.cs:82-85`; `Grpc/TokenRevocationPipelineTests.cs:38` |
| 10 | Code validity | `IPEndPoint.TryParse` accepts `203.0.113.7`, `203.0.113.7:51234`, `2001:db8::1`, `[2001:db8::1]:443` and `::ffff:203.0.113.7`, and rejects `not-an-ip` and `…:99999`; `IPNetwork.Contains` accepts a mapped address; `IPNetwork.TryParse` rejects `bogus` and `/33` | Probed on .NET 10 (`scratchpad/probe-ipparse`) |
| 11 | Code validity | `RateLimitPartition<string>.PartitionKey` is readable without invoking the limiter factory | `System.Threading.RateLimiting` public struct member; used in Task 1 tests |
| 12 | Command | There is no committed Helm render-test harness. CI runs `helm lint`, `helm template \| kubeconform` and `kube-score` over the 5 profiles with their ci-overrides | `.github/workflows/deploy-validate.yml:16-130`; `command grep -rln "helm template"` finds only CI files and `setup.sh` |
| 13 | Command | `kube-score` 1.19.0 and `kubeconform` are installed locally. `kube-score` already exits 1 on `main`: 12 CRITICAL lines on values-local, including `iverson-authentik-server` with ReadOnlyRootFilesystem and ephemeral-storage | `which kube-score kubeconform`; a baseline run on a scratch copy of the chart (`scratchpad/helmbase`) |
| 14 | Consumer | Every profile enables the authentik subchart: `values.yaml` sets `authentik.enabled: true` and no overlay overrides it | awk over `values*.yaml` |
| 15 | Consumer | No test or Development config depends on Program's `Authentik:BaseUrl` fallback; `appsettings*.json` leaves `Authentication:Authority` empty | `AuthentikAdminClientTests.cs:59-73` and `AuthentikRecoveryFlowIntegrationTests.cs:62-72` build their own clients; `appsettings.json:31-37`; `appsettings.Development.json` has no Authentication |
| 16 | Consumer | The Launcher starts only named infrastructure services (no Authentik) and runs the API in Development | `Iverson.Launcher/Program.cs:18-23` |
| 17 | Consumer | The compose `iverson-api` runs as `user: "1000:1000"` with no volumes; `iverson-worker` mirrors its Authentik env at worker-block lines 31, 38, 40 and 41 | `docker-compose.yml:445-560` |
| 18 | Code validity | `curl` 8.12.1 with OpenSSL is in `nginxinc/nginx-unprivileged:1.27-alpine` | `docker run --rm --entrypoint sh … -c 'curl --version'` |
| 19 | Command | E's live-check procedure is reusable: strip `container_name`, rename the api image, `--project-directory $BR/Iverson.Server`, `--env-file`, health via `docker inspect`, conformance `dotnet run -- --scenarios identity` | `docs/plans/2026-10-04-csr-round10-tooling-implementation-plan.md:1694-1890` |
| 20 | Consumer | The user owns image tags `iverson-api:latest` and `iverson-api:0.1.0`, and `build-and-load-image.sh` defaults to tag `0.1.0`, so Task 7 must pass its own tag | `podman images`; `build-and-load-image.sh:10-13,42` |
| 21 | Consumer | This machine has 9 GB of RAM and 4 CPUs; `values-local` needs 16 GB or more, and `values-laptop` (no StarRocks) fits; podman already has `pids_limit = -1` | `free -g`; `nproc`; `values-local.yaml:1`; `values-laptop.yaml:1-2,22`; `~/.config/containers/containers.conf:7` |
| 22 | Command | kind clusters are created by the operator (`setup.sh` doesn't create one) with the default name `iverson`; kind maps 127.0.0.1:8080 and 8443 to ingress 80 and 443; no kind cluster exists now | `build-and-load-image.sh:13`; `kind-config.yaml:12-24`; `kind get clusters` → none |
| 23 | Signature | The admin-automation client Secret is `<release>-authentik-admin-automation-client`, with keys `client-id` and `client-secret` | `charts/authentik/templates/secret-service-clients.yaml:141-149` |
| 24 | Code validity | `/iverson.ObjectMappingService/GetSchema` is a real, ingress-routed RPC | `object_mapping.proto:3,15`; `charts/api/templates/ingress.yaml:54` |
| 25 | Ordering | Task 2 consumes Task 1's key names (`RateLimiting__TrustedProxies__*`); Tasks 5 and 6 consume Task 4's setting names; Task 3 is independent; Task 7 needs all | By construction (names are defined in Tasks 1 and 4) |
| 26 | Consumer | The API Helm deployment mounts single Secret keys with `items:` and names env vars `Authentication__*` and `Authentik__*` at `deployment.yaml:137-176` | Read of `charts/api/templates/deployment.yaml:130-250` |
| 27 | Consumer | The authentik subchart's resources live at `.Values.resources.server/worker`, and values-laptop overrides only those two keys | `charts/authentik/values.yaml:1-8`; `values-laptop.yaml:111-118` |
| 28 | Consumer | WebApplicationFactory settings (`UseSetting`, `ConfigureAppConfiguration`) arrive too late for configuration Program reads before `Build()`. So the guard runs after `Build()` on `app.Configuration`/`app.Environment`, and Authentik settings are read inside the options and HttpClient lambdas | `Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs:37-46` (the Qdrant `ApiKey` env-var workaround and its comment) |
| 29 | Command | No project treats warnings as errors, so an analyzer warning on the new X509 calls does not break the build | `command grep -rn TreatWarningsAsErrors\|WarningsAsErrors` over `Iverson.Server` `*.props`/`*.csproj` → no hits; no `Directory.Build.props` |

---

## Tasks

### Task 1: Rate-limiter client key

**Files:**
- Create: `Iverson.Server/Iverson.Api/RateLimiting/TrustedProxyOptions.cs`, `Iverson.Server/Iverson.Api/RateLimiting/ClientPartitionKey.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (usings; after `:38`; `:110-137`; `:489-512`; the partial class at `:841`)
- Test: `Iverson.Server/Iverson.Api.Tests/RateLimiting/ClientPartitionKeyTests.cs`, `Iverson.Server/Iverson.Api.Tests/RateLimiting/TrustedProxyOptionsTests.cs`

**Interfaces:**
- Produces:
  - the configuration keys `RateLimiting:TrustedProxies:Cidrs:<i>` and `RateLimiting:TrustedProxies:Hops`;
  - `TrustedProxyOptions.FromConfiguration(IConfiguration)`;
  - `ClientPartitionKey.For(HttpContext, TrustedProxyOptions)`;
  - `Program.PreAuthPartition`/`PostAuthPartition(HttpContext, TrustedProxyOptions)`.

- [ ] **Step 0: Create the worktree** (this task only)

```bash
git -C /home/ben/repositories/Iverson worktree add .worktrees/csr10-edge-transport -b csr10-edge-transport main
```

- [ ] **Step 1: Write the failing tests**

`Iverson.Api.Tests/RateLimiting/TrustedProxyOptionsTests.cs`:

```csharp
using FluentAssertions;
using Iverson.Api.RateLimiting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Iverson.Api.Tests.RateLimiting;

public class TrustedProxyOptionsTests
{
    internal static IConfiguration Config(int? hops, params string[] cidrs)
    {
        var values = new Dictionary<string, string?>();
        if (hops is not null)
            values["RateLimiting:TrustedProxies:Hops"] = hops.Value.ToString();
        for (var i = 0; i < cidrs.Length; i++)
            values[$"RateLimiting:TrustedProxies:Cidrs:{i}"] = cidrs[i];
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void FromConfiguration_AcceptsAnEmptyList_AndDefaultsHopsToOne()
    {
        var opts = TrustedProxyOptions.FromConfiguration(Config(null));

        opts.Networks.Should().BeEmpty();
        opts.Hops.Should().Be(1);
    }

    [Fact]
    public void FromConfiguration_RejectsAnEntryThatIsNotACidr() =>
        FluentActions.Invoking(() => TrustedProxyOptions.FromConfiguration(Config(1, "10.244.0.0/16", "bogus")))
            .Should().Throw<InvalidOperationException>().WithMessage("*RateLimiting:TrustedProxies:Cidrs*bogus*");

    [Fact]
    public void FromConfiguration_RejectsHopsBelowOne() =>
        FluentActions.Invoking(() => TrustedProxyOptions.FromConfiguration(Config(0, "10.244.0.0/16")))
            .Should().Throw<InvalidOperationException>().WithMessage("*RateLimiting:TrustedProxies:Hops*");
}
```

`Iverson.Api.Tests/RateLimiting/ClientPartitionKeyTests.cs`:

```csharp
using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.RateLimiting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Iverson.Api.Tests.RateLimiting;

public class ClientPartitionKeyTests
{
    private static TrustedProxyOptions Opts(int hops, params string[] cidrs) =>
        TrustedProxyOptions.FromConfiguration(TrustedProxyOptionsTests.Config(hops, cidrs));

    private static HttpContext Ctx(string? peer, string? xff)
    {
        var ctx = new DefaultHttpContext();
        if (peer is not null)
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (xff is not null)
            ctx.Request.Headers["X-Forwarded-For"] = xff;
        return ctx;
    }

    [Theory]
    [InlineData("10.244.3.4", "203.0.113.7", 1, "203.0.113.7")]                // trusted peer, one entry
    [InlineData("10.244.3.4", "198.51.100.9, 203.0.113.7", 1, "203.0.113.7")] // the proxy's own entry wins over a client-supplied one
    [InlineData("10.244.3.4", "203.0.113.7, 34.120.1.1", 2, "203.0.113.7")]   // client second from the right
    [InlineData("10.244.3.4", "203.0.113.7:51234", 1, "203.0.113.7")]         // ip:port
    [InlineData("10.244.3.4", "2001:db8::1", 1, "2001:db8::1")]               // IPv6
    [InlineData("10.244.3.4", "[2001:db8::1]:443", 1, "2001:db8::1")]         // IPv6 with a port
    [InlineData("192.0.2.50", "203.0.113.7", 1, "192.0.2.50")]                // untrusted peer keeps its own address
    [InlineData("10.244.3.4", null, 1, "10.244.3.4")]                         // no header
    [InlineData("10.244.3.4", "203.0.113.7", 2, "10.244.3.4")]                // fewer entries than hops
    [InlineData("10.244.3.4", "not-an-ip", 1, "10.244.3.4")]                  // malformed entry
    [InlineData("::ffff:10.244.3.4", "203.0.113.7", 1, "203.0.113.7")]        // IPv4-mapped peer is matched as IPv4
    [InlineData("::ffff:192.0.2.50", null, 1, "192.0.2.50")]                  // and keyed as IPv4
    public void For_ReturnsTheClientAddress(string peer, string? xff, int hops, string expected) =>
        ClientPartitionKey.For(Ctx(peer, xff), Opts(hops, "10.244.0.0/16")).Should().Be(expected);

    [Fact]
    public void For_ReturnsAnon_WhenThereIsNoPeerAddress() =>
        ClientPartitionKey.For(Ctx(null, "203.0.113.7"), Opts(1, "10.244.0.0/16")).Should().Be("anon");

    [Fact]
    public void For_NeverTrustsTheHeader_WhenNoProxyIsConfigured() =>
        ClientPartitionKey.For(Ctx("10.244.3.4", "203.0.113.7"), Opts(1)).Should().Be("10.244.3.4");

    [Fact]
    public void PreAuthPartition_PutsTwoClientsBehindOneTrustedProxyInDifferentPartitions()
    {
        var opts = Opts(1, "10.244.0.0/16");

        var first  = Program.PreAuthPartition(Ctx("10.244.3.4", "203.0.113.7"), opts).PartitionKey;
        var second = Program.PreAuthPartition(Ctx("10.244.3.4", "203.0.113.8"), opts).PartitionKey;

        first.Should().Be("203.0.113.7");
        second.Should().Be("203.0.113.8");
    }

    [Fact]
    public void PreAuthPartition_KeepsAnUntrustedPeerInItsOwnPartition_WhateverItsHeaderSays()
    {
        var opts = Opts(1, "10.244.0.0/16");

        Program.PreAuthPartition(Ctx("192.0.2.50", "203.0.113.7"), opts).PartitionKey.Should().Be("192.0.2.50");
        Program.PreAuthPartition(Ctx("192.0.2.50", "203.0.113.8"), opts).PartitionKey.Should().Be("192.0.2.50");
    }

    [Fact]
    public void PostAuthPartition_FallsBackToTheClientAddress_WhenThereIsNoSub() =>
        Program.PostAuthPartition(Ctx("10.244.3.4", "203.0.113.7"), Opts(1, "10.244.0.0/16"))
            .PartitionKey.Should().Be("203.0.113.7");

    [Fact]
    public void PostAuthPartition_PrefersTheSubClaim()
    {
        var ctx = Ctx("10.244.3.4", "203.0.113.7");
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test"));

        Program.PostAuthPartition(ctx, Opts(1, "10.244.0.0/16")).PartitionKey.Should().Be("user-1");
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail to compile** (the types and methods don't exist yet)

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport && dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.RateLimiting"
```

Expected: build errors naming `TrustedProxyOptions`, `ClientPartitionKey` and `Program.PreAuthPartition`.

- [ ] **Step 3: Implement**

`Iverson.Api/RateLimiting/TrustedProxyOptions.cs`:

```csharp
using System.Net;

namespace Iverson.Api.RateLimiting;

/// <summary>
/// CSR round-10 #3: the load-balancer or ingress ranges whose X-Forwarded-For the rate limiters
/// trust, and which entry, counted from the right, holds the client. Empty means the header is
/// never trusted (compose, Development): those clients connect to the API directly.
/// </summary>
public sealed class TrustedProxyOptions
{
    public const string Section = "RateLimiting:TrustedProxies";

    public string[] Cidrs { get; set; } = [];
    public int Hops { get; set; } = 1;

    internal IReadOnlyList<IPNetwork> Networks { get; private set; } = [];

    public static TrustedProxyOptions FromConfiguration(IConfiguration cfg)
    {
        var opts = cfg.GetSection(Section).Get<TrustedProxyOptions>() ?? new TrustedProxyOptions();
        if (opts.Hops < 1)
            throw new InvalidOperationException($"{Section}:Hops must be at least 1, got {opts.Hops}.");

        var networks = new List<IPNetwork>(opts.Cidrs.Length);
        foreach (var cidr in opts.Cidrs)
        {
            if (!IPNetwork.TryParse(cidr, out var network))
                throw new InvalidOperationException($"{Section}:Cidrs contains '{cidr}', which is not a CIDR.");
            networks.Add(network);
        }
        opts.Networks = networks;
        return opts;
    }
}
```

`Iverson.Api/RateLimiting/ClientPartitionKey.cs`:

```csharp
using System.Net;

namespace Iverson.Api.RateLimiting;

/// <summary>
/// CSR round-10 #3: the client address the rate limiters partition on. Behind an ingress or load
/// balancer the TCP peer is the proxy, so every client would share one partition. When the peer
/// is a trusted proxy, the client is the X-Forwarded-For entry <see cref="TrustedProxyOptions.Hops"/>
/// positions from the right: the entry the proxy itself wrote, never one the client supplied.
/// Anything else (untrusted peer, no header, too few entries, unparseable entry) keys on the peer.
/// </summary>
public static class ClientPartitionKey
{
    public static string For(HttpContext ctx, TrustedProxyOptions opts)
    {
        var peer = ctx.Connection.RemoteIpAddress;
        if (peer is null)
            return "anon";
        if (peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();

        if (!opts.Networks.Any(network => network.Contains(peer)))
            return peer.ToString();

        // Multiple X-Forwarded-For header lines arrive joined by commas, the same as one list.
        var entries = ctx.Request.Headers["X-Forwarded-For"].ToString()
            .Split(',', StringSplitOptions.TrimEntries);
        if (entries.Length < opts.Hops || !IPEndPoint.TryParse(entries[^opts.Hops], out var client))
            return peer.ToString();

        var address = client.Address.IsIPv4MappedToIPv6 ? client.Address.MapToIPv4() : client.Address;
        return address.ToString();
    }
}
```

`Program.cs`:
1. Add `using Iverson.Api.RateLimiting;` to the usings.
2. After the `workloadRole` check (`:37-38`), add:

   ```csharp
   // CSR round-10 #3: which peers may vouch for a client address through X-Forwarded-For.
   var trustedProxies = TrustedProxyOptions.FromConfiguration(cfg);
   ```

3. Replace the post-auth `options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => { … });` block (`:110-137`) with:

   ```csharp
   options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => PostAuthPartition(ctx, trustedProxies));
   ```

4. Replace the pre-auth `preAuthOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => { … });` block (`:489-512`) with:

   ```csharp
   preAuthOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => PreAuthPartition(ctx, trustedProxies));
   ```

5. Add to the `public partial class Program` block the two lambda bodies, moved verbatim with their comments. The only change in each is the IP key:

   ```csharp
   /// <summary>The post-auth global limiter's partition (CSR round-7 #7, round-10 #3).</summary>
   internal static RateLimitPartition<string> PostAuthPartition(HttpContext ctx, TrustedProxyOptions trustedProxies)
   {
       // <the exclusion comment block moved from the old lambda, unchanged>
       var isHealthListenerEndpoint = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>()?.Port == 8081;
       var isGrpcCall = ctx.Request.ContentType?.StartsWith("application/grpc") == true;
       if (isHealthListenerEndpoint || isGrpcCall)
           // <the constant-key comment block moved from the old lambda, unchanged>
           return RateLimitPartition.GetNoLimiter("unlimited");

       return RateLimitPartition.GetSlidingWindowLimiter(
           ctx.User.FindFirst("sub")?.Value ?? ClientPartitionKey.For(ctx, trustedProxies),
           _ => new SlidingWindowRateLimiterOptions
           {
               PermitLimit = 6_000,
               Window = TimeSpan.FromMinutes(1),
               SegmentsPerWindow = 6,
               QueueLimit = 0
           });
   }

   /// <summary>The pre-auth global limiter's partition (CSR round-9 #2, round-10 #3).</summary>
   internal static RateLimitPartition<string> PreAuthPartition(HttpContext ctx, TrustedProxyOptions trustedProxies)
   {
       // <the "deliberately does NOT exclude gRPC calls" comment block moved from the old lambda, unchanged>
       var isHealthListenerEndpoint = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>()?.Port == 8081;
       if (isHealthListenerEndpoint)
           return RateLimitPartition.GetNoLimiter("unlimited");

       return RateLimitPartition.GetSlidingWindowLimiter(
           ClientPartitionKey.For(ctx, trustedProxies),
           _ => new SlidingWindowRateLimiterOptions
           {
               // <the "Matches RateLimitInterceptor's 50,000/min" comment moved unchanged>
               PermitLimit = 50_000,
               Window = TimeSpan.FromMinutes(1),
               SegmentsPerWindow = 6,
               QueueLimit = 0
           });
   }
   ```

   The `// <…>` lines stand for the existing comment text at `:112-126`, `:492-499` and `:504-506`. Move it, don't rewrite it.

- [ ] **Step 4: Run the new tests and confirm they pass**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport && dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.RateLimiting"
```

Expected: 21 passed. That is 18 in `ClientPartitionKeyTests` (12 theory cases and 6 facts) and 3 in `TrustedProxyOptionsTests`. Record the count xUnit reports.

- [ ] **Step 5: Mutation-check the key, restoring exactly after each**
  1. Replace `!opts.Networks.Any(network => network.Contains(peer))` with `false`. The untrusted-peer cases must fail.
  2. Replace `entries[^opts.Hops]` with `entries[opts.Hops - 1]`. The spoofed-entry and two-hop cases must fail.
  3. Delete the `peer = peer.MapToIPv4();` line. Both IPv4-mapped cases must fail.

- [ ] **Step 6: Run the pipeline suites that build the app**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport && dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Pipeline|FullyQualifiedName~TracesRelay|FullyQualifiedName~AdminConsole"
```

Expected: all pass. The app now binds `RateLimiting:TrustedProxies` (empty) at startup.

- [ ] **Step 7: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport
git add Iverson.Server/Iverson.Api/RateLimiting Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/RateLimiting
git commit -m "key the rate limiters on the client address a trusted proxy reports, not the proxy's own

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: Helm trusted proxies

**Files:**
- Modify:
  - `Iverson.Server/deploy/helm/iverson/charts/api/values.yaml`
  - `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` (top of file, and env before `:137`)
  - `Iverson.Server/deploy/helm/iverson/values-{local,laptop,aws,azure,gcp}.yaml`, under `api:`

**Interfaces:**
- Consumes: Task 1's key names `RateLimiting__TrustedProxies__Cidrs__<i>` and `RateLimiting__TrustedProxies__Hops`.

- [ ] **Step 1: Add the subchart default** to `charts/api/values.yaml`, after `adminConsoleEnabled`:

```yaml
# CSR round-10 #3: the load-balancer or ingress ranges whose X-Forwarded-For the rate limiters
# trust, and which entry from the right holds the client (2 behind Google's load balancer, which
# appends "<client>,<lb-ip>"). No usable default: every profile sets its own, and
# templates/deployment.yaml fails the render while the list is empty, the same guard
# networkPolicy.clusterCidrs has.
trustedProxies:
  cidrs: []
  hops: 1
```

- [ ] **Step 2: Add the guard and the env vars** to `charts/api/templates/deployment.yaml`.

At the very top:

```yaml
{{- if not .Values.trustedProxies.cidrs }}
{{- fail "api.trustedProxies.cidrs must list at least one CIDR: the load-balancer or ingress ranges whose X-Forwarded-For the rate limiters trust" }}
{{- end }}
{{- range .Values.trustedProxies.cidrs }}
{{- if not (trim .) }}
{{- fail "api.trustedProxies.cidrs contains a blank entry; every entry must be a CIDR" }}
{{- end }}
{{- end }}
```

In `env:`, immediately before `- name: Authentication__Authority`:

```yaml
            {{- range $i, $cidr := .Values.trustedProxies.cidrs }}
            - name: RateLimiting__TrustedProxies__Cidrs__{{ $i }}
              value: {{ $cidr | quote }}
            {{- end }}
            - name: RateLimiting__TrustedProxies__Hops
              value: {{ .Values.trustedProxies.hops | quote }}
```

- [ ] **Step 3: Set each profile.** In each file, add a `trustedProxies:` block inside the existing top-level `api:` block, after its `ingress:` block. Use the values below, with the comment shown.

`values-local.yaml` and `values-laptop.yaml`:

```yaml
  # CSR round-10 #3: ingress-nginx reaches the API from a pod IP in kind's pod CIDR (the tigera
  # operator sizes Calico's pool from kubeadm's podSubnet, kind's default 10.244.0.0/16), and its
  # defaults replace X-Forwarded-For with the address it saw. NetworkPolicy admits only the
  # ingress-nginx namespace from pod IPs on 8080.
  trustedProxies:
    cidrs: ["10.244.0.0/16"]
    hops: 1
```

`values-aws.yaml`:

```yaml
  # CSR round-10 #3: the two public subnets (modules/cluster-aws, cidrsubnet(vpc_cidr, 4, 8..9)),
  # where the internet-facing ALB is placed; nodes sit in the private subnets. ALB appends the
  # client IP.
  trustedProxies:
    cidrs: ["10.0.128.0/20", "10.0.144.0/20"]
    hops: 1
```

`values-azure.yaml`:

```yaml
  # CSR round-10 #3: the Application Gateway subnet (modules/cluster-azure,
  # ingress_application_gateway.subnet_cidr). It writes "ip:port", which the API accepts.
  trustedProxies:
    cidrs: ["10.1.16.0/24"]
    hops: 1
```

`values-gcp.yaml`:

```yaml
  # CSR round-10 #3: Google's front-end ranges. The load balancer appends "<client>,<lb-ip>", so
  # the client is the second entry from the right.
  trustedProxies:
    cidrs: ["130.211.0.0/22", "35.191.0.0/16"]
    hops: 2
```

- [ ] **Step 4: Render and check**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport/Iverson.Server/deploy/helm/iverson
helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do
  x=""; [ -f $v.ci-override.yaml ] && x="-f $v.ci-override.yaml"
  echo "== $v"; helm template iverson . -f $v.yaml $x --show-only charts/api/templates/deployment.yaml \
    | grep -A1 "RateLimiting__TrustedProxies"
done
helm template iverson . -f values-local.yaml --set-json 'api.trustedProxies.cidrs=[]' 2>&1 | grep -c "api.trustedProxies.cidrs must list"
helm template iverson . -f values-local.yaml --set-json 'api.trustedProxies.cidrs=[" "]' 2>&1 | grep -c "contains a blank entry"
```

Expected:
- Each profile prints its CIDRs as `RateLimiting__TrustedProxies__Cidrs__0..n` and its `Hops`. GCP prints `"2"`.
- Both `grep -c` lines print `1`.
- Then `helm lint . -f <profile> [ci-override]` exits 0 for all 5 profiles.

- [ ] **Step 5: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport
git add Iverson.Server/deploy/helm/iverson/charts/api/values.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/deploy/helm/iverson/values-local.yaml Iverson.Server/deploy/helm/iverson/values-laptop.yaml Iverson.Server/deploy/helm/iverson/values-aws.yaml Iverson.Server/deploy/helm/iverson/values-azure.yaml Iverson.Server/deploy/helm/iverson/values-gcp.yaml
git commit -m "give every Helm profile its trusted proxy ranges and fail the render without them

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: Enrichment regex and bounded response read

**Files:**
- Modify: `Iverson.Server/Iverson.Embeddings/EnrichmentService.cs` (`:22-23`, `GenerateInternalAsync` at `:37-84`)
- Test: `Iverson.Server/Iverson.Embeddings.Tests/EnrichmentServiceTests.cs`

- [ ] **Step 1: Write the failing tests.** Add these to `EnrichmentServiceTests`, reusing its `FakeHttpMessageHandler`, `CreateService` and `ChatResponse`:

```csharp
    // A response body that never completes: ReadAsync waits until its token is cancelled.
    private sealed class StallingStream : Stream
    {
        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static EnrichmentServiceOptions WithTimeout(TimeSpan timeout) =>
        new() { ModelId = "Qwen/Qwen2.5-1.5B-Instruct", BaseUrl = "http://tgi:8092", Timeout = timeout };

    [Fact]
    public async Task GenerateJsonAsync_FailsFast_OnManyUnterminatedFences()
    {
        // 128 KB of "```\nx": every fence opening is unterminated. Before NonBacktracking this took
        // seconds; it must now fail (no JSON) well inside the bound.
        var handler = new FakeHttpMessageHandler(ChatResponse(string.Concat(Enumerable.Repeat("```\nx", 26_240))));
        var svc = CreateService(handler);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await FluentActions.Awaiting(() => svc.GenerateJsonAsync("p")).Should().ThrowAsync<InvalidOperationException>();

        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    [Fact]
    public async Task GenerateAsync_RejectsABodyOverOneMebibyte_AsANonTransientError()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[1024 * 1024 + 1])
        });

        await FluentActions.Awaiting(() => CreateService(handler).GenerateAsync("p"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeded*");
    }

    [Fact]
    public async Task GenerateAsync_ParsesABodyJustUnderTheCap()
    {
        var content = new string('a', 1_000_000); // the JSON envelope stays under 1 MiB
        var handler = new FakeHttpMessageHandler(ChatResponse(content));

        (await CreateService(handler).GenerateAsync("p")).Should().HaveLength(1_000_000);
    }

    [Fact]
    public async Task GenerateAsync_RaisesTheHttpClientTimeoutShape_WhenTheBodyStallsPastTheTimeout()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream())
        });
        var svc = CreateService(handler, WithTimeout(TimeSpan.FromMilliseconds(200)));

        (await FluentActions.Awaiting(() => svc.GenerateAsync("p")).Should().ThrowAsync<TaskCanceledException>())
            .WithInnerException<TimeoutException>();
    }

    [Fact]
    public async Task GenerateAsync_LeavesCallerCancellationUntranslated()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream())
        });
        var svc = CreateService(handler, WithTimeout(TimeSpan.FromMinutes(1)));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var thrown = await FluentActions.Awaiting(() => svc.GenerateAsync("p", caller.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.InnerException.Should().NotBeOfType<TimeoutException>();
    }
```

- [ ] **Step 2: Run them and confirm the expected failures**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport && dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj --filter "FullyQualifiedName~EnrichmentServiceTests"
```

Expected failures:
- `FailsFast` (takes seconds);
- `RejectsABodyOverOneMebibyte` (a `JsonException` or no throw instead);
- `RaisesTheHttpClientTimeoutShape` (it hangs past the 200 ms, finishing only at the test's 100 s HttpClient timeout, so cancel the run once the other results are in);
- `LeavesCallerCancellationUntranslated` may already pass.

The just-under-cap test passes. Record the output.

- [ ] **Step 3: Implement** in `EnrichmentService.cs`.

Replace the regex declaration (`:22-23`):

```csharp
    // NonBacktracking (CSR round-10 #21): the lazy body over many unterminated fence openings was
    // quadratic under the backtracking engine; this engine is linear and returns the same groups.
    private static readonly Regex FencedBlock =
        new("```(?:json)?\\s*\\n(.*?)\\n\\s*```", RegexOptions.Singleline | RegexOptions.NonBacktracking);

    // A completion capped at MaxGeneratedTokens is a few KB; anything near this cap is not a reply.
    internal const int MaxResponseBytes = 1024 * 1024;
```

Replace `GenerateInternalAsync` with:

```csharp
    private async Task<string> GenerateInternalAsync(string prompt, bool jsonFormat, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("enrichment.generate", ActivityKind.Client);
        activity?.SetTag("enrichment.model", ModelId);
        activity?.SetTag("enrichment.input_chars", prompt.Length);
        activity?.SetTag("enrichment.json_format", jsonFormat);

        // The body is read after the headers, outside HttpClient.Timeout, so the whole call shares one
        // deadline: the same bound HttpClient.Timeout gave the buffered read before (CSR round-10 #21).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.Value.Timeout);

        try
        {
            try
            {
                using var client = httpClientFactory.CreateClient(Telemetry.EnrichmentHttpClientName);

                // <the existing "OpenAI-compatible chat route" comment, unchanged>
                var body = JsonSerializer.Serialize(new
                {
                    model       = ModelId,
                    messages    = new[] { new { role = "user", content = prompt } },
                    max_tokens  = MaxGeneratedTokens,
                    temperature = 0,
                    stream      = false,
                }, _jsonOpts);
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_baseUrl), "/v1/chat/completions"))
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await ReadCappedAsync(response.Content, deadline.Token));

                // { "choices": [ { "message": { "role": "assistant", "content": "..." } } ], ... }
                var text = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? string.Empty;

                activity?.SetTag("enrichment.output_chars", text.Length);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return text;
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The shape HttpClient itself raises on timeout, which EnrichmentConsumer skips rather
                // than redelivers. Cancelling the linked source surfaces with an inner IOException,
                // which that filter would not match.
                throw new TaskCanceledException(
                    $"The enrichment call exceeded its {options.Value.Timeout} timeout.", new TimeoutException(ex.Message));
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "GenerateAsync failed for model {Model}", ModelId);
            throw;
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw Oversize();

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw Oversize();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Non-transient (CSR round-10 #21): an oversize reply is the backend misbehaving, not an outage.
    private static InvalidOperationException Oversize() =>
        new($"Enrichment backend response exceeded {MaxResponseBytes} bytes.");
```

- [ ] **Step 4: Run the Embeddings suite**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport && dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj
```

Expected: all pass, 59 (54 before plus 5 new). Record the count.

- [ ] **Step 5: Mutation-check, restoring exactly after each**
  1. Remove `deadline.CancelAfter(...)`. The stall test must fail (it hangs; cancel the run).
  2. Change the catch filter to `when (deadline.IsCancellationRequested)`. The caller-cancellation test must fail.
  3. Replace `RegexOptions.NonBacktracking` with `RegexOptions.Compiled`. `FailsFast` must fail.
  4. Remove the `buffer.Length + read > MaxResponseBytes` check. The oversize test must fail.

- [ ] **Step 6: Run the enrichment consumer tests** (they pin the skip path for this exception shape, `EnrichmentConsumerTests.cs:545`)

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport && dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~EnrichmentConsumerTests|FullyQualifiedName~TransientFailures"
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport
git add Iverson.Server/Iverson.Embeddings/EnrichmentService.cs Iverson.Server/Iverson.Embeddings.Tests/EnrichmentServiceTests.cs
git commit -m "make the enrichment fence regex linear and bound the response body in size and time

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: API side of Authentik TLS

**Files:**
- Create: `Iverson.Server/Iverson.Api/Tenancy/AuthentikTrust.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (after Task 1's `trustedProxies` line; the default scheme `:181-205`; the `ActingUser` scheme `:206-233`; the IdpAdminClient registration and its comment `:393-412`)
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs`

**Interfaces:**
- Produces:
  - the setting names `Authentication:MetadataAddress`, `Authentication:ActingUser:MetadataAddress`, `Authentication:InternalIssuer` and `Authentik:CaCertificatePath`, used by Tasks 5 and 6;
  - `Authentik:BaseUrl`, which must now be https outside Development.

- [ ] **Step 1: Write the failing tests** in `Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

public class AuthentikTrustTests : IClassFixture<AuthTestWebApplicationFactory>, IDisposable
{
    private readonly AuthTestWebApplicationFactory _baseFactory;
    private readonly string _dir = Directory.CreateTempSubdirectory("authentik-trust-").FullName;

    public AuthentikTrustTests(AuthTestWebApplicationFactory baseFactory) => _baseFactory = baseFactory;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static X509Certificate2 NewCa(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    // A leaf for dnsName signed by issuer, or self-signed when issuer is null, exported and reloaded
    // so Kestrel gets a persisted private key on every platform.
    private static X509Certificate2 NewLeaf(string dnsName, X509Certificate2? issuer)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = issuer is null
            ? req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30))
            : req.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(8))
                 .CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private string WritePem(X509Certificate2 cert, string file)
    {
        var path = Path.Combine(_dir, file);
        File.WriteAllText(path, cert.ExportCertificatePem());
        return path;
    }

    private static async Task<HttpStatusCode> GetThroughHandler(X509Certificate2 serverCert, string caPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(serverCert)));
        await using var app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        var port = new Uri(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First()).Port;

        using var http = new HttpClient(AuthentikTrust.CreateHandler(caPath));
        return (await http.GetAsync($"https://localhost:{port}/")).StatusCode;
    }

    [Fact]
    public async Task Handler_AcceptsACertificateTheConfiguredCaSigned()
    {
        var ca = NewCa("test-ca");
        (await GetThroughHandler(NewLeaf("localhost", ca), WritePem(ca, "ca.crt"))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Handler_RejectsACertificateAnotherCaSigned()
    {
        var trusted = NewCa("trusted-ca");
        var other = NewCa("other-ca");
        await FluentActions.Awaiting(() => GetThroughHandler(NewLeaf("localhost", other), WritePem(trusted, "ca.crt")))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Handler_RejectsACertificateForAnotherHost()
    {
        var ca = NewCa("test-ca");
        await FluentActions.Awaiting(() => GetThroughHandler(NewLeaf("other.example", ca), WritePem(ca, "ca.crt")))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Handler_RejectsASelfSignedCertificate()
    {
        var ca = NewCa("test-ca");
        await FluentActions.Awaiting(() => GetThroughHandler(NewLeaf("localhost", null), WritePem(ca, "ca.crt")))
            .Should().ThrowAsync<HttpRequestException>();
    }

    private IConfiguration GoodConfig(Action<Dictionary<string, string?>>? change = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Authentication:MetadataAddress"] = "https://iverson-authentik:8443/application/o/iverson-api/.well-known/openid-configuration",
            ["Authentication:ActingUser:MetadataAddress"] = "https://iverson-authentik:8443/application/o/iverson-api/.well-known/openid-configuration",
            ["Authentication:InternalIssuer"] = "http://iverson-authentik:9000/",
            ["Authentik:BaseUrl"] = "https://iverson-authentik:8443",
            ["Authentik:CaCertificatePath"] = WritePem(NewCa("test-ca"), "guard-ca.crt"),
        };
        change?.Invoke(values);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    [Theory]
    [InlineData("Authentication:MetadataAddress", null)]
    [InlineData("Authentication:MetadataAddress", "http://iverson-authentik:9000/application/o/iverson-api/.well-known/openid-configuration")]
    [InlineData("Authentication:ActingUser:MetadataAddress", null)]
    [InlineData("Authentication:ActingUser:MetadataAddress", "http://iverson-authentik:9000/application/o/iverson-api/.well-known/openid-configuration")]
    [InlineData("Authentik:BaseUrl", null)]
    [InlineData("Authentik:BaseUrl", "http://iverson-authentik:9000")]
    [InlineData("Authentication:InternalIssuer", null)]
    [InlineData("Authentik:CaCertificatePath", null)]
    [InlineData("Authentik:CaCertificatePath", "/nonexistent/ca.crt")]
    public void Guard_RefusesTheApiRole_OutsideDevelopment(string key, string? value) =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(v => v[key] = value), "api", Env("Production")))
            .Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");

    [Fact]
    public void Guard_RefusesACaFileThatIsNotAPemCertificate()
    {
        var notPem = Path.Combine(_dir, "not.pem");
        File.WriteAllText(notPem, "not a certificate");
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(v => v["Authentik:CaCertificatePath"] = notPem), "api", Env("Production")))
            .Should().Throw<InvalidOperationException>().WithMessage("*Authentik:CaCertificatePath*");
    }

    [Fact]
    public void Guard_PassesAFullyConfiguredApiRole() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(), "api", Env("Production"))).Should().NotThrow();

    [Fact]
    public void Guard_SkipsDevelopment() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(new ConfigurationBuilder().Build(), "api", Env("Development"))).Should().NotThrow();

    [Fact]
    public void Guard_SkipsTheWorkerRole() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(new ConfigurationBuilder().Build(), "worker", Env("Production"))).Should().NotThrow();

    [Theory]
    [InlineData(JwtBearerDefaults.AuthenticationScheme, "Authentication:MetadataAddress")]
    [InlineData("ActingUser", "Authentication:ActingUser:MetadataAddress")]
    public void BothSchemes_FetchMetadataOverTls_TrustTheCa_AndAcceptTheInternalIssuer(string scheme, string metadataKey)
    {
        const string metadata = "https://iverson-authentik:8443/application/o/iverson-api/.well-known/openid-configuration";
        var caPath = WritePem(NewCa("test-ca"), $"wiring-{scheme}.crt");
        using var factory = _baseFactory.WithWebHostBuilder(b => b
            .UseSetting(metadataKey, metadata)
            .UseSetting("Authentication:InternalIssuer", "http://iverson-authentik:9000/")
            .UseSetting("Authentik:CaCertificatePath", caPath));

        var opts = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(scheme);

        opts.RequireHttpsMetadata.Should().BeTrue();
        opts.MetadataAddress.Should().Be(metadata);
        opts.TokenValidationParameters.ValidIssuers.Should().Contain("http://iverson-authentik:9000/");
        opts.BackchannelHttpHandler.Should().BeOfType<SocketsHttpHandler>()
            .Which.SslOptions.RemoteCertificateValidationCallback.Should().NotBeNull();
    }
}
```

- [ ] **Step 2: Run them and confirm they fail to compile** (there is no `AuthentikTrust` yet)

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport && dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AuthentikTrustTests"
```

- [ ] **Step 3: Implement** `Iverson.Api/Tenancy/AuthentikTrust.cs`:

```csharp
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round-10 #5: the API reaches Authentik (OIDC discovery, JWKS, the admin API) over TLS on the
/// tls-proxy sidecar's port 8443, trusting only the CA the deployment mounts at
/// <c>Authentik:CaCertificatePath</c>.
/// </summary>
internal static class AuthentikTrust
{
    /// <summary>
    /// A handler that accepts only a certificate chaining to the CA at <paramref name="caCertificatePath"/>
    /// and matching the requested host. With no path (Development, the test suites) it is a default handler.
    /// </summary>
    public static HttpMessageHandler CreateHandler(string? caCertificatePath)
    {
        if (string.IsNullOrEmpty(caCertificatePath))
            return new SocketsHttpHandler();

        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(caCertificatePath));
        return new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, errors) => ChainsToCa(ca, presented, errors) }
        };
    }

    private static bool ChainsToCa(X509Certificate2 ca, X509Certificate? presented, SslPolicyErrors errors)
    {
        // The platform chain error is expected (the CA is in no system store) and is decided below;
        // a missing certificate or a name mismatch is not.
        if (presented is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        // A private CA publishes no revocation endpoint.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(presented));
    }

    /// <summary>
    /// Outside Development the api role refuses to start unless every Authentik hop is https and the
    /// CA is readable. The worker role never calls Authentik and carries no Authentik settings.
    /// </summary>
    public static void ValidateStartup(IConfiguration cfg, string workloadRole, IHostEnvironment env)
    {
        if (workloadRole != "api" || env.IsDevelopment())
            return;

        RequireHttps(cfg, "Authentication:MetadataAddress");
        RequireHttps(cfg, "Authentication:ActingUser:MetadataAddress");
        RequireHttps(cfg, "Authentik:BaseUrl");
        if (string.IsNullOrWhiteSpace(cfg["Authentication:InternalIssuer"]))
            throw new InvalidOperationException("Authentication:InternalIssuer is required outside Development.");

        var caPath = cfg["Authentik:CaCertificatePath"];
        if (string.IsNullOrWhiteSpace(caPath))
            throw new InvalidOperationException("Authentik:CaCertificatePath is required outside Development.");
        try
        {
            _ = X509Certificate2.CreateFromPem(File.ReadAllText(caPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"Authentik:CaCertificatePath '{caPath}' is not a readable PEM certificate.", ex);
        }
    }

    private static void RequireHttps(IConfiguration cfg, string key)
    {
        var value = cfg[key];
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{key} must be an https:// URL outside Development, got '{value}'.");
    }
}
```

`Program.cs`:
1. Immediately after `var app = builder.Build();` (`:478`), add the guard. It runs against `app.Configuration` and `app.Environment` because they are final there: WebApplicationFactory's settings and environment arrive too late for code that runs before `Build()`, as the comment at `AuthTestWebApplicationFactory.cs:37-46` records. A failure still stops startup before `app.Run()`.

   ```csharp
   // CSR round-10 #5: outside Development the api role reaches Authentik only over TLS, trusting only
   // the CA the deployment mounts.
   Iverson.Api.Tenancy.AuthentikTrust.ValidateStartup(app.Configuration, workloadRole, app.Environment);
   ```

   For the same reason, the settings below are read inside the JwtBearer and HttpClient configuration lambdas, which run when the options resolve, never into variables before `Build()`.

2. In the default `AddJwtBearer(options => { … })`:
   - **Issuers.** After `options.TokenValidationParameters.ValidIssuers = new[] { … };`, add:

     ```csharp
     // Authentik's global issuer mode stamps iss with the minting request's own scheme and host. Tokens
     // minted in-cluster on 9000 carry http://<authentik>:9000/, which matched only because JwtBearer
     // appends the metadata document's issuer; with metadata now on 8443 that issuer is the https one.
     if (cfg["Authentication:InternalIssuer"] is { Length: > 0 } internalIssuer)
         options.TokenValidationParameters.ValidIssuers = options.TokenValidationParameters.ValidIssuers.Append(internalIssuer).ToArray();
     if (cfg["Authentication:MetadataAddress"] is { Length: > 0 } metadataAddress)
         options.MetadataAddress = metadataAddress;
     ```

   - **HTTPS.** Replace the comment block at `:191-196` and `options.RequireHttpsMetadata = false;` with:

     ```csharp
     // CSR round-10 #5: discovery and JWKS come from the tls-proxy sidecar over https, validated
     // against the deployment's own CA (AuthentikTrust).
     options.RequireHttpsMetadata = true;
     options.BackchannelHttpHandler = Iverson.Api.Tenancy.AuthentikTrust.CreateHandler(cfg["Authentik:CaCertificatePath"]);
     ```

3. In `AddJwtBearer("ActingUser", options => { … })`:
   - Replace `options.RequireHttpsMetadata = false;` with `options.RequireHttpsMetadata = true;`.
   - After it, add:

     ```csharp
     options.BackchannelHttpHandler = Iverson.Api.Tenancy.AuthentikTrust.CreateHandler(cfg["Authentik:CaCertificatePath"]);
     if (cfg["Authentication:ActingUser:MetadataAddress"] is { Length: > 0 } actingMetadataAddress)
         options.MetadataAddress = actingMetadataAddress;
     // See the default scheme: acting-user tokens minted on 9000 carry the internal issuer.
     if (cfg["Authentication:InternalIssuer"] is { Length: > 0 } internalIssuer)
         options.TokenValidationParameters.ValidIssuers = [internalIssuer];
     ```

4. **IdpAdminClient.**
   - Replace the comment block at `:393-400` with:

     ```csharp
     // CSR round-10 #5: the admin token travels over TLS to the tls-proxy sidecar, trusting only the
     // deployment's CA. Authentik:BaseUrl has no fallback: the startup guard requires it outside
     // Development, and the Development test hosts register their own clients.
     ```

   - Replace `var authentikBaseUrlValue = cfg["Authentik:BaseUrl"] ?? "http://authentik-server:9000";` and the `AddHttpClient(IdpAdminClient.HttpClientName, …)` registration with:

     ```csharp
     builder.Services.AddHttpClient(Iverson.Api.Tenancy.IdpAdminClient.HttpClientName, client =>
     {
         if (cfg["Authentik:BaseUrl"] is { Length: > 0 } authentikBaseUrl)
             client.BaseAddress = new Uri(authentikBaseUrl);
         var adminToken = cfg["Authentik:AdminToken"];
         if (!string.IsNullOrEmpty(adminToken))
             client.DefaultRequestHeaders.Authorization =
                 new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
     }).ConfigurePrimaryHttpMessageHandler(() => Iverson.Api.Tenancy.AuthentikTrust.CreateHandler(cfg["Authentik:CaCertificatePath"]));
     ```

- [ ] **Step 4: Run the new tests**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport && dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AuthentikTrustTests"
```

Expected: all pass. That is 4 handler tests, 9 + 4 guard cases, and 2 wiring cases. Record the count xUnit reports.

- [ ] **Step 5: Mutation-check, restoring exactly after each**
  1. Make `ChainsToCa` return `true` unconditionally. The other-CA, other-host and self-signed tests must fail.
  2. Delete the `RemoteCertificateNameMismatch` check. The other-host test must fail.
  3. Delete `RequireHttps(cfg, "Authentik:BaseUrl")`. Both `Authentik:BaseUrl` guard cases must fail.
  4. In the ActingUser scheme, delete the `ValidIssuers = [internalIssuer]` line. The ActingUser wiring case must fail.

- [ ] **Step 6: Run the whole Api.Tests suite** (several minutes, because of testcontainers)

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport && dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

Expected: 0 failed. The count is 1399 + Task 1's tests + this task's tests; record it.

- [ ] **Step 7: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport
git add Iverson.Server/Iverson.Api/Tenancy/AuthentikTrust.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs
git commit -m "fetch Authentik metadata and send the admin token over TLS, trusting only the deployment's CA

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 5: Helm side of Authentik TLS

**Files:**
- Create:
  - `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-internal-tls.yaml`
  - `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap-tls-proxy.yaml`
- Modify:
  - `charts/authentik/values.yaml` (`resources.tlsProxy`)
  - `charts/authentik/templates/deployment-server.yaml` (the `tls-proxy` container and volumes)
  - `charts/authentik/templates/service.yaml` (port 8443)
  - `templates/networkpolicies.yaml` (`:90-91` and `:600-602`)
  - `charts/api/templates/deployment.yaml` (the Authentik env vars at `:137-176`, the volume)

**Interfaces:**
- Consumes: Task 4's setting names.

- [ ] **Step 1: Baseline** `kube-score` on the branch before editing. Same command as CI.

```bash
export SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
mkdir -p $SCR/t5; for v in values-local values-laptop values-aws values-azure values-gcp; do x=""; [ -f $v.ci-override.yaml ] && x="-f $v.ci-override.yaml"
  helm template iverson . -f $v.yaml $x | kube-score score --ignore-test pod-networkpolicy --ignore-test container-image-pull-policy --ignore-test container-security-context-user-group-id - \
  | awk '/^[a-z].*\//{obj=$1" "$2} /CRITICAL/{print obj" |"$0}' | sort > $SCR/t5/before-$v.txt; done
```

- [ ] **Step 2: Add the internal Secret**, `charts/authentik/templates/secret-internal-tls.yaml`:

```yaml
{{- /* CSR round-10 #5: the certificate the tls-proxy sidecar serves on 8443 and the CA the API trusts
   for it. Lookup-or-generate like secret-postgres.yaml: an existing Secret is reused unchanged, so
   upgrades keep the same CA. The CA private key is never stored, so nothing in the cluster can issue
   more certificates under it. Rotate by deleting this Secret, upgrading, then restarting the
   Authentik server and API pods. The name must never equal the public ingress certificate's. */}}
{{- $name := printf "%s-authentik-internal-tls" .Release.Name }}
{{- if eq $name .Values.ingress.tlsSecretName }}
{{- fail (printf "authentik.ingress.tlsSecretName must not be %s: that is the chart-owned internal TLS Secret" $name) }}
{{- end }}
apiVersion: v1
kind: Secret
metadata:
  name: {{ $name }}
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace $name }}
{{- if $existing }}
  tls.crt: {{ index $existing.data "tls.crt" }}
  tls.key: {{ index $existing.data "tls.key" }}
  ca.crt: {{ index $existing.data "ca.crt" }}
{{- else }}
{{- $svc := printf "%s-authentik" .Release.Name }}
{{- $ca := genCA "iverson-authentik-ca" 3650 }}
{{- $cert := genSignedCert $svc nil (list $svc (printf "%s.%s.svc" $svc .Release.Namespace) (printf "%s.%s.svc.cluster.local" $svc .Release.Namespace)) 3650 $ca }}
  tls.crt: {{ $cert.Cert | b64enc }}
  tls.key: {{ $cert.Key | b64enc }}
  ca.crt: {{ $ca.Cert | b64enc }}
{{- end }}
```

- [ ] **Step 3: Add the sidecar config**, `charts/authentik/templates/configmap-tls-proxy.yaml`. Compose's copy is `deploy/compose-tls-proxy.conf` (Task 6); keep the two identical.

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .Release.Name }}-authentik-tls-proxy
data:
  # CSR round-10 #5: TLS on 8443 in front of Authentik's plaintext 9000, inside the same pod. Host is
  # passed through and X-Forwarded-Proto set to https, so Authentik (which trusts both from loopback)
  # builds https issuer and jwks_uri URLs for this listener.
  default.conf: |
    server {
      listen 8443 ssl;
      ssl_certificate     /etc/iverson/tls/tls.crt;
      ssl_certificate_key /etc/iverson/tls/tls.key;
      location / {
        proxy_pass http://127.0.0.1:9000;
        proxy_set_header Host $http_host;
        proxy_set_header X-Forwarded-Proto https;
      }
    }
```

- [ ] **Step 4: Add the container, volumes and port.**

In `charts/authentik/values.yaml`, under `resources:` after `worker:`:

```yaml
  tlsProxy:
    requests: { cpu: "10m", memory: "16Mi" }
    limits: { cpu: "100m", memory: "64Mi" }
```

In `deployment-server.yaml`, after the `server` container's `volumeMounts` (before `volumes:`), add:

```yaml
        # CSR round-10 #5: terminates TLS on 8443 for the API's discovery, JWKS and admin-API calls.
        # Authentik's own 9443 listener serves a self-signed fallback and is never exposed.
        - name: tls-proxy
          image: "nginxinc/nginx-unprivileged:1.27-alpine@sha256:65e3e85dbaed8ba248841d9d58a899b6197106c23cb0ff1a132b7bfe0547e4c0"
          imagePullPolicy: IfNotPresent
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: ["ALL"] }
          ports:
            - containerPort: 8443
              name: https
          resources:
            requests:
              cpu: {{ .Values.resources.tlsProxy.requests.cpu | quote }}
              memory: {{ .Values.resources.tlsProxy.requests.memory | quote }}
            limits:
              cpu: {{ .Values.resources.tlsProxy.limits.cpu | quote }}
              memory: {{ .Values.resources.tlsProxy.limits.memory | quote }}
          readinessProbe:
            tcpSocket: { port: 8443 }
            periodSeconds: 10
          volumeMounts:
            - name: tls-proxy-config
              mountPath: /etc/nginx/conf.d
              readOnly: true
            - name: internal-tls
              mountPath: /etc/iverson/tls
              readOnly: true
            - name: tls-proxy-tmp
              mountPath: /tmp
```

The container inherits the pod's `runAsUser: 1000`/`fsGroup: 1000`, so it can read the Secret's key. Under `volumes:` add:

```yaml
        - name: tls-proxy-config
          configMap:
            name: {{ .Release.Name }}-authentik-tls-proxy
        - name: internal-tls
          secret:
            secretName: {{ .Release.Name }}-authentik-internal-tls
            items:
              - key: tls.crt
                path: tls.crt
              - key: tls.key
                path: tls.key
        - name: tls-proxy-tmp
          emptyDir: {}
```

In `service.yaml`, add under `ports:`:

```yaml
    - name: https
      port: 8443
      targetPort: 8443
```

Match the existing `http` entry's keys.

- [ ] **Step 5: Move the two NetworkPolicy rules** in `templates/networkpolicies.yaml`:
  - `:90-91` (`<release>-api-egress`): the rule to `{{ .Release.Name }}-authentik-server` changes `port: 9000` to `port: 8443`.
  - `:600-602` (`<release>-authentik-ingress`): the rule `from: podSelector app: {{ .Release.Name }}-api` changes `port: 9000` to `port: 8443`.
  - Leave the allow-all `from: []` 9000 rule unchanged.
  - Add a one-line comment on each: `# CSR round-10 #5: the API reaches Authentik only on the tls-proxy sidecar's 8443.`

- [ ] **Step 6: Point the API at 8443** in `charts/api/templates/deployment.yaml`:
  - Change `Authentik__BaseUrl` to `"https://{{ .Release.Name }}-authentik:8443"`.
  - After the `Authentik__AdminToken` entry, add:

    ```yaml
                - name: Authentication__MetadataAddress
                  value: "https://{{ .Release.Name }}-authentik:8443/application/o/iverson-api/.well-known/openid-configuration"
                - name: Authentication__ActingUser__MetadataAddress
                  value: "https://{{ .Release.Name }}-authentik:8443/application/o/iverson-api/.well-known/openid-configuration"
                # The issuer in-cluster minters on 9000 stamp (Authentik's global issuer mode).
                - name: Authentication__InternalIssuer
                  value: "http://{{ .Release.Name }}-authentik:9000/"
                - name: Authentik__CaCertificatePath
                  value: "/etc/iverson/authentik-ca/ca.crt"
    ```

  - Under `volumeMounts:`, add:

    ```yaml
                - name: authentik-ca
                  mountPath: /etc/iverson/authentik-ca
                  readOnly: true
    ```

  - Under `volumes:`, add:

    ```yaml
            - name: authentik-ca
              secret:
                secretName: {{ .Release.Name }}-authentik-internal-tls
                items:
                  - key: ca.crt
                    path: ca.crt
    ```

  - `Authentication__Authority` and `Authentication__ActingUser__Authority` stay as they are.

- [ ] **Step 7: Render and check**

```bash
export SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
# The generated certificate chains to the CA and names the service.
helm template iverson . -f values-local.yaml --namespace iverson --show-only charts/authentik/templates/secret-internal-tls.yaml > $SCR/t5/secret.yaml
python3 - <<'PY'
import base64,yaml,os
d=yaml.safe_load(open(os.environ['SCR']+'/t5/secret.yaml'))['data']
for k in ('tls.crt','ca.crt'): open(os.environ['SCR']+'/t5/'+k,'wb').write(base64.b64decode(d[k]))
PY
openssl verify -CAfile $SCR/t5/ca.crt $SCR/t5/tls.crt
openssl x509 -in $SCR/t5/tls.crt -noout -ext subjectAltName
# The collision guard.
helm template iverson . -f values-azure.yaml -f values-azure.ci-override.yaml --set authentik.ingress.tlsSecretName=iverson-authentik-internal-tls 2>&1 | grep -c "must not be iverson-authentik-internal-tls"
# Lint, schema and kube-score on all 5 profiles.
for v in values-local values-laptop values-aws values-azure values-gcp; do x=""; [ -f $v.ci-override.yaml ] && x="-f $v.ci-override.yaml"
  helm lint . -f $v.yaml $x >/dev/null && echo "$v lint ok"
  helm template iverson . -f $v.yaml $x | kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas | tail -1
  helm template iverson . -f $v.yaml $x | kube-score score --ignore-test pod-networkpolicy --ignore-test container-image-pull-policy --ignore-test container-security-context-user-group-id - \
    | awk '/^[a-z].*\//{obj=$1" "$2} /CRITICAL/{print obj" |"$0}' | sort > $SCR/t5/after-$v.txt
  diff $SCR/t5/before-$v.txt $SCR/t5/after-$v.txt && echo "$v kube-score: no new critical"
done
# The rendered wiring.
helm template iverson . -f values-local.yaml | grep -E "8443|authentik-internal-tls|InternalIssuer|MetadataAddress|CaCertificatePath" | sort | uniq -c
```

Expected:
- `openssl verify` prints `OK`, and the SAN lists `iverson-authentik`, `iverson-authentik.iverson.svc` and `iverson-authentik.iverson.svc.cluster.local`.
- The guard `grep -c` prints `1`.
- All 5 profiles print `lint ok`, report 0 kubeconform errors, and print `no new critical`.
- The wiring grep shows:
  - the Service port and both NetworkPolicy rules on 8443;
  - the sidecar mount;
  - the API's three new env vars plus the https `Authentik__BaseUrl`;
  - the `authentik-ca` volume.

  If a new kube-score critical does appear, fix it in the sidecar spec. Don't add an ignore.

- [ ] **Step 8: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport
git add Iverson.Server/deploy/helm/iverson/charts/authentik Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml
git status --short   # charts/*.tgz are gitignored; nothing else should be staged
git commit -m "serve Authentik to the API over TLS from a sidecar with a chart-generated CA

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 6: Compose side of Authentik TLS

**Files:**
- Create: `Iverson.Server/deploy/compose-tls-proxy.conf`
- Modify: `scripts/generate-compose-secrets.sh`, `.gitignore`, `Iverson.Server/docker-compose.yml`

**Interfaces:**
- Consumes: Task 4's setting names.

- [ ] **Step 1: Generate the TLS files from the secrets script.** In `scripts/generate-compose-secrets.sh`, before `echo "Wrote/updated $ENV_FILE"`, add:

```bash
# CSR round-10 #5: the certificate the compose authentik-tls-proxy serves on 8443 and the CA the API
# trusts for it. Written only when missing, like the secrets above, so a re-run keeps them. The CA
# key is deleted once the certificate is signed. The files are world-readable inside an owner-only
# directory: under rootless podman a container uid cannot read a host file that is 0600, and the
# directory keeps other host users out.
TLS_DIR="$(dirname "$0")/../Iverson.Server/deploy/compose-tls"
if [ ! -f "$TLS_DIR/tls.crt" ]; then
    mkdir -p "$TLS_DIR"
    chmod 700 "$TLS_DIR"
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj "/CN=iverson-compose-authentik-ca" \
        -keyout "$work/ca.key" -out "$TLS_DIR/ca.crt" 2>/dev/null
    openssl req -newkey rsa:2048 -nodes -subj "/CN=authentik-server" \
        -keyout "$TLS_DIR/tls.key" -out "$work/tls.csr" 2>/dev/null
    printf 'subjectAltName=DNS:authentik-server\n' > "$work/ext.cnf"
    openssl x509 -req -in "$work/tls.csr" -CA "$TLS_DIR/ca.crt" -CAkey "$work/ca.key" \
        -set_serial "0x$(openssl rand -hex 8)" -days 3650 -extfile "$work/ext.cnf" -out "$TLS_DIR/tls.crt" 2>/dev/null
    chmod 644 "$TLS_DIR/ca.crt" "$TLS_DIR/tls.crt" "$TLS_DIR/tls.key"
    echo "Wrote $TLS_DIR"
fi
```

- [ ] **Step 2: Ignore the TLS directory.** Append to `.gitignore`:

```
# CSR round-10 #5: compose's Authentik TLS material, generated by scripts/generate-compose-secrets.sh
Iverson.Server/deploy/compose-tls/
```

- [ ] **Step 3: Add the compose nginx config**, `Iverson.Server/deploy/compose-tls-proxy.conf`. Byte-identical to the Helm ConfigMap's `default.conf` body (Task 5 Step 3), dedented:

```nginx
server {
  listen 8443 ssl;
  ssl_certificate     /etc/iverson/tls/tls.crt;
  ssl_certificate_key /etc/iverson/tls/tls.key;
  location / {
    proxy_pass http://127.0.0.1:9000;
    proxy_set_header Host $http_host;
    proxy_set_header X-Forwarded-Proto https;
  }
}
```

- [ ] **Step 4: Edit `docker-compose.yml`.**

**a. The header comment.** After the Qdrant bullet, add:

```
#   - Authentik TLS (CSR round-10 #5): the API reaches Authentik through authentik-tls-proxy on 8443,
#     using a per-checkout CA in deploy/compose-tls/ — run scripts/generate-compose-secrets.sh first
#     (existing checkouts must re-run it once before the next `up`)
```

**b. A new service** after `authentik-worker`. The long-form bind mounts make a missing file fail loudly, rather than being created as a directory:

```yaml
  # CSR round-10 #5: terminates TLS on 8443 in front of authentik-server, sharing its network
  # namespace the way the Helm chart's tls-proxy sidecar shares the pod, so the hop to Authentik
  # stays on loopback. The API reaches it as authentik-server:8443.
  authentik-tls-proxy:
    image: nginxinc/nginx-unprivileged:1.27-alpine@sha256:65e3e85dbaed8ba248841d9d58a899b6197106c23cb0ff1a132b7bfe0547e4c0
    container_name: iverson-authentik-tls-proxy
    restart: unless-stopped
    user: "1000:1000"
    read_only: true
    cap_drop: [ALL]
    tmpfs:
      - /tmp:mode=1777
    network_mode: "service:authentik-server"
    volumes:
      - { type: bind, source: ./deploy/compose-tls-proxy.conf, target: /etc/nginx/conf.d/default.conf, read_only: true }
      - { type: bind, source: ./deploy/compose-tls/tls.crt, target: /etc/iverson/tls/tls.crt, read_only: true }
      - { type: bind, source: ./deploy/compose-tls/tls.key, target: /etc/iverson/tls/tls.key, read_only: true }
    depends_on:
      authentik-server:
        condition: service_started
```

**c. In `iverson-api` and `iverson-worker`:**
- Change `Authentik__BaseUrl=http://authentik-server:9000` to `Authentik__BaseUrl=https://authentik-server:8443`.
- After the `Authentik__AdminToken` line, add:

  ```yaml
        - Authentication__MetadataAddress=https://authentik-server:8443/application/o/iverson-api/.well-known/openid-configuration
        - Authentication__ActingUser__MetadataAddress=https://authentik-server:8443/application/o/iverson-api/.well-known/openid-configuration
        # The issuer tokens minted on 9000 carry (Authentik's global issuer mode).
        - Authentication__InternalIssuer=http://authentik-server:9000/
        - Authentik__CaCertificatePath=/etc/iverson/authentik-ca/ca.crt
  ```

- Add, after each service's `environment:` list:

  ```yaml
      volumes:
        - { type: bind, source: ./deploy/compose-tls/ca.crt, target: /etc/iverson/authentik-ca/ca.crt, read_only: true }
  ```

**d. In `iverson-api.depends_on`,** add:

```yaml
      authentik-tls-proxy:
        condition: service_started
```

- [ ] **Step 5: Check the script and the compose file in a scratch copy** (never the main checkout's `.env`)

```bash
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad
T=$SCR/t6; rm -rf $T; mkdir -p $T/scripts $T/Iverson.Server
cp /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport/scripts/generate-compose-secrets.sh $T/scripts/
bash $T/scripts/generate-compose-secrets.sh
stat -c '%a %n' $T/Iverson.Server/deploy/compose-tls $T/Iverson.Server/deploy/compose-tls/*
ls $T/Iverson.Server/deploy/compose-tls
openssl verify -CAfile $T/Iverson.Server/deploy/compose-tls/ca.crt $T/Iverson.Server/deploy/compose-tls/tls.crt
openssl x509 -in $T/Iverson.Server/deploy/compose-tls/tls.crt -noout -ext subjectAltName
sha256sum $T/Iverson.Server/deploy/compose-tls/tls.crt > $T/sum1; bash $T/scripts/generate-compose-secrets.sh >/dev/null; sha256sum -c $T/sum1
BR=/home/ben/repositories/Iverson/.worktrees/csr10-edge-transport
docker compose -f $BR/Iverson.Server/docker-compose.yml --project-directory $BR/Iverson.Server --env-file $T/Iverson.Server/.env config -q && echo "compose config ok"
```

Expected:
- The directory is `700`; `ca.crt`, `tls.crt` and `tls.key` are `644`.
- There is no `ca.key` and no `.srl` file.
- `openssl verify` prints `OK`, and the SAN is `DNS:authentik-server`.
- After the re-run, `sha256sum -c` prints `OK` (unchanged).
- `compose config ok`.

`config` resolves paths only; it neither starts anything nor touches the user's stack.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport
git add scripts/generate-compose-secrets.sh .gitignore Iverson.Server/deploy/compose-tls-proxy.conf Iverson.Server/docker-compose.yml
git commit -m "serve Authentik to the compose API over TLS through a proxy sharing the server's network namespace

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 7: Full suites and live verification

This task makes no commits. Its helpers live in `$SCR/live` and are never committed. Standing rules:
- Snapshot the user's containers, volumes, networks and images before starting, and diff them after teardown.
- Never print a `.env` value or a Kubernetes Secret value. Values reach programs only through exported variables or 0600 files that are deleted afterwards.
- Every block starts with `source $SCR/live/env.sh`.

- [ ] **Step 1: Run the full server suites,** one project at a time.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport/Iverson.Server
for p in Iverson.Api.Tests Iverson.ClientConformance.Tests Iverson.Embeddings.Tests Iverson.Events.Tests Iverson.LoadTest.Tests Iverson.Patterns.Tests Iverson.Sql.Tests Iverson.StarRocks.Tests Iverson.Vector.Tests; do
  dotnet test $p/$p.csproj 2>&1 | grep -E "^(Passed|Failed)!"; done
```

Expected: every line reads `Passed!`, with 0 failed.

- [ ] **Step 2: Prepare,** and check the user's stack is down.

```bash
mkdir -p $SCR/live/home; docker ps --format '{{.Names}}'
```

If the list is not empty, **stop and ask the user**. Never stop their containers yourself. The isolated stack publishes the same host ports.

Write `$SCR/live/env.sh`:

```bash
MAIN=/home/ben/repositories/Iverson
BR=$MAIN/.worktrees/csr10-edge-transport
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad
LIVE=$SCR/live
ENVF=$BR/Iverson.Server/.env
REAL_HOME=/home/ben
DC="docker compose -p csr10edge -f $LIVE/compose.yml --project-directory $BR/Iverson.Server --env-file $ENVF"
val() { grep "^$1=" "$ENVF" | cut -d= -f2-; }
scratch_env() {
  export HOME=$LIVE/home NUGET_PACKAGES=$REAL_HOME/.nuget/packages PYTHONUSERBASE=$REAL_HOME/.local
  export GOMODCACHE=$REAL_HOME/go/pkg/mod MAVEN_OPTS=-Dmaven.repo.local=$REAL_HOME/.m2/repository npm_config_cache=$REAL_HOME/.npm
}
```

Then run:

```bash
source $SCR/live/env.sh
bash $BR/scripts/generate-compose-secrets.sh      # the worktree's own .env and deploy/compose-tls/
grep -v container_name $BR/Iverson.Server/docker-compose.yml \
  | sed 's/^    image: iverson-api$/    image: csr10edge-api/' > $LIVE/compose.yml
grep -c 'image: csr10edge-api' $LIVE/compose.yml
docker ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt
docker volume ls -q | sort > $LIVE/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
```

Expected: `grep -c` prints `2`, for the api and the worker.

- [ ] **Step 3: Write the load probe,** `$LIVE/loadprobe/`, a scratch console app.

```bash
mkdir -p $LIVE/loadprobe && cd $LIVE/loadprobe
printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>' > loadprobe.csproj
```

`Program.cs`. It sends a garbage-token gRPC request to `GetSchema` `count` times and prints counts by HTTP status. The literal `not-a-token` is a placeholder, not a credential.

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
// args: url count concurrency [hostHeader|-] [xForwardedFor]
var (url, count, conc) = (args[0], int.Parse(args[1]), int.Parse(args[2]));
var host = args.Length > 3 && args[3] != "-" ? args[3] : null;
var xff = args.Length > 4 ? args[4] : null;
var handler = new SocketsHttpHandler
{
    EnableMultipleHttp2Connections = true,
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }, // kind's self-signed ingress certificate
};
using var http = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
var counts = new ConcurrentDictionary<int, int>();
var sw = Stopwatch.StartNew();
await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = conc }, async (_, ct) =>
{
    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(new byte[5]) };
    req.Content.Headers.ContentType = new("application/grpc");
    req.Headers.TryAddWithoutValidation("te", "trailers");
    req.Headers.TryAddWithoutValidation("authorization", "Bearer not-a-token");
    if (host is not null) req.Headers.Host = host;
    if (xff is not null) req.Headers.TryAddWithoutValidation("x-forwarded-for", xff);
    try { using var r = await http.SendAsync(req, ct); counts.AddOrUpdate((int)r.StatusCode, 1, (_, n) => n + 1); }
    catch { counts.AddOrUpdate(-1, 1, (_, n) => n + 1); }
});
Console.WriteLine($"elapsed={sw.Elapsed.TotalSeconds:F1}s " + string.Join(" ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")));
```

Then `dotnet build -c Release`.

- [ ] **Step 4: Stand up the isolated compose stack.** Run this with `run_in_background`, because the build can exceed 10 minutes.

```bash
source $SCR/live/env.sh
$DC up -d --build > $LIVE/up.log 2>&1; echo "up exit=$?" >> $LIVE/up.log
```

Wait until `docker inspect -f '{{.State.Health.Status}}' csr10edge-iverson-api-1` prints `healthy` (up to 10 minutes). Then confirm the API started with the guard satisfied: `docker logs csr10edge-iverson-api-1 2>&1 | grep -c "must be an https:// URL\|is required outside Development"` prints `0`.

- [ ] **Step 5: Compose check — the TLS hop.**

```bash
source $SCR/live/env.sh
docker exec csr10edge-authentik-tls-proxy-1 curl -s --cacert /etc/iverson/tls/tls.crt https://authentik-server:8443/application/o/iverson-api/.well-known/openid-configuration -o /dev/null -w '%{http_code}\n' || true
docker run --rm --network csr10edge_default -v $BR/Iverson.Server/deploy/compose-tls/ca.crt:/ca.crt:ro --entrypoint sh \
  docker.io/nginxinc/nginx-unprivileged:1.27-alpine -c \
  'curl -s --cacert /ca.crt https://authentik-server:8443/application/o/iverson-api/.well-known/openid-configuration' \
  | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d["issuer"], d["jwks_uri"])'
```

Expected:
- The second command prints `https://authentik-server:8443/` (global issuer mode) and `https://authentik-server:8443/application/o/iverson-api/jwks/`.
- The first line is informational only: it verifies against the leaf, not the CA.

- [ ] **Step 6: Compose check — real tokens through the TLS-fetched keys.** This is the conformance `identity` scenario with all five languages, exactly as E's Task 7 ran it, under the scratch `HOME`:

```bash
source $SCR/live/env.sh
( scratch_env
  export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id
  export IVERSON_CLIENT_SECRET="$(val IVERSON_LOADTEST_CLIENT_SECRET)"
  export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/
  export IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"
  export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)"
  export IVERSON_OTHER_TENANT_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)"
  cd $BR/Iverson.Server/Iverson.ClientConformance && dotnet run -- --scenarios identity ) > $LIVE/identity.out 2>&1; echo "harness exit=$?"
tail -6 $LIVE/identity.out
docker logs csr10edge-iverson-api-1 2>&1 | grep -c "IDX20803\|IDX10500\|IDX20804"
```

Expected:
- `harness exit=0`.
- The `identity` row reads `ok` for dotnet, python, typescript, go and java.
- The IDX count is `0`, meaning no metadata or signing-key retrieval failure.

- [ ] **Step 7: Compose check — per-client pre-auth partitions.** Compose sets no trusted proxies, so the header is ignored.

```bash
source $SCR/live/env.sh
U=http://127.0.0.1:8080/iverson.ObjectMappingService/GetSchema
dotnet $LIVE/loadprobe/bin/Release/net10.0/loadprobe.dll $U 60000 128          # client A, the host
dotnet $LIVE/loadprobe/bin/Release/net10.0/loadprobe.dll $U 50 8 - 198.51.100.77 # client A again, spoofing a header
docker run --rm --network csr10edge_default --entrypoint sh docker.io/nginxinc/nginx-unprivileged:1.27-alpine -c \
  "printf '\0\0\0\0\0' | curl -s -o /dev/null -w '%{http_code}\n' --http2-prior-knowledge -H 'content-type: application/grpc' -H 'te: trailers' -H 'authorization: Bearer not-a-token' --data-binary @- http://iverson-api:8080/iverson.ObjectMappingService/GetSchema"   # client B
```

Expected:
- The first run ends with a non-zero `429=` count (more than 50,000 requests within the minute) and `401=` for the rest. Record `elapsed`.
- The spoofing run is all `429`, because the header bought no fresh partition.
- Client B prints `401`, not `429`: a separate partition.
- If the first run never reaches `429`, record the `elapsed` and counts and stop and report. Don't retry with different numbers to force it.

- [ ] **Step 8: Tear compose down.**

```bash
source $SCR/live/env.sh
$DC down -v > $LIVE/down.log 2>&1; docker rmi csr10edge-api >/dev/null 2>&1; true
```

- [ ] **Step 9: The kind check.** Run the slow steps with `run_in_background`. Read the Secret values only into exported variables.

```bash
source $SCR/live/env.sh
cd $BR
KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
bash Iverson.Server/deploy/kind/setup.sh
bash Iverson.Server/deploy/kind/build-and-load-image.sh csr10edge iverson
cd Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm upgrade --install iverson . -n iverson -f values-laptop.yaml \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr10edge --set worker.image.tag=csr10edge --wait --timeout 30m
```

Then:

```bash
source $SCR/live/env.sh
kubectl get ippools.crd.projectcalico.org -o jsonpath='{.items[*].spec.cidr}'; echo
kubectl -n iverson get pods -o wide
kubectl -n iverson get pod -l app=iverson-authentik-server -o jsonpath='{.items[0].status.containerStatuses[*].name}{" "}{.items[0].status.containerStatuses[*].ready}'; echo
kubectl -n iverson get secret iverson-authentik-internal-tls -o jsonpath='{.data.ca\.crt}' | base64 -d > $LIVE/kind-ca.crt   # a public CA certificate
kubectl -n iverson port-forward svc/iverson-authentik 18443:8443 19000:9000 > $LIVE/pf-ak.log 2>&1 & echo $! > $LIVE/pf-ak.pid
kubectl -n iverson port-forward svc/iverson-api 18080:8080 > $LIVE/pf-api.log 2>&1 & echo $! > $LIVE/pf-api.pid
sleep 3
curl -s --cacert $LIVE/kind-ca.crt --resolve iverson-authentik:18443:127.0.0.1 \
  https://iverson-authentik:18443/application/o/iverson-api/.well-known/openid-configuration \
  | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d["jwks_uri"])'
```

Expected:
- The pool is `10.244.0.0/16`.
- Every pod is Running and Ready, and the authentik-server pod shows `server tls-proxy true true`.
- The `jwks_uri` is `https://iverson-authentik:18443/application/o/iverson-api/jwks/`, validated through the chart's CA.

Mint a service token with the Host header the in-cluster minters use, and call the API with it:

```bash
source $SCR/live/env.sh
umask 077
( export CID="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
  export CSEC="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-secret}' | base64 -d)"
  printf 'grant_type=client_credentials&client_id=%s&client_secret=%s&scope=admin schema_admin tenant_id_admin' "$CID" "$CSEC" \
    | curl -s -H 'Host: iverson-authentik:9000' --data-binary @- http://127.0.0.1:19000/application/o/token/ \
    | python3 -c 'import sys,json; print("authorization: Bearer "+json.load(sys.stdin)["access_token"])' > $LIVE/auth.hdr )
printf '\0\0\0\0\0' | curl -s -D - -o /dev/null --http2-prior-knowledge -H 'content-type: application/grpc' -H 'te: trailers' -H @$LIVE/auth.hdr \
  --data-binary @- http://127.0.0.1:18080/iverson.ObjectMappingService/GetSchema | grep -i -E "^HTTP|grpc-status"
rm -f $LIVE/auth.hdr
kubectl -n iverson logs deploy/iverson-api | grep -c "IDX20803\|IDX10500\|IDX20804"
```

Expected:
- `HTTP/2 200` with a `grpc-status` other than `16` (Unauthenticated). That means the token's `http://iverson-authentik:9000/` issuer and its signature validated against keys the API fetched over 8443 through the NetworkPolicy.
- The IDX count is `0`.

Per-client partitions through ingress-nginx:

```bash
source $SCR/live/env.sh
NODE=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' iverson-control-plane)
U=https://127.0.0.1:8443/iverson.ObjectMappingService/GetSchema
dotnet $LIVE/loadprobe/bin/Release/net10.0/loadprobe.dll $U 60000 128 iverson.local                 # client A, the host
dotnet $LIVE/loadprobe/bin/Release/net10.0/loadprobe.dll $U 50 8 iverson.local 198.51.100.77         # client A, spoofing a header
docker run --rm --network kind --entrypoint sh docker.io/nginxinc/nginx-unprivileged:1.27-alpine -c \
  "printf '\0\0\0\0\0' | curl -sk -o /dev/null -w '%{http_code}\n' --http2 --resolve iverson.local:443:$NODE -H 'content-type: application/grpc' -H 'te: trailers' -H 'authorization: Bearer not-a-token' --data-binary @- https://iverson.local/iverson.ObjectMappingService/GetSchema"   # client B
```

Expected, as in Step 7:
- Client A reaches `429` past 50,000 requests in the minute.
- The spoofing run is all `429`, because ingress-nginx replaces the header.
- Client B prints `401`.

The API pod is capped at 0.5 CPU on this profile. If client A never reaches `429`, record `elapsed` and counts, then **stop and report**. Don't raise limits or change values to force it.

- [ ] **Step 10: Tear kind down and diff the user's state.**

```bash
source $SCR/live/env.sh
kill $(cat $LIVE/pf-ak.pid) $(cat $LIVE/pf-api.pid) 2>/dev/null
kind delete cluster --name iverson; kind get clusters
docker rmi docker.io/library/iverson-api:csr10edge localhost/iverson-api:csr10edge >/dev/null 2>&1; true
docker ps -a --format '{{.Names}}' | sort | diff $LIVE/before-containers.txt - && echo containers-unchanged
docker volume ls -q | sort | diff $LIVE/before-volumes.txt - && echo volumes-unchanged
docker network ls --format '{{.Name}}' | sort | diff $LIVE/before-networks.txt - && echo networks-unchanged
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | diff $LIVE/before-images.txt - && echo images-unchanged
```

Expected:
- `kind get clusters` prints `No kind clusters found`.
- All four `*-unchanged` lines print. A difference limited to newly pulled base images (kind node, nginx, Calico) is acceptable; list it. A changed or removed user image or tag is not.

- [ ] **Step 11: Remove the worktree's generated files** so the worktree is clean for the merge:

```bash
rm -rf /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport/Iverson.Server/.env /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport/Iverson.Server/deploy/compose-tls
git -C /home/ben/repositories/Iverson/.worktrees/csr10-edge-transport status --short
```

Expected: no output.

---

## Known issues inherited from spec

- **Trusted-proxy residual.** A host inside a trusted load-balancer subnet, or on kind a pod in the ingress-nginx namespace, can choose its own pre-auth bucket by setting `X-Forwarded-For`. Those subnets hold only the load balancers, so this needs a foothold inside them.
- **Other plaintext hops in #5** remain plaintext: StarRocks without `SslMode`, Jaeger, TEI and Ollama, ingress-to-pod, and ingress-to-Authentik. They need a mesh or per-service TLS.
- **Edge rate limiting** (WAF, Cloud Armor, nginx limit annotations) belongs to sub-project D.
- **GCP and Azure gRPC** paths still return 400 (an existing ingress gap). The limiter fix covers whatever traffic reaches the API, including the admin API on those profiles.
- **`ExternalIssuer` never matches global-mode tokens**, because their issuer is the root path, not the app path. That is the round-10 identity-plane defect and belongs to sub-project C.
- **The Helm CA is valid for 10 years and has no automatic rotation.** Rotation is manual (§2a).

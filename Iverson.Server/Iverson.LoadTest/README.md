# Iverson.LoadTest

A console app that seeds benchmark data directly into Postgres/StarRocks and drives Iverson's
gRPC write and read paths under load, reporting latency (p50/p95/p99/p999/max) and throughput.
It exercises the same acting-user authentication and row/field authorization enforcement that
production traffic goes through — via a native Authentik OAuth2 client, not mocked auth.

## Prerequisites

- The Iverson API running and reachable at `IVERSON_GRPC_URL` (default `http://localhost:8080`)
  — e.g. `dotnet run` in `Iverson.Api`, or `docker compose up iverson-api`.
- Postgres and StarRocks reachable at `IVERSON_POSTGRES_CS` / `IVERSON_STARROCKS_CS`.
- Authentik reachable (for acting-user token minting) — required by every command except
  `clear-data` and `--help`. On first use per identity this performs a TOTP+PKCE login flow;
  the resulting access/refresh tokens are cached so subsequent runs don't repeat it.

## Commands

```
dotnet run -- <command> [options]
```

| Command | Description |
|---|---|
| `seed` | Seed benchmark data directly into Postgres and StarRocks (bulk `COPY`/batch `INSERT`, not via gRPC) |
| `write-path` | Benchmark gRPC `Post` → Kafka → consumer pipeline |
| `read-path` | Benchmark `GetMany` / `Search` / `Aggregate` via gRPC |
| `all` | Run `seed` → `write-path` → `read-path` in sequence |
| `clear-data` | Drop all StarRocks tables and truncate Postgres benchmark tables, for a greenfield re-registration |
| `acting-user-smoke-test` | Exercise the acting-user auth layer with one `Aggregate` call carrying `IVERSON_ACTING_USER_TOKEN` |

Run with no command (or `--help`) to print this usage from the tool itself.

### Options

| Flag | Default | Applies to | Meaning |
|---|---|---|---|
| `--force-reseed` | off | `seed`, `all` | Truncate and re-seed even if data already present |
| `--concurrency <N>` | `16` | `write-path`, `read-path`, `all` | Parallel tasks |
| `--count <N>` | `10000` | `write-path`, `all` | Records to post in write-path |
| `--iterations <N>` | `1000` | `read-path`, `all` | Iterations per sub-scenario in read-path |
| `--type <name>` | `Article` | `write-path`, `all` | Entity type to post: `Article`\|`Author`\|`Tag` |
| `--target <name>` | `containers` | `write-path`, `all` | `containers` (docker-compose, plaintext Kafka) or `kind` (kind/cloud Helm charts, TLS+SCRAM Kafka — see below) |

### Typical runs

```bash
# Full pipeline against docker-compose, defaults
dotnet run -- all

# Wipe everything and start clean
dotnet run -- clear-data
dotnet run -- all

# Heavier write-path run, posting Tags
dotnet run -- write-path --type Tag --count 50000 --concurrency 32

# Read-path only, fewer iterations for a quick check
dotnet run -- read-path --iterations 100
```

`clear-data` doesn't require `--force-reseed` on the following `seed`/`all` run — the tables are
already empty. It touches Postgres (`benchmark_articles`, `benchmark_authors`, `benchmark_tags`,
plus a legacy `benchmark_users` if present) and drops **every** table in the configured StarRocks
database (`SHOW TABLES` isn't scoped to benchmark tables) — don't point it at a StarRocks instance
shared with non-benchmark data. It never touches Qdrant; none of the three benchmark entities have
embedding fields, so there's nothing there to clear.

## Entities

Three entities live in `Entities/`, registered with the Iverson schema on every run of `seed`,
`write-path`, `read-path`, or `all`:

- **`BenchmarkArticle`** — `Title`, `Body` (`IversonLargeField`), `BenchmarkAuthorId` (FK to
  `BenchmarkAuthor`), `Category`/`PublishedAt` (`IversonSearchKey`, drive the StarRocks MV),
  `WordCount`, `OwnerId`.
- **`BenchmarkAuthor`** — `Name`, `Email`, `Bio`, `OwnerId`.
- **`BenchmarkTag`** — `Name`, `Category`, `OwnerId`.

`seed` targets 400,000 articles, 50,000 authors, and 10,000 tags (see `Seeding/DirectSeeder.cs`);
each `Seed*Async` method skips itself if the table already has ≥95% of its target row count,
unless `--force-reseed` is passed.

### Row/field authorization

Real `AuthorizationRules` are registered for all three entities (see `Program.cs`'s
`BuildAuthorizationRules`), so LoadTest exercises the same enforcement path production traffic
does, not a bypassed one:

- **Row ownership** via `OwnerId`. ~1% of seeded rows (`i % 100 == 0`) are stamped with the
  owner-restricted identity's real `sub` claim; the rest get a random `Guid`. This gives read-path
  a mix of rows the owner-restricted identity can and can't see.
- **Field-level restriction**, one field per entity (`Body` on Article, `Email` on Author,
  `Category` on Tag), readable/writable only by the `iverson-loadtest-bypass` role.

## Acting-user identities

Two Authentik identities share the `iverson-loadtest-human` OAuth2 client (public, PKCE),
provisioned via the Authentik blueprint charts:

- **Regular** (`iverson-acting-user-smoke-test`) — owner-restricted, no group. Its writes always
  include the field restricted to the bypass role, so the server always rejects them with
  `InvalidArgument` — this is expected and tracked as a separate `fieldRejections` counter in
  write-path output, not mixed into the error count.
- **Bypass** (`iverson-loadtest-bypass-user`, in the `iverson-loadtest-bypass` group) — has
  `RowPermission` bypass (`CanReadAll`/`CanWriteAll`/`CanDeleteAll`), so its writes/reads are never
  ownership- or field-restricted.

Every write-path and read-path request picks one of the two at random (`ActingUserIdentities.PickRandom`)
and attaches its token as a gRPC header. Tokens are minted natively (`Auth/AuthentikFlowExecutorClient.cs`
— TOTP+PKCE+flow-executor, no dependency on the Python reference script) and cached with
refresh-token renewal (`Auth/ActingUserTokenProvider.cs`), so only the first use per identity in
a given cache scope pays the full login-flow cost.

### Acting-user environment variables

| Variable | Default | Notes |
|---|---|---|
| `IVERSON_ACTING_USER_HOST_HEADER` | `authentik-server:9000` | Wire-level `Host` header for the flow-executor's HTTP requests |
| `IVERSON_ACTING_USER_CLIENT_ID` | `dev-iverson-loadtest-human-client-id` | Compose-fixed; **kind requires an override** — kind's client_id is Helm-random, not fixed |
| `IVERSON_ACTING_USER_REDIRECT_URI` | `http://localhost/placeholder-callback` | Compose-fixed; **kind requires an override** — kind's redirect_uri is ingress-derived |
| `IVERSON_ACTING_USER_USERNAME` / `_PASSWORD` | `iverson-acting-user-smoke-test` / dev password | Regular identity credentials |
| `IVERSON_ACTING_USER_BYPASS_USERNAME` / `_PASSWORD` | `iverson-loadtest-bypass-user` / dev password | Bypass identity credentials |
| `IVERSON_ACTING_USER_TOKEN` | — | Only used by `acting-user-smoke-test`: a pre-minted token to attach directly |

## Running against `kind`

`--target kind` switches write-path's Kafka client config from docker-compose's plaintext broker
to the kind/cloud Helm charts' TLS+SCRAM-SHA-512 listener, and requires:

```
IVERSON_KAFKA_SECURITY_PROTOCOL
IVERSON_KAFKA_SASL_MECHANISM
IVERSON_KAFKA_SASL_USERNAME
IVERSON_KAFKA_SASL_PASSWORD
IVERSON_KAFKA_SSL_CA_LOCATION
```

along with the kind-specific `IVERSON_ACTING_USER_CLIENT_ID` / `IVERSON_ACTING_USER_REDIRECT_URI`
overrides above. The tool refuses to start under `--target kind` if the Kafka security env vars
are missing.

## Other environment variables

| Variable | Default |
|---|---|
| `IVERSON_GRPC_URL` | `http://localhost:8080` |
| `IVERSON_CLIENT_ID` / `IVERSON_CLIENT_SECRET` / `IVERSON_TOKEN_ENDPOINT` | unset (client-credentials auth for the gRPC client itself; optional) |
| `IVERSON_POSTGRES_CS` | `Host=localhost;Port=5432;Database=iverson;Username=iverson;Password=iverson` |
| `IVERSON_STARROCKS_CS` | `Server=127.0.0.1;Port=9030;Database=iverson;Uid=root;Pwd=;` |
| `IVERSON_KAFKA_BOOTSTRAP` | `localhost:9092` |

## Output

`write-path` and `read-path` print a live progress/summary to the console and also save a full
report to `../docs/performance/results/<timestamp>-<scenario>.txt` (relative to this project's
directory, i.e. `Iverson.Server/docs/performance/results/`). `write-path` additionally reports:

- An end-to-end visibility probe (`Post` → row visible in Postgres) for a sample of posted keys.
- Kafka consumer lag across the projection consumer groups, polled until it drains or 60s elapses.

## Layout

| Path | Purpose |
|---|---|
| `Program.cs` | CLI entrypoint, DI wiring, command dispatch, `AuthorizationRules` construction |
| `Entities/` | The three benchmark entity classes |
| `Seeding/DirectSeeder.cs` | Bulk data seeding directly into Postgres/StarRocks |
| `Scenarios/WritePathScenario.cs`, `KindWritePathScenario.cs`, `WritePathRunner.cs` | write-path benchmark (compose vs. kind Kafka config; shared run logic) |
| `Scenarios/ReadPathScenario.cs` | read-path benchmark (`GetMany`/`Search`/`Aggregate`) |
| `Auth/AuthentikFlowExecutorClient.cs` | Native TOTP+PKCE+OAuth2 flow-executor client |
| `Auth/ActingUserTokenProvider.cs` | Token caching/refresh wrapper + the two-identity `ActingUserIdentities` |
| `Reporting/BenchmarkReport.cs` | HDR histogram-backed latency/throughput reporting |

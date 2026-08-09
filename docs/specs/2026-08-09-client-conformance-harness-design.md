# Cross-client conformance harness

**Date:** 2026-08-09
**Status:** Design approved, not yet planned
**Depends on:** `2026-08-09-relation-key-typing-design.md` — Go and TypeScript cannot pass this
harness's read steps until that fix lands

## Problem

Iverson ships five client libraries — .NET, Java, Python, TypeScript, Go — that must agree on a
single wire contract. Nothing verifies that they do against a running server.

The existing test estate covers two of the three necessary layers. Each client has unit tests with
a mocked transport, and `Iverson.Clients/Common/testdata/*.json` holds golden fixtures that every
language's query builder must reproduce. Both are offline. The only live-server harness is
`Iverson.LoadTest`, which is .NET-only and measures latency rather than conformance.

The cost of that gap is documented. The relation foreign-key-only work (2026-08-07) passed four
review rounds, seven per-task reviews and a whole-branch review, and still shipped a defect that
only a live depth-resolved read exposed: Python and TypeScript registered a relation whose
`PropertyName` equalled its `ForeignKey`, so the server's `EntityRelationResolver` overwrote the
foreign key with the hydrated related entity. Every suite was green throughout.

Verifying this design surfaced a second, larger instance of the same blindness — see
**Known issues**.

## Contract

The harness answers one question per client: **does this client, against a real server, register a
correct schema and round-trip an entity through create, read, update and delete without corrupting
a relation?**

It is a conformance gate, not a benchmark and not a unit test. It runs on demand before merging
work that touches a client or the wire contract.

## Architecture

A single orchestrator drives five thin drivers as subprocesses.

```
Iverson.Server/Iverson.ClientConformance/     orchestrator (.NET)
  Program.cs        CLI: --languages, --scenarios, --json <path>, --keep
  TokenBroker.cs    mints both tokens once, via Iverson.LoadTest's auth
  DriverRunner.cs   builds and execs one driver, reads its JSON output
  Verifier.cs       owns every assertion
  Scenarios/        per-scenario expectations
  Report.cs         console matrix + machine-readable JSON

Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/
Iverson.Clients/Java/conformance/             new maven module
Iverson.Clients/Python/conformance/driver.py
Iverson.Clients/TypeScript/conformance/driver.ts
Iverson.Clients/Go/conformance/main.go
```

Drivers live beside the client they exercise so dependency resolution and build tooling come free —
a Java driver is a module in the existing pom, a Go driver is a package in the existing module. The
orchestrator lives in the server tree because it references `Iverson.LoadTest` for authentication.

### Why an orchestrator rather than five native test suites

Acting-user authentication requires Authentik's flow executor: an identification stage, a password
stage, a TOTP stage with a cached secret, then PKCE and an authorization-code exchange. One
implementation of that exists, in C#. Native per-language e2e suites would need five, and would
restate every assertion five times with the drift that implies.

The orchestrator mints tokens once and passes them to drivers, which therefore need no auth code at
all. Assertions live in exactly one place.

### Driver protocol

A driver is a subprocess with a fixed contract. It has no test framework, no assertions, and no
knowledge of expected values.

**Invocation:**

```
<driver> --scenario crud-roundtrip --type PyArticle --tenant tenant-bypass \
         --grpc http://localhost:8080 --token <T> --acting-token <A> \
         --id-prefix run7a3f --out /tmp/py-crud.json
```

**Output** is one JSON document written to the `--out` path. Stdout is deliberately not used:
TypeScript's `console.log` and Java's SLF4J default output would corrupt it.

```json
{
  "language": "python",
  "steps": [
    {"name": "register", "ok": true,
     "descriptor": {"relations": [{"propertyName": "PyAuthor",
                                   "foreignKey": "PyAuthorId",
                                   "kind": "many_to_one"}],
                    "properties": ["Id", "Title", "TenantId", "PyAuthorId"]}},
    {"name": "write",  "ok": true, "key": "…"},
    {"name": "get",    "ok": true, "entity": {"id": "…", "pyAuthorId": "c60c…"}},
    {"name": "update", "ok": true},
    {"name": "delete", "ok": true}
  ]
}
```

Three properties make this work:

- **Drivers report, never assert.** Correctness lives in one place and cannot drift.
- **Drivers use only their client's public API** — `SchemaRegistrar`, `EntityCoordinator` — so they
  exercise what real users touch, not raw gRPC.
- **`entity` is the driver's own typed object after deserialization**, serialized to JSON. This is
  the only way to catch a read path that finds the right key and drops the value, which is the
  shape of the Python `list_value` → `None` defect.

A step that throws sets `"ok": false` with `"error"`, and the driver still exits 0 — a failed step
is data. A non-zero exit means the driver itself broke.

Roughly 150 lines per language must be written five times. That duplication is the irreducible cost
of exercising five separate client libraries; everything above it is written once.

### Registration and authorization are separate steps

Only .NET's `SchemaRegistrar` accepts authorization rules (`authorizationByTypeName`); TypeScript
hardcodes `authorization: undefined` and Python, Go and Java have no support (A7). Because
`RowFieldAuthorizationEvaluator` returns `Denied` when a schema's rules are null
(`RowFieldAuthorizationEvaluator.cs:11-12`), a schema registered by those four clients is writable
by nobody.

The harness therefore registers twice. The driver registers via its own client — that descriptor is
what is under test — and the orchestrator then re-registers the same shape with row permissions
attached. Re-registration with an unchanged column shape is idempotent (A12).

## Scenarios

**S1 — `crud-roundtrip`** (per language, own types). Two types: `{Lang}Author` carrying a
`one_to_many`, and `{Lang}Article` carrying a `many_to_one` to it plus a `many_to_many` to
`{Lang}Tag`.

| Step | Proves |
|---|---|
| register both types | FK column declared under the inferred name; `one_to_many` declares none |
| write author, tag, article with both FKs | m2o FK persists; m2m ids arrive as a `ListValue`, not a string |
| get article (depth 0) | FK reads back into the client's own typed member |
| *orchestrator* reads at depth 1 | FK survives hydration and the nav property appears beside it |
| update title | write path works against an existing row |
| delete | row gone; a subsequent get reports not-found |

The many-to-many leg is deliberate: Go, Python and TypeScript each had a value-ladder gap there, and
Go's write path serialized slices as `null`.

The depth-1 step belongs to the orchestrator, not the driver, because only .NET exposes a
depth-taking read (A6). This is the right home regardless: the `PropertyName`/`ForeignKey` collision
lives in the registered *descriptor*, which is per-type rather than per-client, so a
Python-registered type is corrupted when read at depth by any consumer. The driver's job is to
register the descriptor; the orchestrator's is to read it back.

**S2 — `naming-rejected`** (Go, Python, TypeScript — negative). The driver attempts to register a
type whose `many_to_one` member is misnamed (`writer_id` against an `Author`). Registration must
fail client-side before any RPC; the driver reports the failure and the orchestrator asserts the
message names both the actual and the required name. .NET and Java are skipped with a recorded
reason — their foreign key is a separate declared field, so the server's registration check governs
them instead.

**S3 — `nav-property-rejected`** (orchestrator only). No client can produce this payload any more,
which is the point of the FK-only work, so the orchestrator hand-builds a `Struct` containing a
navigation-property key and posts it over raw gRPC, asserting `InvalidArgument` with a message
naming both the property and the foreign key.

**S4 — `interop`** (shared type, all languages). The .NET driver registers `SharedAuthor` and
`SharedArticle` once. Every language writes one row keyed `shared-<lang>-<runid>`, then every
language reads all five rows. The orchestrator asserts all twenty-five reads agree on the foreign
key value.

S4 is the only scenario that can catch two clients disagreeing about the wire format while each
passes its own isolated test. Its cost is a second entity declaration per driver.

**Isolation.** Every scenario takes an `--id-prefix` run id. Type names stay stable so schema drift
detection remains meaningful across runs; row keys are prefixed so runs never collide. The
orchestrator deletes its rows on completion unless `--keep` is passed.

## Verification

For each entity the orchestrator compares three independent observations and requires agreement:

| Source | Answers |
|---|---|
| the driver's reported entity | did the client repopulate its own typed member? |
| the orchestrator's own `MappingGet` | what does the server return on the wire? |
| the orchestrator's Postgres query | what is actually stored in the column? |

Which pair disagrees localizes the defect. Driver versus gRPC isolates the client's read path;
gRPC versus Postgres isolates the server's read path, which is precisely the depth-1 clobbering
where storage was correct and the response was not; both agreeing but differing from what was
written isolates the write path.

Table names are derivable as `ToSnakeCase(TypeName) + "s"` (`SchemaBuilder.cs:30`), so the Postgres
query needs no configuration.

Registration is verified against the descriptor the driver reports: for each non-`OneToMany`
relation, `propertyName != foreignKey`, the foreign key appears among the declared properties, and
`isArray` is set only for `many_to_many`.

## Reporting

The console gets a matrix — languages down, scenarios across, `ok` / `FAIL` / `skip` / `xfail`.
Each failure prints the assertion, the three observed values, and the driver's captured stderr.
`--json <path>` writes the same content machine-readably. The exit code is 0 only when every
non-skipped, non-expected-fail cell passed.

A skip is never silent: skips carry a reason and render distinctly from passes, so
`naming-rejected` being inapplicable to Java reads as a deliberate exclusion rather than a green
tick.

## Expected failures

Go and TypeScript are marked **expected-fail on every read step** of S1 and S4, and all three
FK-on-field clients (Go, Python, TypeScript) are expected-fail on any step exercising a
`one_to_many` read, with the cause recorded in the report. Both are resolved by
`2026-08-09-relation-key-typing-design.md`.

They run rather than being skipped, so the harness continues to report the defects instead of
hiding them. When that spec lands, removing the `xfail` marks is the regression test — and if the
harness is built first, its first green run is the proof that the fix worked.

## Lifecycle

The harness assumes the stack is already running, matching `Iverson.LoadTest`'s existing contract
rather than inventing a second convention. A preflight checks API reachability, Authentik
reachability and Postgres connectivity, failing with a message that names what is down and the
command to start it. It does not manage compose; a harness that owns a twelve-service stack is a
second product.

Missing toolchains degrade rather than crash: absent `mvn` makes Java's row `skip (mvn not found)`
while the other four still run.

Tenant provisioning calls the `TenantLifecycle` service directly. `Iverson.LoadTest`'s
`EnsureTenantProvisionedAsync` is a static local method in `Program.cs` and is not reachable from
another project (A3); the call is about ten lines and duplicating it is cheaper than refactoring
working code.

## CI readiness

The harness is built for local execution but keeps four constraints that make CI possible later
without redesign: machine-readable output, meaningful exit codes, no interactive prompts, and the
TOTP secret read from `IVERSON_TOTP_SECRET` when set, falling back to the existing
`~/.cache/iverson` file. That environment variable is the only thing standing between this design
and a CI runner, and honouring it costs one null-coalescing operator at
`AuthentikFlowExecutorClient.cs:146`.

## Testing the harness

Every scenario must be demonstrated to fail. During implementation each assertion is checked by
breaking the thing it guards — reverting Python's relation-property-name helper must turn S1's
depth-1 check red; stubbing out Go's slice branch must turn the many-to-many leg red — and the
evidence goes in the implementation report.

This is not optional ceremony. The work that motivated this harness produced three tests that could
not fail, and its final review found real defects only once it was instructed to mutate. A
conformance harness carries the same exposure amplified, because it is a large green tick that
people will trust.

## Consequences

**Five drivers must be maintained.** A change to any client's public API may require a driver
change in that language. This is the standing cost of the design.

**The harness is red on its first run, by design.** Go and TypeScript fail every read step, and all
three FK-on-field clients fail `one_to_many` reads, until
`2026-08-09-relation-key-typing-design.md` lands. That is the correct outcome: the harness reports
what is true.

**The harness will find more than it was built for.** Verifying this design alone surfaced a
shipping defect that all prior review layers missed.

## Verified assumptions

Fifteen assumptions, enumerated against the design before verification and checked against the
codebase and a running stack. Eleven held.

| # | Assumption | Result |
|---|---|---|
| A1 | LoadTest's auth types are public and referencable | ✅ `AuthentikFlowExecutorClient`, `ActingUserTokenProvider` are `public sealed` |
| A2 | A project can reference `Iverson.LoadTest` (an Exe) | ✅ `net10.0` Exe; legal in .NET |
| A3 | LoadTest's tenant provisioning is reusable | ⚠️ **PARTIAL** — `EnsureTenantProvisionedAsync` is a static local in `Program.cs`. The orchestrator calls `TenantLifecycle` directly instead |
| A4 | The TOTP secret has one read point, so an env fallback is local | ✅ `LoadCachedTotpSecret()` at `AuthentikFlowExecutorClient.cs:146` |
| A5 | All five clients expose register/write/get/update/delete | ✅ verified per client |
| A6 | All five can issue a depth-resolved read | ❌ **FAILED** — only .NET, via `EntityCoordinator.GetMappedAsync(key, depth)`. Python/TS/Go/Java expose no depth parameter. Design updated: the depth check moves to the orchestrator |
| A7 | All five registrars can declare authorization rules | ❌ **FAILED** — only .NET (`authorizationByTypeName`). TypeScript hardcodes `authorization: undefined`; Python, Go, Java have none. With `rules is null → Denied`, four clients register unwritable schemas. Design updated: the orchestrator re-registers with permissions |
| A8 | All five can declare a GUID-typed key and FK | ❌ **FAILED for Go and TypeScript** — Go's `goScalarToClr` maps `reflect.String → CLR_STRING`; TypeScript's `jsTypeToClr` has no Guid case and defaults to `CLR_STRING`. Python (`uuid`/`UUID`), Java (`java.util.UUID`) and .NET map to `CLR_GUID`. See Known issues |
| A9 | All five support `many_to_many`, `one_to_many` and a tenant field | ✅ |
| A10 | Naming enforcement raises a catchable client-side error | ✅ observed live — Python raised `ValueError` naming both the actual and required name before any RPC |
| A11 | Postgres table naming is derivable | ✅ `SchemaBuilder.cs:30` — `ToSnakeCase(TypeName) + "s"` |
| A12 | Re-registering an identical shape is idempotent | ✅ re-ran a full registration cycle against existing types with no reset; passed |
| A13 | The five toolchains are available locally | ✅ dotnet 10.0.110, mvn 3.9.9, python3 3.14.4, npm 10.9.2, go, java 21.0.5 |
| A14 | Client libraries leave stdout clean | ⚠️ **RISK** — TypeScript `console.log` and Java SLF4J may write to stdout. Design updated: drivers write JSON to `--out <path>` |
| A15 | Referencing LoadTest's auth from a second project breaks nothing | ✅ no dependents affected |

## Known issues / accepted as out of scope

**Two live defects found while verifying this design are fixed elsewhere.** Both were confirmed
against a running stack on 2026-08-09 and are specified in
`2026-08-09-relation-key-typing-design.md`, which **Ben chose on 2026-08-09** to split out rather
than fold in — it is a production correctness fix, not a test tool, and warrants its own review.

- **Go- and TypeScript-registered entities cannot be read by key.** Neither client maps any type to
  `CLR_GUID` (A8), so their key columns are `text`, and `EntityRepository` hardcodes `@Key::uuid`.
  A text-keyed type accepted a write, then failed both `depth=0` and `depth=1` reads.
- **One-to-many resolution is broken for Go, Python and TypeScript.** All three synthesize their
  relation foreign key as `CLR_STRING` → a `TEXT` column, while `EntityRelationResolver:154`
  resolves the reverse direction through `FetchByColumnAsync`, which casts to uuid.

The second was introduced by the foreign-key-only work days earlier and passed every review layer,
because the many-to-one direction — the one exercised live — is unaffected. Finding it during the
design of a conformance harness, rather than by running one, is the argument for building it.

**The harness does not manage the docker compose stack.** It verifies the stack is up and fails with
instructions otherwise.

**CI execution is not implemented**, only kept possible. Seeding the TOTP secret on an ephemeral
runner remains unsolved and is the one genuine obstacle.

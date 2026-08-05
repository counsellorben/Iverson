# Critical Implementation Review: 2026-08-04-retrieval-quality-benchmark-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-04-retrieval-quality-benchmark-implementation-plan.md`
**Verified plan-level assumptions section:** present (P1–P21)

⚠️ 1 commit since plan-write time (SHA `ac0f27f`); cited file:line references re-checked under §1. The single commit is `a97b075`, the plan's own commit — no code changed between spec-write and review.

## 0. Coverage enumeration

**Task 1 — test project, models, parsers**

| Surface | Disposition |
|---|---|
| Step 1 code/prose (csproj) | ok — package set matches `Iverson.Vector.Tests.csproj` exactly (xunit 2.9.3, runner 2.8.2, Test.Sdk 17.12.0, FluentAssertions 7.0.0, coverlet 6.0.2, `net10.0`, `IsTestProject`); omitting NSubstitute/Testcontainers is consistent with the tests described |
| Step 2 code (slnx line) | ok — path form `Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj` matches the 14 existing `<Project Path=…/>` entries, which are relative to the `.slnx` directory |
| Step 3 code (models) | ok — three records, no external types; `Qrel.Subtopic` doc-comment matches spec A3's iteration-field convention |
| Step 4–5 prose (parser formats) | ok — BEIR's `_id`/`title`/`text` + qrels TSV is stated concretely; FreshStack's layout is explicitly deferred to the downloaded dataset with the public shape pinned. Consistent with spec A2, which already records corpus facts as unfetched |
| Step 6 prose (tests) | ok — four named cases, each falsifiable; no assertion-free tests |
| Step 7 command | ok — `dotnet test Iverson.Server/Iverson.LoadTest.Tests` matches the path Step 1 creates |
| Step 8 command | ok — `git add` names the three created paths plus the modified slnx; no `-A` |

**Task 2 — entity and authorization**

| Surface | Disposition |
|---|---|
| Step 1 code block | ok — `[IversonEntity]`, `[IversonKey] Guid Id`, `[IversonTenant] string`, dual `[IversonEmbedding]`+`[IversonChunk]` all confirmed against `Entities/BenchmarkArticle.cs:5,8,16` and `SchemaRegistrar.cs:186-200` (two independent `if`s, so dual annotation is additive) |
| Step 2 code (auth entry) | ok — `BuildAuthorizationRules(string restrictedField)` at `Program.cs:277` takes exactly one arg; dictionary literal at `:147-152` is additive |
| Step 3 command/prose | dropped — `seed` does register schemas (`needsTenantAndSchema` includes it), so the step works. It is heavyweight (DirectSeeder, `--count` default 10,000) and the plan doesn't pass `--count`, but a wasteful verification step does not make the spec's outcome wrong. Not literal-wrongness |
| Step 4 command | ok — names the two touched files |

**Task 3 — ingest scenario**

| Surface | Disposition |
|---|---|
| Step 1 prose/code (KeyMap) | ok — **persistence boundary flagged and checked**: T3 writes the value returned by `PersistAsync`, T4 reads `ChunkSearchResponse.ParentKey`; both are the same entity key (`IntelligenceStoreConsumer.cs:253` — `["parent_id"] = ev.Key`), so the map's key space matches on both sides |
| Step 2 code block | ok — `PersistAsync(T, Metadata?, CancellationToken)` returns `Task<string?>` (`EntityCoordinator.cs:98-111`); `new Grpc.Core.Metadata().WithActingUser(...)` matches `WritePathRunner.cs:75`; `EntityCoordinator<BenchmarkDocument>` resolves via the open-generic `services.AddTransient(typeof(EntityCoordinator<>))` (`ServiceCollectionExtensions.cs:75`) with no per-type wiring |
| Step 2 prose (unset `Id`) | ok — leaving `Id` default is safe: the server assigns when the key is blank **or** `Guid.Empty.ToString()` (`ObjectMappingGrpcService.cs:301-304`). The all-zeros-collision failure this could have had does not occur |
| Step 3 prose (parameter sourcing) | → §2.1 |
| Step 4 prose/code (wiring) | → §2.1, → §2.2 |
| Step 5 command | ok — names the three touched paths |

**Task 4 — query scenario, aggregation, run files**

| Surface | Disposition |
|---|---|
| Step 1 prose (aggregator signature) | ok — signature is self-consistent with its Step 5 call site (`limit: 50`) |
| Step 2 prose (tests) | ok — three cases covering max-not-first, ordering, truncation; both failure directions of the aggregation rule |
| Step 3 prose (TREC writer) | ok — pure formatting, rank base 1 stated |
| Step 4 code block | ok — `search.SearchSimilar(req, headers, cancellationToken: ct)` matches the generated client's `(request, Metadata, DateTime?, CancellationToken)` shape used at `ReadPathScenario.cs:235`; `Query.Similar<T>(d => d.Body)` resolves through `PropertyNameObj`'s `Convert` case for a string property; `AsyncServerStreamingCall` is disposable |
| Step 4 code (`Data.Fields["docId"]`) | ok — **second identifier in the same block, checked separately**: `DocId` is a scalar column, and scalar columns land in the object point as `col.Name.ToCamelCase()` (`IntelligenceStoreConsumer.cs:377-384`) → key is `docId`. Field masking does not strip it: the bypass role is in `ReadableRoles`, so `excluded.Count == 0` and `AllowedFields` stays null (`RowFieldAuthorizationEvaluator.cs:56-77`) |
| Step 5 prose (two budgets) | ok — matches spec A22; `ChunkBudgetMultiplier` declared once as `private const int`, satisfying the Global Constraint that it be held constant |
| Step 6 prose/code (wiring) | → §2.1, → §2.2 |
| Step 7–8 commands | ok — test path matches; `git add` names touched paths |

**Cross-task interface contracts**

| Contract | Disposition |
|---|---|
| T1 → T3: `CorpusDocument`, `BeirCorpusParser` | ok — T3 consumes `DocId`/`Title`/`Text`, all three defined in T1 Step 3 |
| T1 → T4: `CorpusQuery` | ok — T4 reads `q.Text`; defined in T1 Step 3 |
| T2 → T3/T4: registered `BenchmarkDocument` | → §2.2 (the type is defined, but never registered on the new commands' path) |
| T3 → T4: key map, **crosses a persistence boundary** | ok — see T3 Step 1 row; keys match on both sides |
| T1 → T4: `MaxPassageAggregator` keyMap parameter | ok — the aggregator's `keyMap` parameter type matches what `KeyMap.LoadAsync` returns (`Dictionary<string,string>`) |

**Rule-like content**

| Rule | Disposition |
|---|---|
| Max-passage aggregation, both directions | ok — over-inclusion: one entry per parent by construction (group-by); under-inclusion: every parent with ≥1 chunk appears, and truncation is explicit and tested |
| Chunk-budget multiplier | ok — mechanics, not a calibrated value: it is a single `const` read at one call site and pinned by a Global Constraint |
| Server key assignment | ok — both branches checked; see T3 Step 2 prose row |

## 1. Verified-plan-assumptions cross-check

P1–P21 **all still hold**. The plan was written at this same commit, so this is a re-read rather than a re-derivation; the ones re-read fresh this round are P3 (slnx, 14 projects, no test entry), P4/P5 (`EntityCoordinator.cs:98-111`, `:204-206`, `:222-224`), P9 (`ObjectSearchGrpcService.cs:271-273`), P10 (`InternalsVisibleTo` — `Iverson.Client.Core.Tests` only), P11 (`IntelligenceStoreConsumer.cs:253`), P14 (`Iverson.Vector.Tests.csproj`), P16 (`Program.cs:168-190`) and P19 (`Program.cs:147-152`).

P21 remains correctly labelled as not grounded in repo evidence; nothing in this round changes that, and the plan already carries the fallback.

**Span check — two uncovered dependencies, both verified in-round and promoted to §2:**

- Nothing in P1–P21 or "Inherited from spec" covers the **shape of `CommandFlags`**, yet three steps source their parameters from it. → §2.1
- Nothing covers the **`needsTenantAndSchema` gate**, yet both new commands depend on tenant provisioning and schema registration having run. → §2.2

## 2. Literal-wrongness findings

### 2.1 Three steps read parameters from `CommandFlags` that it does not have, and no task adds them

**Description.** Task 3 Step 3 says "Take the corpus name and paths from `CommandFlags`", Task 3 Step 4 says to write the map "next to the run-file output directory", and Task 4 Step 6 says "Take the configuration label (for the run tag), the key-map path and the output directory from `CommandFlags`." `CommandFlags` has none of those fields, and no task in the plan modifies it — the File Structure's `Modify: Program.cs` line lists only "auth-dictionary entry, DI registrations, two switch cases".

At execution time the implementer of Task 3 and the implementer of Task 4 are separate subagents. Each must invent the missing flags independently, and the second has no way to know what the first chose — so the key-map path Task 3 writes to and the one Task 4 reads from are set by two unconnected decisions. The likely outcome is hardcoded paths in one or both tasks, which silently breaks the map handoff the whole run depends on.

**Evidence.**
- `Program.cs:330-347` — `CommandFlags` is `ForceReseed`, `Concurrency`, `Count`, `Iterations`, `Type`, `Target`. No corpus path, output directory, key-map path, or configuration label.
- `Program.cs:339-347` — `Parse` is a fixed initializer; `StrFlag`/`IntFlag` helpers exist at `:349+`, so extending it is mechanical.
- Plan File Structure, `Modify` bullet — does not mention `CommandFlags`.

**Proposed fix.** Add an explicit step to **Task 3** (the first task that needs them) extending `CommandFlags` and `Parse` with the fields both later tasks read, and name them in the plan so Task 4 consumes the same identifiers:

```csharp
public string CorpusPath    { get; init; } = "";
public string OutputDir     { get; init; } = "";
public string KeyMapPath    { get; init; } = "";
public string ConfigLabel   { get; init; } = "";
```

with matching `StrFlag(args, "--corpus-path", "")` entries. Then change Task 4's Interfaces block to declare `Consumes: Task 3's CommandFlags fields (KeyMapPath, OutputDir, ConfigLabel)`, so the contract is visible to the Task 4 subagent rather than implied.

### 2.2 The two new commands never trigger tenant provisioning or schema registration, so ingest fails on its first write

**Description.** Tenant provisioning and schema registration are gated on a hardcoded command list. `benchmark-ingest` and `benchmark-query` are not in it, and no task adds them. Running `benchmark-ingest` therefore skips both: no tenant is provisioned, and `BenchmarkDocument` is never registered with the server — so the very first `PersistAsync` targets an unregistered type.

This is not a degraded result; it is a hard stop at the first document of a 59K-document ingest. It also makes Task 2 Step 3's verification misleading in a specific way: that step proves registration works *under `seed`*, which is in the gate list, so the plan's own check passes while the path the benchmark actually uses is broken.

**Evidence.**
- `Program.cs:82` — `var needsTenantAndSchema = command is "seed" or "write-path" or "read-path" or "all";`
- `Program.cs:85` — tenant provisioning is inside `if (needsTenantAndSchema && clientCredentials is not null)`.
- `Program.cs:142` — `if (needsTenantAndSchema)` guards the whole `Registering schemas...` block including `RegisterAllAsync`.
- Plan Task 3 Step 4 and Task 4 Step 6 add only `switch` cases; nothing touches line 82.

**Proposed fix.** In Task 3 Step 4, alongside the `switch` case, extend the gate:

```csharp
var needsTenantAndSchema = command is "seed" or "write-path" or "read-path" or "all"
    or "benchmark-ingest" or "benchmark-query";
```

`benchmark-query` needs it too — it does not write, but it resolves the schema server-side on every search and runs as the acting user the tenant provisioning creates.

With that in place, Task 2 Step 3's verification should invoke the benchmark path rather than `seed`, so the check exercises the same gate branch the benchmark will use. (Until Task 3 exists, `seed --count 1` is the light equivalent — the plan currently passes no `--count` and would seed 10,000 entities to check one registration.)

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.**

Both findings came from the span check rather than from a failed assumption: P1–P21 are individually true, and both defects live in facts about `Program.cs` that no assumption was scoped to cover — its flag record and its registration gate. §2.2 in particular would stop the ingest at its first write, and the plan's own verification step would not have caught it, because that step exercises a command on the working side of the gate.

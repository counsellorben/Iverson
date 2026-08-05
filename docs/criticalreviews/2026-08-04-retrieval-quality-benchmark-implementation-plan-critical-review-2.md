# Critical Implementation Review: 2026-08-04-retrieval-quality-benchmark-implementation-plan (Round 2)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-04-retrieval-quality-benchmark-implementation-plan.md`
**Verified plan-level assumptions section:** present (P1–P23)

⚠️ 3 commits since plan-write time (SHA `ac0f27f`); cited file:line references re-checked under §1. All three are plan/review documents (`a97b075`, `c46d498`, and this round's inputs) — no code changed.

The §0 enumeration below was rebuilt from the plan before re-reading round 1, so round 1's two fixes appear as ordinary rows rather than as the search area.

## 0. Coverage enumeration

**Task 1**

| Surface | Disposition |
|---|---|
| Step 1 csproj | ok — re-derived against `Iverson.Vector.Tests.csproj`: versions, `IsTestProject`, `net10.0` all match |
| Step 2 slnx line | ok — relative-path form matches the 14 existing entries; adding a 15th is additive |
| Step 3 models code | ok — three records; no external type referenced |
| Step 4 prose (BEIR format + qrels header skip) | ok — `_id`/`title`/`text` and the `query-id/corpus-id/score` TSV header are BEIR's published shape; consistent with spec A2, which records corpus facts as unfetched |
| Step 5 prose (FreshStack deferred) | ok — public shape pinned to BEIR's, format deferred to the dataset; the plan is explicit that this is unverified rather than silently assumed |
| Step 6 prose (tests) | ok — four falsifiable cases named |
| Step 7–8 commands | ok — test path matches Step 1's project; `git add` names created paths |

**Task 2**

| Surface | Disposition |
|---|---|
| Step 1 entity code | ok — every attribute re-confirmed against `Entities/BenchmarkArticle.cs:5,8,16`; dual annotation additive per `SchemaRegistrar.cs:186-200` |
| Step 2 auth entry | ok — `BuildAuthorizationRules(string)` arity matches `Program.cs:277` |
| Step 3 command + gate caveat | ok — `seed --count 1` is valid (`--count` parsed at `Program.cs:343`); the caveat about the gate is accurate |
| Step 4 command | ok — names both touched files |

**Task 3**

| Surface | Disposition |
|---|---|
| Step 1 KeyMap prose | ok — persistence boundary re-checked; keys match on both sides (`PersistAsync` return ↔ `parent_id` from `IntelligenceStoreConsumer.cs:253`) |
| Step 2 scenario code | ok — `PersistAsync(T, Metadata?, ct)` and the `WithActingUser` pattern re-confirmed at `EntityCoordinator.cs:98-111` and `WritePathRunner.cs:75`; `EntityCoordinator<>` open-generic registration at `ServiceCollectionExtensions.cs:75` |
| Step 2 prose (`TenantId`) | dropped — the plan says to set it to "the tenant id the other scenarios use", and grep shows the other scenarios never assign `TenantId` at all; tenant is server-derived (`ObjectMappingGrpcService.cs:312` passes `decision.TenantValue`; `IntelligenceStoreConsumer.cs:116-117` re-derives authoritatively). The instruction is imprecise, but following it yields the provisioned tenant id (`Program.cs:44`), which is correct under either mechanism. Not literal-wrongness |
| Step 2 prose (failure handling) | ok — counts failures, continues, fails the run at the end; a partial corpus is not silently accepted |
| Step 3 prose (BEIR first) | ok — ordering instruction only; `CorpusPath` now concrete |
| Step 4 code (`CommandFlags`) | ok — field names match Task 4's Interfaces block; `StrFlag` helper exists at `Program.cs:349+` |
| Step 5 code (gate + switch + DI) | ok — gate line matches `Program.cs:82` verbatim; switch shape matches `:168-190` |
| Step 5 prose (ingest completion) | → §2.1 |
| Step 6 command | ok — stages `Program.cs`, which carries the flag and gate edits |

**Task 4**

| Surface | Disposition |
|---|---|
| Step 1 aggregator prose | ok — signature self-consistent with the Step 5 call site |
| Step 2 tests prose | ok — both failure directions plus truncation |
| Step 3 TREC writer prose | ok — field order and rank base stated; whitespace-separated is what `ir_measures` reads |
| Step 4 code block | ok — client call shape matches `ReadPathScenario.cs:235`; `Query.Similar<T>(d => d.Body)` resolves through `PropertyNameObj`'s `Convert` case |
| Step 4 code (`Data.Fields["docId"]`) | ok — re-checked independently of the block's other identifiers: `DocId` is a scalar column and scalar columns land as `col.Name.ToCamelCase()` (`IntelligenceStoreConsumer.cs:377-384`); masking cannot strip it for the bypass role (`RowFieldAuthorizationEvaluator.cs:56-77`) |
| Step 5 prose (two budgets) | ok — matches spec A22; multiplier is a single `const` pinned by a Global Constraint |
| Step 6 code (switch + flags) | ok — consumes the exact field names Task 3 Step 4 defines |
| Step 7–8 commands | ok |

**Cross-task interface contracts**

| Contract | Disposition |
|---|---|
| T1 → T3 `CorpusDocument` / parsers | ok — `DocId`/`Title`/`Text` all defined in T1 Step 3 |
| T1 → T4 `CorpusQuery` | ok — T4 reads `q.Text` |
| T2 → T3/T4 registered type | ok — the gate fix means both benchmark commands now reach `RegisterAllAsync` |
| T3 → T4 key map, **crosses a persistence boundary** | ok — key spaces match; path now carried by `KeyMapPath` on both sides |
| T3 → T4 `CommandFlags` fields | ok — `KeyMapPath`, `OutputDir`, `ConfigLabel` defined in T3 Step 4 and named in T4's Interfaces block |
| T3 → T4 **the indexed corpus itself** (the artifact the query operation actually reads) | → §2.1 — the only cross-task artifact whose *readiness*, not just whose shape, the plan never establishes |

**Rule-like content**

| Rule | Disposition |
|---|---|
| Max-passage aggregation, both directions | ok — group-by prevents over-inclusion; every parent with ≥1 chunk appears; truncation explicit and tested |
| Chunk-budget multiplier | ok — mechanics pinned by a Global Constraint, not a calibrated value |
| Server key assignment | ok — `Guid.Empty.ToString()` branch handled (`ObjectMappingGrpcService.cs:301-304`) |
| Schema re-registration on each of the 8 query runs (new condition created by round 1's gate fix) | ok — checked because the gate fix makes `benchmark-query` re-register every run: Qdrant collection lifecycle lives in the **consumer** (`IntelligenceStoreConsumer.cs:144,162`), not in registration, and the migration path copies points before deleting (`IntelligenceCollectionManager.cs:117-135`). Re-registering an unchanged schema cannot destroy the ingested corpus |

## 1. Verified-plan-assumptions cross-check

P1–P23 **all still hold**, including P22 and P23 added by round 1. Re-read fresh this round: P4/P5 (`EntityCoordinator.cs:98-111`, `:204-206`, `:222-224`), P9 (`ObjectSearchGrpcService.cs:271-273`), P11 (`IntelligenceStoreConsumer.cs:253`), P16 (`Program.cs:168-190`), P22 (`Program.cs:330-347`) and P23 (`Program.cs:82`, `:85`, `:142`).

P21 remains correctly labelled as not grounded in repo evidence, with its fallback intact.

**Span check — one uncovered dependency, verified in-round and promoted to §2:**

- Every assumption covers the *shape* of what passes between tasks — types, fields, flags, paths. Nothing covers the *timing* of the corpus becoming queryable: that `PersistAsync` returning is not the same event as the document being embedded and searchable. → §2.1

## 2. Literal-wrongness findings

### 2.1 Ingest returns long before the corpus is queryable, and nothing in the plan waits for or checks the drain

**Description.** `PersistAsync` returns once the API has written the outbox row and published to Kafka. The work the benchmark actually measures — chunking, embedding, centroid computation, Qdrant upsert — happens afterwards and asynchronously in `IntelligenceStoreConsumer`. Task 3 declares itself complete when the last `PersistAsync` returns, and Task 4 queries a corpus it assumes is indexed.

On this corpus the gap is not incidental. ~59K documents chunk into substantially more embedding calls through CPU Ollama — the spec's own A10 flags the duration as the design's largest unknown. Any `benchmark-query` run started before the intelligence consumer drains scores a partially-indexed corpus.

The failure is silent and it corrupts the result rather than stopping it. The run files are well-formed; the queries return real hits; the numbers are simply computed over a fraction of the corpus. Worse, because the eight configurations are run sequentially against one ingest, an early configuration can be scored against fewer documents than a later one — so the ablation compares configurations that saw different corpora, which is precisely the comparison the sweep exists to make valid. The spec's non-empty-run-file check (§7) does not catch this: a partial index produces a non-empty file.

**Evidence.**
- `EntityCoordinator.cs:98-111` — `PersistAsync` returns `response.Key` as soon as the RPC completes; no indexing guarantee.
- `IntelligenceStoreConsumer.cs:144,162` — collection ensure, and by extension chunk/embed/upsert, happen on the consumer, downstream of Kafka.
- `WritePathRunner.cs:202-232` — `PrintKafkaLagAsync` exists and reports lag for consumer groups including `iverson.consumer.intelligence`; `DirectSeeder.cs:49` calls it at the end of a write run. It **prints** lag; it does not wait.
- The plan's Task 3 has no lag step, and Task 4 Step 4 begins querying with no readiness precondition.

**Proposed fix.** Add a final step to Task 3, after the map is saved: poll `iverson.consumer.intelligence` lag to zero before reporting success, reusing the existing probe rather than inventing one. `PrintKafkaLagAsync` is `internal static` on `WritePathRunner` and already builds the admin/consumer clients needed; the smallest change is to extract the lag figure it computes into a value the ingest scenario can loop on — poll every N seconds, print the remaining lag so a multi-hour drain is visible rather than looking hung, and exit only at zero.

Lag reaching zero means the consumer has *consumed* every message; if that proves insufficient in practice, the direct check is Qdrant point count against documents ingested. State whichever is chosen in Task 3 so Task 4 can rely on it, and add one line to Task 4 Step 4 recording that the query scenario assumes a fully drained ingest.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** (three steps read `CommandFlags` fields that did not exist) is resolved. Task 3 Step 4 now defines `CorpusPath`, `OutputDir`, `KeyMapPath` and `ConfigLabel` with `StrFlag` parse entries; Task 4's Interfaces block names the three it consumes; both prose references now cite the concrete fields; P22 records the original shape.
- **Round 1 §2.2** (new commands never triggered tenant provisioning or schema registration) is resolved. Task 3 Step 5 extends `needsTenantAndSchema` with both commands, Task 2 Step 3 carries the caveat that `seed` exercises a different branch of that gate, and P23 records the gate.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.**

One finding, and it is the first in two rounds that is purely dynamic: every static contract between Tasks 3 and 4 is now sound — types, fields, flags, paths and key spaces all line up. What no assumption covers is that the artifact Task 4 reads is not ready when Task 3 returns. Both prior rounds checked what passes between tasks; this one is about when.

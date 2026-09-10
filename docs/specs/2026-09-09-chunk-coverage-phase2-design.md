# Chunk-Coverage Signal, Phase 2 (staged) — the primary gate

Phase 1 measured the tail. This phase asks the question the measurement was for: **does any β on the
ladder beat β = 0 on nDCG@10 for FreshStack-2048 at λ = 1.00?** Nothing else.

Parent design: `docs/specs/2026-09-08-chunk-coverage-signal-design.md` (the ladder construction is its
§2, the gate rules its §6, the corpora its §5). Phase 1's result:
`docs/plans/2026-09-GATE-chunk-coverage.md`.

## 1. Why this is staged

Spec §5 lists four corpora and §6 adds a λ = 0.70 arm, but the **primary** gate is FreshStack-2048 at
λ = 1.00 alone. §6 requires the ordering check to pass "before the confirmation arm is read as a gate
result" — and the confirmation arm only runs **if a β qualifies**. So the work splits: reach a primary
answer first, and pay for everything that only matters if the answer is positive only once it is.

**The primary answer is an offline computation.** The β sweep replays
`fs2048-pool.chunks.hits.tsv` — no query run, no Qdrant restore, no exposure to the crash that refused
a run on 2026-09-09 (`docs/specs/2026-09-09-postgres-backend-sigpipe-diagnosis-design.md`). That is
exactly what Phase 1's byte-identity check bought, and this is the phase that spends it.

Phase 2 nonetheless **opens with one capture run**, for the reason in §1.1. So the staging saves the
three deferred corpora, not the λ = 0.70 arm.

### 1.1 Captured up front: the λ = 0.70 dump

Parent §6 makes the λ = 0.70 confirmation arm mandatory for a positive verdict, and parent §4's arm
design exists to make λ the only difference between it and the primary. **That comparison is
perishable, and the deferral cannot be priced in wall-clock alone.**

`iverson-api` is a stopped container carrying `VectorRanking__LambdaChunks=1.00` with
`RestartCount = 0`; it produced composite `3ffafcd26416ed30`. Its compose
`com.docker.compose.project.working_dir` is `.worktrees/chunk-coverage-phase1/Iverson.Server`, **which
no longer exists** — the source tree that produced the build is gone, and the build survives only as
the local image, `iverson-api:latest` and the container's image being the same digest
`b75f1c1b021af2afe6af1f2d7` today. `docker-compose.yml` gives `iverson-api` `build: context: ..`, so
any path that rebuilds produces a *different* composite from current `main`. And λ is fixed at
container-create time — which is why the stopped container still reads 1.00 — so reaching λ = 0.70
requires a recreate, and a recreate is one flag away from a rebuild.

The dump is therefore captured **now, while the build is live**, which converts a perishable
capability into a permanent artefact and makes both λ = 0.70 items offline replays like the primary:

1. Bring the stack up with single-service `docker start` / `docker compose up -d --no-deps` actions
   only. **Never a tier-wide `docker compose up`** — 12 of 19 containers were created from working
   directories that no longer exist.
2. Recreate `iverson-api` with `VECTOR_RANKING_LAMBDA_CHUNKS=0.70`, **without `--build`**.
3. **Verify `/build` still reports `3ffafcd26416ed30` before running anything.** This is the check that
   catches an accidental rebuild, and it is the whole point of the sequence: a different composite here
   means the confirmation arm is no longer a λ-only comparison, and the capture has failed.
4. Verify `LambdaChunks` reads `0.70`, then run the pool pass at `--chunk-budget-multiplier` 11 —
   **with a label and directory of its own**: `--config-label fs2048-pool-l070` into a new
   `chunk-coverage-phase2-l070-<date>/runs`. **Neither `--output-dir` nor `--config-label` may be
   Phase 1's** (`chunk-coverage-phase1-2026-09-09/runs`, `fs2048-pool`). `benchmark-query` writes all
   five artefacts as `Path.Combine(OutputDir, ConfigLabel…)` with `append: false` and **no overwrite
   guard** — its only `File.Exists` calls are on inputs. Reusing Phase 1's invocation with λ changed
   and nothing else, which is literally what parent §4's "λ is the only difference" invites, truncates
   `fs2048-pool.chunks.hits.tsv` — the sole input to all six primary replays, and the file
   `s` = 0.695455 was derived from. By this step the λ = 1.00 container is already gone, so recovery
   is another recreate plus another 42-minute SIGPIPE-exposed pass.

This run is exposed to the SIGPIPE crash, whose fail-closed guard refused a run on 2026-09-09. A
refusal means re-running, not reinterpreting.

## 2. The ladder

From Phase 1's measured `s` = 0.695455, under §2's construction — tie-break, three log-spaced
intermediates, parity, plus β = 0 as the baseline endpoint:

| Arm | β | label |
|---|---|---|
| baseline | 0 | `fs2048-b0` |
| tie-break | 0.003387 | `fs2048-b3387` |
| ×1.8031 | 0.006107 | `fs2048-b6107` |
| ×1.8031 | 0.011012 | `fs2048-b11012` |
| ×1.8031 | 0.019855 | `fs2048-b19855` |
| parity | 0.035800 | `fs2048-b35800` |

Five non-zero arms — the count §6 names for its Holm family. **No arm exceeds parity**: past it the
term degenerates into a count of tail chunks, and §6's opposite-conclusion rule would misread a
count-ranking loss as evidence about coverage.

Labels carry β in units of 1e-6 so they are dot-free (a dot would sit awkwardly beside the
`.chunks.trec` suffix `report.py` parses) and sort in ladder order.

## 3. Producing the arms

Six `benchmark-aggregate` replays of the Phase 1 dump, all into **one** output directory under the
labels above — `benchmark-aggregate` derives its run path as `<output-dir>/<config-label>.chunks.trec`
(`BenchmarkAggregateScenario.cs:219`), so distinct labels give distinct files with no collision, which
is what lets `report.py` discover them together.

Each arm also passes `--scores-path`, because §4's invariant needs untruncated full-precision
per-document scores and the run file is truncated to the top 50 and formatted to six decimals. Passing
it does not perturb the run file — pinned by
`BenchmarkAggregateScenarioTests.RunAsync_ScoresPath_LeavesTheRunFileByteIdentical`.

**Name each scores file after its arm — `<dir>/<label>.scores.tsv`.** Unlike the run path,
`--scores-path` is operator-supplied rather than label-derived, and the tool's collision guard compares
it only against *that invocation's* own files — so nothing stops two arms being handed the same path,
and the second would overwrite the first. Naming by label extends this section's collision-freedom
argument to them; §4 carries the assertion that catches it if this slips.

**One warning is expected per replay — six in total — and it is not a failure.** It is the
`queryCount` banner at `BenchmarkAggregateScenario.cs:195`, the **only** `WARNING` emitter in the
harness. **Any warning that is not that banner is unexpected and must be explained before the sweep is
read.** The Phase 1 sidecar predates the `queryCount`
completeness field, so each replay prints the "cannot verify the dump came from a run that finished"
warning. That run *was* verified complete by hand at the time — 672/672 queries, zero failed RPCs, no
unhandled exception in its log — and this spec records that so the warning has a documented answer
rather than being waved away at replay time.

## 4. Precondition: the single-chunk invariant

**A (query, document) pair contributing exactly one pooled chunk has no tail, so its score must be
identical at every β — exactly, not approximately.** A difference means the tail slice is off by one,
or the maximum is being included in its own tail.

The check compares the β = 0 and β = parity `--scores-path` outputs over every such pair, and **reports
the number of pairs it asserted. A count of zero is a failure of the precondition, not a pass** — the
same rule §6 applies to its ordering check, for the same reason: a check that silently asserts nothing
looks exactly like a check that passed.

**It must also assert that at least one *multi*-chunk pair DIFFERS between the two files, and a count
of zero there is equally a failure.** The count rule above guards against the check running on nothing;
it does not guard against the check running on everything and comparing it to itself — which reports
79,946 pairs asserted and zero differences, and looks exactly like a triumphant pass. This positive
control kills that case and an arm that silently failed to apply β — both collapse to zero differences
— from data the check already has open. **It does not catch a mis-typed β**, except a mistype to zero:
any other wrong non-zero β still moves every multi-chunk pair, and the single-chunk assertion is
β-blind by construction, since `Skip(1).Take(3).Sum()` is 0 and the score is `descending[0] + β·0` at
every β.

**So assert the ladder itself, from an artefact the sweep already writes.** `benchmark-aggregate`
records `beta` in each `<label>.meta.json` (`BenchmarkAggregateScenario.cs:249`) and nothing currently
reads it back — `report.py` reads only `composite`. Check that the six sidecars' `beta` values equal
§2's ladder exactly and that **none exceeds 0.035800**, the Phase 1 gate's hard bound made checkable,
at one read per arm. Without it a decimal slip putting the top arm at 0.35800 — ten times parity, in
the region §6 says degenerates into counting tail chunks — passes every other check this spec
defines.

**If this fails, the sweep is not interpreted at all.**

Spec §5 sites this invariant on SciFact, where 73.7 % single-chunk documents make violations most
visible. This phase runs it on the FreshStack-2048 dump instead, for two reasons: the dump holds
**79,946 single-chunk (query, parent) pairs out of 172,704 — 46.3 %**, which is ample power; and a
violation there impugns *the very data the primary conclusion rests on*, which a violation on a
different corpus would not. SciFact remains available and is deferred, not discarded.

## 5. Scoring and the gate

```
PYTHONPATH=<corpora>/python-libs python3 report.py \
  --qrels        <corpus>/qrels.trec \
  --nugget-qrels <corpus>/qrels.nugget.trec \
  --baseline     <dir>/fs2048-b0.chunks.trec \
  --run <dir>/fs2048-b0.chunks.trec  --run <dir>/fs2048-b3387.chunks.trec \
  --run <dir>/fs2048-b6107.chunks.trec --run <dir>/fs2048-b11012.chunks.trec \
  --run <dir>/fs2048-b19855.chunks.trec --run <dir>/fs2048-b35800.chunks.trec
```

All six go to `--run` so every arm's absolute scores are printed; β = 0 is additionally the
`--baseline`. `report.py` excludes the baseline file from the comparison set and says so
(`report.py:646-648`, printing `[baseline] excluded …`), so **the Holm family is the five non-zero
arms** — which is §6's stated invariant, that the printed family size equals the number of non-zero β
arms compared. Holm corrects within each measure, never pooled across them.

**`--pair` must not be used.** β changes which documents reach the top 50, so the document set differs
between arms and `check_pool` exits `ARM INVALID: pool changed`. `--baseline` is the correct mode.

**The verdict.** A β **qualifies** if its nDCG@10 delta against β = 0 is **positive** and Holm
p_adj < 0.05. **A significant negative delta is a qualifying result for the opposite conclusion** and is
recorded as such, not discarded. Coverage is a precision claim — a document matching in several places
is more likely genuinely relevant — so ordering is the endpoint.

α-nDCG@10 and R@50 are reported and **do not gate**. α-nDCG is watched for a *negative*: coverage may
concentrate on comprehensive documents at the cost of subtopic spread, and that cost belongs in the
record even though it cannot veto.

## 6. Outcome

A gate document at `docs/plans/2026-09-GATE-chunk-coverage-phase2.md`, following the Phase 1 gate's
convention, recording the ladder, the invariant's asserted-pair count, the full `report.py` output, and
the verdict. `docs/plans/` is git-ignored (`.gitignore:49`), so it is committed with `git add -f`, as
the Phase 1 gate was.

**A null is a result, not a failure.** If no β qualifies, max-passage stands and the null is
interpretable *because it was taken with MMR off* — λ = 1.00 means stream order equals score order, so
nothing about the null is confounded **by diversification**. The fusion-triple confound in §9 stands
regardless: λ = 1.00 removes diversification, not the weights that decided which chunks Qdrant returned.
A null here is a null about coverage **under that triple**.

## 7. Deferred, and what would un-defer it

**The λ = 0.70 ordering check and confirmation arm are no longer deferred** — §1.1 captures their dump
up front, so both become offline replays like the primary, and only the *reading* of the confirmation
waits on a β qualifying. What remains deferred is the three corpora:

| Deferred | Un-deferred by |
|---|---|
| FreshStack-512, NFCorpus (density arms) | a decision to spend ~2 h; they cannot triangulate a null at the production window anyway (see §8) |
| SciFact-2048 (§5's invariant site) | a decision to re-site the §4 invariant |

Each remaining deferred item needs its own pool query run — a Qdrant restore plus ~42 min on the
FreshStack arms — which is the cost this staging avoids paying before the primary answer exists.

## 8. Verified assumptions

Verified 2026-09-09. No Postgres settings were changed and no benchmark run was started; every check
below is read-only against artefacts already on disk or source already committed.

| # | Assumption | Evidence | Result |
|---|---|---|---|
| 1 | `meta.json`'s composite covers the **server**, not the harness | its `assemblies` are `Iverson.Api`, `Client.Contracts`, `Embeddings`, `Events`, `Sql`, `StarRocks`, `Vector` — no `Iverson.LoadTest` | ✅ harness changes cannot invalidate the dump |
| 2 | The server was not rebuilt between the dump and now | `iverson-api` container created 2026-09-09 09:20:16, dump recorded 14:10:35 UTC (10:10 EDT); same container, never recreated | ✅ |
| 3 | The dump is complete and harness-accepted | 672 distinct query ids; zero `failed for QueryId` and zero `Unhandled exception` in its log | ✅ |
| 4 | Every ladder β passes the guard | guard is `!double.IsFinite(beta) \|\| beta < 0` (`BenchmarkAggregateScenario.cs:70`); all six are finite and ≥ 0 | ✅ |
| 5 | Six labels in one directory give six distinct files | run path is `<output-dir>/<config-label>.chunks.trec` (`:219`) | ✅ |
| 6 | `--scores-path` does not perturb the run file | pinned by `RunAsync_ScoresPath_LeavesTheRunFileByteIdentical` | ✅ |
| 7 | A sidecar without `queryCount` **warns**, not refuses | the field is absent from the Phase 1 sidecar; the warn path is the designed behaviour for pre-field runs | ✅ expected |
| 8 | The sidecar's `reranker` is null, so the comparability guard passes | `reranker: None` in the sidecar | ✅ |
| 9 | Single-chunk pairs are identifiable from the dump | same (query, parent) derivation `tail_stats.py` already performs | ✅ |
| 10 | `--scores-path` output is untruncated | `MaxPassageAggregator.Aggregate(chunks, keyMap, int.MaxValue, beta)` (`:237`), vs `DocumentBudget` for the run file | ✅ |
| 11 | Single-chunk pairs exist in usable numbers | **79,946 of 172,704 (46.3 %)** pool-wide in the dump | ✅ far above the 5,048 top-50 slots Phase 1 reported |
| 12 | `report.py` runs with `PYTHONPATH` at `python-libs` | `ir_measures`, `numpy` 2.5.2, `scipy` 1.18.1 and `pyndeval` all import; `--help` prints | ✅ |
| 13 | The Holm family equals the `--run` count, baseline excluded | `report.py:646-648` skips the baseline from `compare_paths` and prints `[baseline] excluded`; `family_size = len(valid)` | ✅ — passing all six as `--run` yields a family of 5 |
| 14 | `report.py` accepts `benchmark-aggregate`'s run files | both `BenchmarkQueryScenario` and `BenchmarkAggregateScenario` write via the same `TrecRunWriter.WriteAsync` | ✅ |
| 15 | `--nugget-qrels` yields α-nDCG@10 | `pyndeval` imports; `report.py` documents α-nDCG@10 as a fourth measure with its own compare block | ✅ |
| 16 | The qrels cover the dump's queries | `qrels.trec` and `qrels.nugget.trec` each hold **672** distinct query ids, matching the dump | ✅ |
| 17 | Nothing else consumes the Phase 1 dump | only source comments name `chunk-coverage-phase1`; no script or doc reads its artefacts | ✅ |
| 18 | Every `benchmark-aggregate` flag exists as spelled | `--beta`, `--hits-path`, `--key-map-path`, `--config-label`, `--output-dir`, `--scores-path` all present in `Program.cs` | ✅ |
| 19 | Every `report.py` flag exists as spelled | `--qrels`, `--baseline`, `--run`, `--nugget-qrels` all present | ✅ |
| 20 | The gate document needs `git add -f` | `.gitignore:49` is `**/docs/plans/`; a new file there is reported ignored | ✅ — note `git check-ignore` on the *directory* misleadingly reports nothing |
| 21 | The aggregator binary Phase 2 runs is output-inert versus the one Phase 1's identity check certified | `git diff 7463d6c..HEAD`: `DocumentRanking.cs`, `MaxPassageAggregator.cs` and `TrecRunWriter.cs` untouched since `be24347`; the only run-file-path change is `double.Parse(…, CultureInfo)` → `(…, NumberStyles.Float, CultureInfo)`, dropping `AllowThousands`, inert for tokens like `0.8120335340499878`. **Assumption 1 does not establish this** — `composite` is copied verbatim from the pool sidecar, so it tracks the server, and harness changes are precisely what could invalidate a *replay*. A free confirmation is available: Phase 2's `fs2048-b0.chunks.trec` cannot be byte-compared to Phase 1's certified `identity-beta0/fs2048-pool.chunks.trec` because `TrecRunWriter.cs:31` writes the config label into column 6, but **a columns-1-to-5 comparison works and costs nothing** | ✅ |
| 22 | The dump's per-query stream order is fused-descending | **0 of 369,600 rows** violate per-query descending score order. Independently, `DocumentRanking.cs:74` takes the tail from `OrderByDescending(s => s)`, so the tail is the three largest by construction regardless of stream order | ✅ — this is what makes deferring parent §6's ordering check safe for the primary arm |
| 23 | The keymap and qrels are the ones the dump was taken with | 0 unresolved parent keys against today's `keymap.json`; both qrels files have mtime 2026-09-07, predating the 2026-09-09 run | ✅ |
| 24 | The fused score is time-independent, so a λ = 0.70 dump captured later is comparable to one captured 2026-09-09 | decay is supplied by a wall-clock read (`ObjectSearchGrpcService.cs:282,653`), but `DecayFieldResolver.Resolve` returns a field only for a `TIMESTAMPTZ`/`DATETIME` metadata column, and `BenchmarkDocument` (`Entities/BenchmarkDocument.cs:6-21`) declares `Id`, `DocId`, `Title`, `Body`, `OwnerId` and **no timestamp** — so `hasDecay` is false and the score reduces to `(0.45·base + 0.45·centroid)/0.90`, a function of the index and query vector only | ✅ — this is the load-bearing fact under §1.1's capture-now decision |
| 25 | The Qdrant collection is the one Phase 1 ran against | in `iversonserver_qdrant_data`, `benchmark_documents_tenant_bypass` and `benchmark_documents_chunks_tenant_bypass` were last written **2026-09-09 09:20**, immediately before the Phase 1 run, and nothing has touched them since. A swapped index would also surface loudly — unresolved parent keys throw at `BenchmarkAggregateScenario.cs:264` | ✅ |
| 26 | Compose from the main checkout owns the existing containers | `docker compose ps -a` from the main checkout enumerates all 19, so the project resolves to `iversonserver` as the container labels record. Were it otherwise, §1.1 step 2 would hit the explicit `container_name: iverson-api` and fail rather than recreate | ✅ |

## 9. Known issues, inherited and accepted

- **The deep-tail evidence is 512-window only.** FreshStack-2048 is the sole production-window arm; the
  density arms are 512/448 ingests. A null here cannot be triangulated against any deep-tail arm **at
  the window that ships** — the gap `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md`
  item 1 already names. Deferring those arms does not widen this gap; it was there either way.
- **The tail cap of 3 binds on the majority of ranked slots**, not rarely — Phase 1 measured 50.5 % of
  top-50 slots contributing ≥ 4 pooled chunks. The parent spec's §9 claim to the contrary is retracted
  in the Phase 1 gate and must not be carried forward into how a null here is read.
- **This phase cannot show whether coverage helps at other chunk budgets.** The dump was taken at
  `--chunk-budget-multiplier` 11 (a 550-chunk pool), which determines how many of a document's 2nd–4th
  chunks are in the pool at all — the quantity the tail term reads. A null is scoped to that budget
  **and to the shipped fusion triple**, per parent §7's sentence, whose two clauses are inherited here
  in full.
- **Chunk-level MMR is off on this arm, but the fusion weights are not.** At λ = 1.00 diversification is
  off; the chunk pool is still whatever Qdrant returned under the shipped 0.45/0.45/0.10 triple. This
  phase does not disentangle the fusion weights from the aggregation rule — it is the one scope limit
  that survives exactly the configuration this phase runs.
- **Nor whether a winning β transfers to the routed `SearchSimilar` path**, which collapses a larger
  pool scaling with the caller's `top_k` (`ObjectSearchGrpcService.cs:358-362`), so available tail depth
  varies per request and no fixed-budget harness arm measures it.

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

Staging is close to free here, because **Phase 1's dump makes the primary answer an offline
computation**. The β sweep replays `fs2048-pool.chunks.hits.tsv` — no query run, no Qdrant restore, no
exposure to the crash that refused a run on 2026-09-09
(`docs/specs/2026-09-09-postgres-backend-sigpipe-diagnosis-design.md`). That is exactly what Phase 1's
byte-identity check bought, and this is the phase that spends it.

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

**Two warnings are expected and are not failures.** The Phase 1 sidecar predates the `queryCount`
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
nothing about the null is confounded by diversification.

## 7. Deferred, and what would un-defer it

| Deferred | Un-deferred by |
|---|---|
| λ = 0.70 ordering check (§6) | a β qualifying — §6 requires it to pass before the confirmation is read |
| λ = 0.70 confirmation arm | a β qualifying |
| FreshStack-512, NFCorpus (density arms) | a decision to spend ~2 h; they cannot triangulate a null at the production window anyway (see §8) |
| SciFact-2048 (§5's invariant site) | a decision to re-site the §4 invariant |

Each deferred item needs a pool query run — a Qdrant restore plus ~42 min on the FreshStack arms —
which is the cost this staging avoids paying before the primary answer exists.

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
  chunks are in the pool at all — the quantity the tail term reads. A null is scoped to that budget.
- **Nor whether a winning β transfers to the routed `SearchSimilar` path**, which collapses a larger
  pool scaling with the caller's `top_k` (`ObjectSearchGrpcService.cs:358-362`), so available tail depth
  varies per request and no fixed-budget harness arm measures it.

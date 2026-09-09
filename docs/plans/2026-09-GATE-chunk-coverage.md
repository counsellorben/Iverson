# Chunk-Coverage Signal, Phase 1 — Gate Verdict

Measurements recorded 2026-09-09 from the `chunk-coverage-phase1` worktree at HEAD `7463d6c`
(`add tail_stats.py: in-pool tail depth and the tail-score level s`), which carries Tasks 1–4 of
`docs/plans/2026-09-09-chunk-coverage-phase1-implementation-plan.md`. The design is
`docs/specs/2026-09-08-chunk-coverage-signal-design.md`; Phase 1's purpose is set by its §2 and §3.
The prerequisite recorded at the end of this document refers to `--scores-path`, which was added
later on the same branch (`6fe3014`) in response to the whole-branch review — after the HEAD named
above, which is why that HEAD does not contain it. No measurement here depends on it.

Phase 1 measures `s` and derives the ladder's two endpoints from it. It does not select the
ladder and it does not score any β ≠ 0 arm — the spec was corrected twice for calibrating β against
a quantity no artefact on disk contained, and this phase exists to produce that quantity first.

**Verdict: Phase 2 is warranted.** 85.0 % of top-50 document slots contribute a tail. The plan's stated
kill condition — "a histogram showing most top-50 documents contribute a single pooled chunk … ends
the experiment" — is not met, and is not close to met.

## Arm

One arm. FreshStack-2048 is the only corpus on disk ingested at the production window.

| | |
|---|---|
| Corpus | FreshStack 6k slice, `freshstack-2048-2026-09-07` |
| Window | 2048/1792 |
| Points verified in Qdrant before the run | 6,000 object / 18,622 chunk |
| `--chunk-budget-multiplier` | **11** (the default 5 would trip `ChunkBudgetGuard` at 3.10 chunks/doc) |
| `DocumentBudget` × multiplier | 50 × 11 = **550-chunk pool per query** |
| `VectorRanking__LambdaChunks` | **1.00**, verified by `docker compose exec iverson-api env` — the harness cannot observe the server's λ and `.meta.json` does not record it |
| Fusion triple | shipped `WBase`/`WCentroid`/`WDecay` = 0.45/0.45/0.10, untouched |
| Build composite | **`3ffafcd26416ed30`** |
| Run label | `fs2048-pool` |

Artefacts live untracked in `~/repositories/iverson-benchmark-corpora/chunk-coverage-phase1-2026-09-09/`:
`runs/` (the accepted run), `identity-beta0/` (the β = 0 replay), `tail-stats.txt`, and
`refused-2026-09-09T1007/` (see below).

## The first run was refused; this is the second

The first pool run (09:25:30 → 10:07 EDT) completed and was **rejected by the harness**:

```
InvalidOperationException: [benchmark-query] 2 search RPC(s) failed
  — the run files above are incomplete and must not be scored.
```

`SearchSimilar` on QueryId=75983653 and — load-bearing — `SearchChunks` on QueryId=76083286, which
removed a query from `fs2048-pool.chunks.trec` **and** `fs2048-pool.chunks.hits.tsv`, leaving
671/672 in every arm. A tail histogram built from that dump would have been computed over a short
corpus with nothing in the files to say so.

Cause, read from the container log rather than inferred: the Postgres **backend** crashed twice
during the run and the postmaster reinitialized both times —
`server process (PID 139873) was terminated by signal 13: Broken pipe` at 13:28:59 UTC and PID
139928 likewise at 13:30:10 UTC, with a third identical crash on 2026-09-08 at 13:28:18 UTC. The
container never restarted (`RestartCount=0`). Both RPC stacks end in
`TenantStatusCache.GetStatusAsync` under `ActingUserInterceptor`, i.e. the call died in the
acting-user lookup before the search ran, which is why it is not query-specific. `apt-daily.timer`
was checked and ruled out — it fired at 13:30:27 UTC, after the second crash.

The refused files are kept in `refused-2026-09-09T1007/` with a README stating why they must not be
scored. They were not deleted: they are the evidence for the crash.

**No retry or failure tolerance was added to the harness.** The fail-closed guard is the only thing
that stood between this crash and a believed-but-wrong number. A Postgres backend dying of SIGPIPE
is abnormal — Postgres sets SIGPIPE to `SIG_IGN` in backends — and remains **undiagnosed and open**.
It is not a chunk-coverage concern and was deliberately not folded into this branch.

The accepted run (10:10:35 → 10:49 EDT) logged **zero** RPC failures, exited 0, and covers
**672/672** queries in all three arms. Its hit dump holds 369,600 rows — exactly 550 per query.

## Identity check (Step 3) — PASS

`benchmark-aggregate --beta 0` replayed the hit dump through the shared aggregator into a separate
directory, using the same key map as the run:

```
runs/fs2048-pool.chunks.trec           sha256 a676f5429c55f18cb83a8947e4db1511d7aa30696f3d37b41b1946cff31efe38
identity-beta0/fs2048-pool.chunks.trec sha256 a676f5429c55f18cb83a8947e4db1511d7aa30696f3d37b41b1946cff31efe38
```

Byte identical, 3,246,319 bytes each. `CollapseByDocIdWithTail` at β = 0 reproduces
`CollapseByDocId` exactly, and the dump captures everything the in-run aggregation consumed.

This is the gate on Phase 2, but it is worth being exact about what it does and does not
establish. `DocumentRanking.cs:61` short-circuits — `if (beta == 0) return CollapseByDocId(...)` —
so this check *cannot* fail by the tail arithmetic being wrong; that path never executes at β = 0.
What it actually tests is **dump, key-map, reader and writer fidelity**: that the offline path
reconstructs the in-run ranking exactly from the persisted hits. That could genuinely have failed —
a truncated dump, a mis-parsed score, a different parent resolution — and it did not.

So "Phase 2 can sweep β offline from this dump" rests on this PASS *for the plumbing* plus Task 1's
unit tests and the spec's §5/§6 checks *for the β ≠ 0 arithmetic*. This check exercises none of the
latter.

## Measurement (Step 4)

In-pool tail depth over all 672 × 50 = 33,600 top-50 document slots:

| Pooled chunks contributed | Slots | Share |
|---|---|---|
| 1 (no tail) | 5,048 | 15.0 % |
| 2 | 4,919 | 14.6 % |
| 3 | 6,675 | 19.9 % |
| 4 or more | 16,958 | 50.5 % |
| **≥ 2 (has a tail)** | **28,552** | **85.0 %** |

The 1-chunk row is not printed by `tail_stats.py`; it was computed independently against the same
three inputs, and that independent pass reproduced the script's 2/3/4+ counts exactly.

| Quantity | Value | n |
|---|---|---|
| `s`, tail level, top-50 scoped | **0.695455** | 69,143 tail-chunk values |
| `s`, tail level, pool-wide | 0.677898 | 182,685 tail-chunk values |
| Rank-1 → rank-50 span (mean) | 0.074692 | 672 queries |
| Median top-10 adjacent gap | 0.002355 | 6,048 adjacent gaps across 672 queries |
| **Tie-break β** = median gap / `s` | **0.003387** | |
| **Parity β** = span / (3 · `s`) | **0.035800** | |

Both `s` values go on the record per spec §2; the pool-wide figure is strictly lower, as expected,
because it includes parents that never reach any top 50.

### Cross-checks

- **Span and gap reproduce the independently recorded figures.** `project-ranking-score-scales`
  recorded 0.0747 mean span and 0.00236 median top-10 gap from `fs-2048-l100.chunks.trec`, a
  different run. This run gives 0.074692 and 0.002355. The measurement is not an artefact of this
  run.
- **λ = 1.00 is corroborated in-artifact, not only by the env check.** Step 2a warns that the
  harness cannot observe the server's λ and `.meta.json` does not record it, so a silent restart
  failure would be invisible. `runs/fs2048-pool.chunks.diversity.json` reports
  `meanDistinctParentsAt10` = 6.665 and `meanDistinctParentsAt50` = 28.815, against spec §4's
  recorded λ = 1.00 row of 6.67 / 28.82 and its λ = 0.70 row of 8.10 / 34.50. The run is
  unambiguously the λ = 1.00 arm.
- **CDR round 2's correction is confirmed quantitatively.** That round established the tail level
  was "strictly below 0.72 by an unmeasured amount" and that the spec's provisional parity of 0.0346
  rested on a file containing no tail chunks. Measured: `s` = 0.6955, parity = 0.0358. The direction
  was right and the provisional figure was 3.4 % low: with span 0.074692 and the placeholder
  `s` = 0.72, provisional parity is 0.034580 against a measured 0.035800.
- **50.5 % at ≥ 4 pooled chunks is not the corpus-wide 46.0 %**, and the two must not be
  conflated. The corpus figure is the share of *documents in the corpus* with ≥ 4 chunks; this is
  the share of *retrieved top-50 slots* whose document put ≥ 4 chunks *in the 550-chunk pool*. The
  4.5-point gap between them is **not** a small effect — it is the net of two large ones, measured
  rather than assumed (chunk counts from a faithful replay of `ingest.py:240` `split_into_chunks`
  at 2048/1792, which reproduces the recorded 18,622 chunks and 26.6 % single-chunk share exactly):

  | | Slots | Share of 33,600 |
  |---|---|---|
  | Document has ≥ 4 **corpus** chunks | 21,592 | 64.3 % |
  | Document has ≥ 4 **pooled** chunks | 16,958 | 50.5 % |
  | ≥ 4 corpus but < 4 pooled — truncated by the budget | 4,634 | 13.8 % |

  Retrieval is biased toward multi-chunk documents by **+18.2 points** (64.3 % of retrieved slots
  vs 46.0 % of corpus documents), and the 550-chunk budget gives **−13.8 points** back by not
  pooling the whole tail. Net **+4.5**. Neither number is evidence about the other.

  This matters for Phase 2 beyond the bookkeeping: **the budget is measurably truncating available
  tail on 13.8 % of top-50 slots**, which is direct evidence for the spec §7 caveat that the chunk
  budget is not a nuisance parameter — it determines how much of a document's tail exists to be
  read at all.

## What this does and does not license

**Licensed.** Phase 2 may proceed, and its β ladder is to be derived from `s` = 0.695455 — the
measurement Phase 1 owed it. The two endpoints it needs are **tie-break 0.003387** and
**parity 0.035800**. Spec §2 requires the ladder to span tie-break to parity *inclusive* and to not
extend past parity, because beyond parity the term degenerates into a count of tail chunks and §6's
opposite-conclusion rule would misread a count-ranking loss as evidence about coverage. **So no
Phase 2 arm may exceed 0.035800.** Phase 2 sets its own arms within that bound; this document does
not set them.

An earlier draft of this gate said the ladder {0, 0.003, 0.008, 0.02, 0.05} "survives the
measurement". That was wrong twice over and is retracted here: that ladder is not in the spec — CDR
round 2 (`23d1c59`) deleted it in favour of the derived construction — and its top arm of 0.05 sits
40 % above the measured parity, which §2 forbids. The measurement **falsifies** that ladder rather
than confirming it.

**Not licensed.** Nothing here says the coverage signal *works*. Tail depth being available is a
necessary condition for the signal to do anything at all, not evidence that it improves ranking.
85.0 % of top-50 documents having a tail means a β ≠ 0 term will move the ranking; whether it moves
it in the right direction is exactly what Phase 2 measures and what this phase cannot anticipate.

The scope limits in the plan's "Tasks NOT in this plan" and "Known issues inherited from spec" carry
forward **with one exception, below**. In particular the deep-tail evidence remains 512-window only,
so a Phase 2 null on FreshStack-2048 still cannot be triangulated against any deep-tail arm at the
window that ships.

### One inherited caveat is refuted by this measurement

Spec §9 and the plan both carry: *"The tail cap of 3 rarely binds on FreshStack-2048. At a mean of
3.10 chunks per document the typical tail is 1–2 chunks; the cap binds for the 46.0 % with four or
more."* **Do not carry that forward.** It is internally inconsistent on its face ("rarely binds …
binds for the 46.0 %"), and it describes the wrong population: 46.0 % is a share of *corpus
documents*, whereas ranking operates over *retrieved slots*. Measured here, **50.5 % of top-50 slots
contribute ≥ 4 pooled chunks**, so the cap binds on the majority of the slots the ranking actually
decides between.

This matters for how a Phase 2 null is read. "The cap barely binds" would license "we never really
tested a 3-chunk tail"; the measurement says the term is a full 3-chunk sum for most ranked slots,
so a null is a null about the signal, not about an inactive cap.

### Prerequisite for Phase 2: spec §6's ordering check needs the auxiliary scores file

Spec §6 — the only falsifier of the tail's *ordering* rule, since §5's SciFact invariant explicitly
cannot check it — requires `score_β − score_0` for every document in the β-arm's top 50 contributing
≥ 4 chunks. The `.chunks.trec` run file cannot supply that: it is truncated at 50 documents and
formatted to 6 decimal places, so at a parity-scale β a substantial share of exactly the documents
the tail term *promoted* have no `score_0` row at all, and the check would silently degrade to the
intersection while looking like it ran.

`benchmark-aggregate --scores-path <file>` (added on this branch) writes an untruncated,
full-precision per-document score file for this purpose; it is opt-in and leaves `.chunks.trec`
byte-identical. **Phase 2 must run §6 from that file, not from the run file.**

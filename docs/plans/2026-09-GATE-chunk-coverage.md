# Chunk-Coverage Signal, Phase 1 — Gate Verdict

Recorded 2026-09-09 from the `chunk-coverage-phase1` worktree at HEAD `7463d6c`
(`add tail_stats.py: in-pool tail depth and the tail-score level s`), which carries Tasks 1–4 of
`docs/plans/2026-09-09-chunk-coverage-phase1-implementation-plan.md`. The design is
`docs/specs/2026-09-08-chunk-coverage-signal-design.md`; Phase 1's purpose is set by its §2 and §3.

Phase 1 measures. It does not calibrate β and it does not score any β ≠ 0 arm — the spec was
corrected twice for calibrating β against a quantity no artefact on disk contained, and this phase
exists to produce that quantity first.

**Verdict: Phase 2 is warranted.** 85.0 % of top-50 documents contribute a tail. The plan's stated
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

This is the gate on Phase 2. Had it failed, no β result from the offline aggregator could have been
trusted, because the aggregator would not reduce to shipped behaviour at β = 0. It passed, so
Phase 2 can sweep β offline from this dump without re-running queries.

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
| Median top-10 adjacent gap | 0.002355 | 672 queries |
| **Tie-break β** = median gap / `s` | **0.003387** | |
| **Parity β** = span / (3 · `s`) | **0.035800** | |

Both `s` values go on the record per spec §2; the pool-wide figure is strictly lower, as expected,
because it includes parents that never reach any top 50.

### Cross-checks

- **Span and gap reproduce the independently recorded figures.** `project-ranking-score-scales`
  recorded 0.0747 mean span and 0.00236 median top-10 gap from `fs-2048-l100.chunks.trec`, a
  different run. This run gives 0.074692 and 0.002355. The measurement is not an artefact of this
  run.
- **CDR round 2's correction is confirmed quantitatively.** That round established the tail level
  was "strictly below 0.72 by an unmeasured amount" and that the spec's provisional parity of 0.0346
  rested on a file containing no tail chunks. Measured: `s` = 0.6955, parity = 0.0358. The direction
  was right and the provisional figure was 3.9 % low.
- **50.5 % at ≥ 4 pooled chunks is not the corpus-wide 46.0 %**, and the two must not be conflated.
  The corpus figure is the share of *documents in the corpus* with ≥ 4 chunks; this is the share of
  *retrieved top-50 slots* whose document put ≥ 4 chunks *in the 550-chunk pool*. Retrieved
  documents are mildly biased toward multi-chunk documents — more chunks, more chances to be pooled
  — and the 4.5-point gap is consistent with that. Neither number is evidence about the other.

## What this does and does not license

**Licensed.** Phase 2 may proceed, and its β ladder is to be derived from `s` = 0.695455 — the
measurement Phase 1 owed it. The two endpoints it needs are tie-break 0.003387 and parity 0.035800.
The spec's provisional ladder {0, 0.003, 0.008, 0.02, 0.05} brackets both, so it survives the
measurement; Phase 2 sets its own arms and this document does not set them.

**Not licensed.** Nothing here says the coverage signal *works*. Tail depth being available is a
necessary condition for the signal to do anything at all, not evidence that it improves ranking.
85.0 % of top-50 documents having a tail means a β ≠ 0 term will move the ranking; whether it moves
it in the right direction is exactly what Phase 2 measures and what this phase cannot anticipate.

The scope limits in the plan's "Tasks NOT in this plan" and "Known issues inherited from spec" carry
forward unchanged. In particular the deep-tail evidence remains 512-window only, so a Phase 2 null
on FreshStack-2048 still cannot be triangulated against any deep-tail arm at the window that ships.

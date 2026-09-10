# Chunk-Coverage Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-09-chunk-coverage-phase2-design.md` (commit SHA: `c31683a`)

**Goal:** Answer spec §5's primary gate — does any β on the ladder beat β = 0 on nDCG@10 for FreshStack-2048 at λ = 1.00? — and capture, before it perishes, the λ = 0.70 dump a positive verdict would need.

**Architecture:** The primary sweep is entirely offline: six `benchmark-aggregate` replays of Phase 1's chunk-hit dump, gated by a three-part precondition check, then scored by `report.py --baseline`. One operational task runs first to capture a λ = 0.70 dump while the server build that produced Phase 1's is still intact.

**Tech stack:** `benchmark-aggregate` (.NET 10 LoadTest harness), Python 3 with `ir_measures`/`numpy`/`scipy`/`pyndeval` from `iverson-benchmark-corpora/python-libs`, Qdrant, Docker Compose.

---

## Global Constraints

From the spec; every task holds to these.

- **Never a tier-wide `docker compose up`.** 12 of 19 `iverson-*` containers were created from working directories that no longer exist. Only single-service `docker start` / `docker compose up -d --no-deps` actions. (spec §1.1)
- **Never `--build`.** `iverson-api`'s build context is current `main`, while the composite under test came from a deleted worktree. Any rebuild yields a different composite and breaks the λ-only comparison. (spec §1.1)
- **No β arm may exceed 0.035800.** Past parity the term degenerates into counting tail chunks. (spec §2, Phase 1 gate)
- **`--pair` must never be used with `report.py`.** β changes which documents reach the top 50, so `check_pool` exits `ARM INVALID: pool changed`. Use `--baseline`. (spec §5)
- **The primary sweep reads Phase 1's dump and must not overwrite it.** `benchmark-query` has no overwrite guard. (spec §1.1)

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/scripts/beta_invariant.py` — spec §4's three precondition checks.
- `Iverson.Server/Iverson.LoadTest/scripts/test_beta_invariant.py` — falsifying tests for each check's failure path.
- `docs/plans/2026-09-GATE-chunk-coverage-phase2.md` — the gate document (git-ignored; `git add -f`).

**Modify** — none.

## Inherited from spec

Verified at spec-write time and **not** re-verified here (spec §8, assumptions 1–26). The load-bearing ones:

- Phase 1's dump is complete (672/672, zero failed RPCs) and its `composite` tracks the **server**, not the harness; the harness diff since is output-inert (#1, #3, #21).
- `--scores-path` output is untruncated (`int.MaxValue`) and does not perturb the run file (#6, #10).
- 79,946 of 172,704 pooled (query, parent) pairs contribute exactly one chunk — 46.3 % (#11).
- `report.py` excludes the `--baseline` file from `compare_paths`, so six `--run` arms yield a Holm family of five (#13).
- Both qrels cover the dump's 672 queries (#16); the keymap and qrels are the ones the dump was taken with (#23).
- The fused score is **time-independent** — `BenchmarkDocument` has no timestamp column, so `hasDecay` is false and the score reduces to `(0.45·base + 0.45·centroid)/0.90`. A λ = 0.70 dump captured later is comparable to Phase 1's (#24).
- The Qdrant collections are the ones Phase 1 ran against (#25); compose from the main checkout owns the containers (#26).

## Verified plan-level assumptions

Newly introduced by this plan and verified 2026-09-09, read-only.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | The three files this plan creates do not exist | `ls` returned none for `beta_invariant.py`, `test_beta_invariant.py`, `2026-09-GATE-chunk-coverage-phase2.md` |
| 2 | File path | No `chunk-coverage-phase2*` directory exists to collide with Task 1's output | `ls -d ~/repositories/iverson-benchmark-corpora/chunk-coverage-phase2*` → none |
| 3 | **Code validity** | **The scores file is TAB-separated, not comma** | `BenchmarkAggregateScenario.cs:355` writes header `queryId\tdocId\tscore` and rows `$"{queryId}\t{docId}\t…"` |
| 4 | Code validity | Scores are shortest-round-trippable, so the invariant can compare **strings exactly** rather than floats | `score.ToString(CultureInfo.InvariantCulture)` at `:362` |
| 5 | Signature | `benchmark-aggregate`'s sidecar carries `beta` as a top-level numeric field | `["beta"] = flags.Beta` at `BenchmarkAggregateScenario.cs:249` |
| 6 | Signature | `/build` exists and serves the composite | `Iverson.Api/Program.cs:304` — `app.MapGet("/build", …)` |
| 7 | Command | Every `benchmark-query` flag Task 1 passes exists | `--corpus-path`, `--key-map-path`, `--output-dir`, `--config-label`, `--chunk-budget-multiplier` all in `Program.cs` |
| 8 | Command | pytest is invoked by explicit path | no `pytest.ini` and no `conftest.py` in the scripts directory |
| 9 | Command | Commit convention is lowercase imperative, no Conventional-Commits prefix | `git log --oneline -20` |
| 10 | Command | `docker compose up -d --no-deps iverson-api` reuses the existing image | `docker-compose.yml:427-430` declares `build:` **and** `image: iverson-api`; compose rebuilds only on `--build`. `docker-compose.yml:444` documents this exact recreate form for λ overrides |
| 11 | **Consumer impact** | **λ = 0.70 is the SHIPPED default, so Task 1 restores it rather than deviating** | `VectorRankingOptions.cs:25` — `LambdaChunks { get; set; } = 0.70`; compose `${VECTOR_RANKING_LAMBDA_CHUNKS:-0.70}`. Phase 1's λ = 1.00 was the deliberate deviation |
| 12 | Ordering | Tasks 2–4 need no live server, so none depends on Task 1 | `BenchmarkAggregateScenario` opens **zero** gRPC clients — fully offline |
| 13 | Ordering | Task 3 reads Phase 1's dump, not Task 1's | the six arms all take `--hits-path <phase1>/runs/fs2048-pool.chunks.hits.tsv` |
| 14 | Dependents | A new file in the scripts directory disturbs nothing | 15 independent `*.py` files there; no package `__init__.py`, no shared import surface |

## Tasks

### Task 1: Capture the λ = 0.70 dump

**Files:** none — operational. **No repo change, therefore no commit step.**

**It runs first because its input is perishable, not because anything downstream needs it.** Tasks 2–4
read Phase 1's dump. If `iverson-api`'s image is lost before this runs, spec §6's confirmation arm
becomes unreachable as a λ-only comparison.

- [ ] **Step 1: Bring up only what a query run needs**, with single-service actions.
```bash
for c in iverson-postgres iverson-qdrant iverson-tei-embed iverson-authentik-server iverson-api; do
  docker start "$c"; done
until docker exec iverson-postgres pg_isready -U iverson >/dev/null 2>&1; do sleep 1; done
```
Never a tier-wide `docker compose up` (Global Constraints).

- [ ] **Step 2: Recreate `iverson-api` at λ = 0.70, without `--build`.**
```bash
cd Iverson.Server
VECTOR_RANKING_LAMBDA_CHUNKS=0.70 docker compose up -d --no-deps iverson-api
```
This is the form `docker-compose.yml:444` documents. λ is fixed at container-create time, which is why
a `docker start` cannot change it. **Setting the variable explicitly matters even though 0.70 is the
default** — the invoking shell may already carry a value from an earlier experiment.

- [ ] **Step 3: Verify the composite BEFORE running anything.** This is the step that makes the whole
capture meaningful: it catches an accidental rebuild while the run has not yet happened.
```bash
curl -s http://localhost:8081/build | python3 -m json.tool | head -5
```
**It must report `3ffafcd26416ed30`.** A different composite means the image was rebuilt, the λ-only
comparison is broken, and the capture has failed — stop and report rather than running.

- [ ] **Step 4: Verify λ took.**
```bash
docker exec iverson-api env | grep VectorRanking__LambdaChunks   # must print 0.70
```

- [ ] **Step 5: Run the pool pass into its own label and directory.**
```bash
source /home/ben/iverson-benchmark-data/bench-env.sh
C=~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07
D=~/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-l070-2026-09-09
mkdir -p "$D/runs"
dotnet run -c Release --project Iverson.Server/Iverson.LoadTest -- benchmark-query \
  --corpus-path "$C" --key-map-path "$C/keymap.json" \
  --output-dir "$D/runs" --config-label fs2048-pool-l070 \
  --chunk-budget-multiplier 11 > "$D/runs/fs2048-pool-l070.log" 2>&1; RC=$?
tail -5 "$D/runs/fs2048-pool-l070.log"; echo "DOTNET_EXIT=$RC"; exit $RC
```
**Neither `--output-dir` nor `--config-label` may be Phase 1's.** `benchmark-query` writes every
artefact as `Path.Combine(OutputDir, ConfigLabel…)` with `append: false` and no overwrite guard;
reusing Phase 1's would truncate the dump all six primary replays read.

Propagate the real exit code — a wrapper ending in `tail` reports the pipeline's status, not
`dotnet`'s, and a run the harness refused would look green.

- [ ] **Step 6: Confirm the run was accepted and record the certification.** The harness refuses a run
with any failed RPC. Record, for the gate document: the `/build` composite from Step 3, the query
count, and whether the SIGPIPE crash intervened.
```bash
grep -c "failed for QueryId\|Unhandled exception" "$D/runs/fs2048-pool-l070.log"   # must be 0
cut -f1 "$D/runs/fs2048-pool-l070.chunks.hits.tsv" | tail -n +2 | sort -u | wc -l  # must be 672
```
If the run was refused by a Postgres SIGPIPE crash, re-run it — that is a re-run, not a reinterpretation.

### Task 2: `beta_invariant.py` and its falsifying tests

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/beta_invariant.py`
- Create: `Iverson.Server/Iverson.LoadTest/scripts/test_beta_invariant.py`

**Interfaces:**
- Consumes: nothing from Task 1. Reads Phase 1's dump format and `benchmark-aggregate`'s outputs.
- Produces: the gate Task 3 must pass before its sweep is interpreted.

- [ ] **Step 1: Write `beta_invariant.py`** implementing spec §4's three checks. Match
`tail_stats.py`'s conventions in the same directory: stdlib only, `argparse`, a module docstring
saying what the script buys and why, and `sys.exit(...)` with a diagnostic message on failure.

Arguments: `--hits`, `--keymap`, `--scores-zero`, `--scores-parity`, `--max-beta` (default
`0.035800`), plus `--sidecar` and `--ladder`, both of which take **one or more values after a single
flag** — `nargs='+'`, **not** `action='append'`. Task 3 invokes them as
`--sidecar "$A"/fs2048-b*.meta.json` (a shell glob expanding to six paths) and
`--ladder 0 0.003387 …`; built with `action='append'` those invocations fail.

The three checks, each of which **must fail loudly on a zero count**:

1. **Single-chunk pairs are identical.** Derive the pooled chunk count per `(queryId, docId)` from the
   dump plus keymap — the same derivation `tail_stats.py` performs. For every pair with exactly one
   pooled chunk, the β = 0 and β = parity score strings must be **equal as strings** (they are
   shortest-round-trippable, so this is exact and needs no float tolerance). Report the number of pairs
   asserted; **zero is a failure, not a pass.**
2. **At least one multi-chunk pair differs.** Over pairs with ≥ 2 pooled chunks, count how many have
   differing score strings. **Zero is a failure.** This is the positive control: it catches a
   self-comparison of one file against itself, and an arm that silently failed to apply β.
3. **The sidecars carry the ladder.** Read `beta` from each `--sidecar`. The multiset of values must
   equal `--ladder` exactly, and **none may exceed `--max-beta`**. This is the only check that catches
   a mis-typed non-zero β: checks 1 and 2 are blind to it, because a single-chunk score is
   `descending[0] + β·0` at every β and any wrong non-zero β still moves every multi-chunk pair.

Parse the scores files as **TAB**-separated with a `queryId\tdocId\tscore` header.

- [ ] **Step 2: Write `test_beta_invariant.py`**, using the inline-fixture style of
`test_tail_stats.py` (no shared harness, no fixture module). **The tests that matter are the ones that
make each check go red** — a check whose failure path is untested is exactly what §4 exists to prevent:

- a fixture with zero single-chunk pairs → exits non-zero, and the message names the zero count
- the same file passed as both `--scores-zero` and `--scores-parity` → exits non-zero on check 2
- a single-chunk pair whose scores differ → exits non-zero on check 1
- a sidecar carrying `0.35800` → exits non-zero on check 3, naming the bound
- a sidecar set missing one arm → exits non-zero on check 3
- a well-formed input → exits zero and prints both counts

- [ ] **Step 3: Run the tests.**
```bash
python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_beta_invariant.py -q
```

- [ ] **Step 4: Mutation-check the checks.** For each of the three, disable it in a scratch copy and
confirm the suite goes red. A test that passes with its check removed is not testing the check.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/beta_invariant.py \
        Iverson.Server/Iverson.LoadTest/scripts/test_beta_invariant.py
git commit -m "add beta_invariant.py: the phase 2 sweep's precondition checks"
```

### Task 3: Produce the six arms, gate them, and score

**Files:** none — operational, reading Phase 1's dump. **No repo change, therefore no commit step.**

**Interfaces:**
- Consumes: `beta_invariant.py` from Task 2. Phase 1's dump. Nothing from Task 1.

- [ ] **Step 1: Replay the dump at all six β.**
```bash
P=~/repositories/iverson-benchmark-corpora/chunk-coverage-phase1-2026-09-09
C=~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07
A=~/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09
mkdir -p "$A"
set -- "fs2048-b0:0" "fs2048-b3387:0.003387" "fs2048-b6107:0.006107" \
       "fs2048-b11012:0.011012" "fs2048-b19855:0.019855" "fs2048-b35800:0.035800"
for arm in "$@"; do
  label="${arm%%:*}"; beta="${arm##*:}"
  dotnet run -c Release --project Iverson.Server/Iverson.LoadTest -- benchmark-aggregate \
    --beta "$beta" --hits-path "$P/runs/fs2048-pool.chunks.hits.tsv" \
    --key-map-path "$C/keymap.json" --config-label "$label" \
    --output-dir "$A" --scores-path "$A/$label.scores.tsv"
done
```
Each arm gets its **own** `--scores-path` named after its label; `--scores-path` is operator-supplied
rather than label-derived, and the tool's collision guard spans only one invocation, so a shared path
would leave the invariant comparing a file against itself.

**One warning per replay — six in total — is expected**: the Phase 1 sidecar predates the `queryCount`
field. It is the only `WARNING` emitter in the harness. **Any other warning is unexpected and must be
explained before the sweep is read.**

- [ ] **Step 2: Run the precondition. The sweep is not interpreted unless this passes.**
```bash
python3 Iverson.Server/Iverson.LoadTest/scripts/beta_invariant.py \
  --hits "$P/runs/fs2048-pool.chunks.hits.tsv" --keymap "$C/keymap.json" \
  --scores-zero "$A/fs2048-b0.scores.tsv" --scores-parity "$A/fs2048-b35800.scores.tsv" \
  --sidecar "$A"/fs2048-b*.meta.json \
  --ladder 0 0.003387 0.006107 0.011012 0.019855 0.035800
```

- [ ] **Step 3: Score.**
```bash
export PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs
python3 Iverson.Server/Iverson.LoadTest/scripts/report.py \
  --qrels "$C/qrels.trec" --nugget-qrels "$C/qrels.nugget.trec" \
  --baseline "$A/fs2048-b0.chunks.trec" \
  --run "$A/fs2048-b0.chunks.trec"     --run "$A/fs2048-b3387.chunks.trec" \
  --run "$A/fs2048-b6107.chunks.trec"  --run "$A/fs2048-b11012.chunks.trec" \
  --run "$A/fs2048-b19855.chunks.trec" --run "$A/fs2048-b35800.chunks.trec" \
  2>&1 | tee "$A/report.txt"
```
`--baseline`, never `--pair` (Global Constraints).

- [ ] **Step 4: Check the family size.** `report.py` should print `[baseline] excluded …` for the β = 0
run and correct Holm across **five** arms per measure. A family of six means the baseline was not
excluded and the correction is wrong.
```bash
grep -n "baseline\] excluded\|family" "$A/report.txt" | head
```

### Task 4: The gate document

**Files:**
- Create: `docs/plans/2026-09-GATE-chunk-coverage-phase2.md`

- [ ] **Step 1: Write the gate document**, following `docs/plans/2026-09-GATE-chunk-coverage.md`'s
convention. Spec §6 requires it to record **every check's result**, not only the verdict:

- the ladder, and the arm labels it maps to
- Task 1's `/build` certification that the λ = 0.70 dump was taken on composite `3ffafcd26416ed30`
- `beta_invariant.py`'s three outputs: the asserted-pair count, the multi-chunk-differs count, and the
  sidecar-β result
- the full `report.py` output, including the printed Holm family size
- the verdict

**A β qualifies only if its nDCG@10 delta against β = 0 is positive AND Holm p_adj < 0.05.** A
significant *negative* delta is a qualifying result for the opposite conclusion and is recorded as
such, not discarded. α-nDCG@10 and R@50 are reported and do not gate.

**A null is a result.** If no β qualifies, max-passage stands, and the null is interpretable because
it was taken with MMR off — but it is a null about coverage **under the shipped 0.45/0.45/0.10 fusion
triple** and **at the 550-chunk budget**, and the document must say so.

- [ ] **Step 2: Commit**
```bash
git add -f docs/plans/2026-09-GATE-chunk-coverage-phase2.md
git commit -m "record chunk-coverage Phase 2: the primary beta sweep"
```
`docs/plans/` is git-ignored (`.gitignore:49`), hence `-f`.

## Tasks NOT in this plan

Inherited from spec §7, in its form. What remains deferred is the three corpora:

| Deferred | Un-deferred by |
|---|---|
| FreshStack-512, NFCorpus (density arms) | a decision to spend ~2 h; they cannot triangulate a null at the production window anyway |
| SciFact-2048 (parent §5's invariant site) | a decision to re-site the §4 invariant |

The λ = 0.70 **ordering check and confirmation arm** are also not in this plan — Task 1 captures their
dump, but spec §7 defers *reading* them until a β qualifies.

## Known issues inherited from spec

Inherited verbatim from spec §9.

- **The deep-tail evidence is 512-window only.** FreshStack-2048 is the sole production-window arm; the
  density arms are 512/448 ingests. A null here cannot be triangulated against any deep-tail arm **at
  the window that ships**.
- **The tail cap of 3 binds on the majority of ranked slots**, not rarely — Phase 1 measured 50.5 % of
  top-50 slots contributing ≥ 4 pooled chunks. The parent spec's §9 claim to the contrary is retracted
  in the Phase 1 gate and must not be carried forward into how a null here is read.
- **This phase cannot show whether coverage helps at other chunk budgets.** The dump was taken at
  `--chunk-budget-multiplier` 11 (a 550-chunk pool), which determines how many of a document's 2nd–4th
  chunks are in the pool at all. A null is scoped to that budget **and to the shipped fusion triple**.
- **Chunk-level MMR is off on this arm, but the fusion weights are not.** At λ = 1.00 diversification is
  off; the chunk pool is still whatever Qdrant returned under 0.45/0.45/0.10. This phase does not
  disentangle the fusion weights from the aggregation rule.
- **Nor whether a winning β transfers to the routed `SearchSimilar` path**, which collapses a larger
  pool scaling with the caller's `top_k`, so available tail depth varies per request.

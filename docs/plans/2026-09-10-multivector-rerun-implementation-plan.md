# Multivector Re-run Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-10-multivector-rerun-design.md` (commit SHA: `03d6809`)

**Goal:** Give `multivector.py` the five changes spec §3.4 specifies — the arm's beam set explicitly, its rows deduped, index-state and run-file collisions failing closed, and a `probe` subcommand implementing §3.5 — without executing the experiment.

**Architecture:** Two tasks against one harness script. Task 1 changes the existing `cmd_query` path (§3.4 items 1-4). Task 2 adds a `probe` subcommand (§3.4 item 5) implementing §3.5's step 0 and step 1, deriving its tail-query set by reusing `report.per_query_values` rather than reimplementing per-query scoring. The experiment run is an operator procedure, not an SDD task — see "Execution".

**Tech stack:** Python 3.14.4 stdlib, pytest 9.1.1, Qdrant 1.18.2 REST (`/points/search` for the control, `/points/query` for the arm), `ir_measures` from `~/repositories/iverson-benchmark-corpora/python-libs` (imported lazily inside `report.py`), the existing `ingest.py` helpers.

---

## Global Constraints

- **No task executes the experiment.** No Qdrant collection is created, restored, patched or deleted; no `docker compose`; no `stack.py`; no ingest; no `multivector.py build|query|probe|stats` run against the live stack. Both tasks are code plus unit tests. Read-only Qdrant reads are permitted.
- **The box is shared and not idle.** Nothing CPU- or memory-heavy.
- **Test idiom is fixed** (`test_multivector.py:8-14`): `sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))` then `import multivector  # noqa: E402`. Do not add a `conftest.py`, fixture module, or mock factory — the suite has none by design.
- **Test command** (from the repo root): `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q`. No `PYTHONPATH` is needed: `report.py` imports `ir_measures` inside functions, so `import report` is free. Running `probe` for real **does** need `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs`.
- **`per_query_values` takes a measure OBJECT** (`from ir_measures import R; R@50`), never a `parse_measure` string — `report.py:489-499` says so explicitly.
- **Commit messages:** lowercase imperative, no Conventional-Commits prefix, `<file>: <what>` where it helps. Match `git log -- Iverson.Server/Iverson.LoadTest/scripts/`.
- **`multivector.py` is organised pure-functions-first.** New pure functions go in that section beside `rank_chunk_hits`/`collapse_by_doc`, not next to the `cmd_*` handlers.

## File Structure

**Modify:**
- `Iverson.Server/Iverson.LoadTest/scripts/multivector.py` — Task 1 adds `rank_multivector_points` and `existing_run_files` and rewires `cmd_query`; Task 2 adds `tail_query_ids`, `probe_set` and `cmd_probe` plus its CLI subparser.

**Test:**
- `Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py` — new cases for the four new pure functions.

No files are created.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here (spec §5):

- **A2** three gte snapshots present, 98,686,976 / 135,059,968 / 107,074,560 bytes
- **A3** no hard-coded collection name in the query path — `multivector.py:351-353`
- **A4** nothing in run files or scoring depends on collection *name* — `multivector.py:45-94`
- **A5** ~341 MB of aliases fit — 839 GB free
- **A6** TEI templated for model + batch tokens — `docker-compose.yml:180`
- **A7** `--no-deps` required and sufficient — `docker-compose.yml:507`, `:576`
- **A8** `indexing_threshold` direction; the gate's "0" is inverted
- **A11** corpus-fraction match is 65 — 250/19,967 × 5,183 = 64.8946
- **A12** `report.py` takes explicit run paths — `:828`, `:842`
- **A13** the three criteria — `2026-09-GATE-multivector.md:468-472`
- **A14** 300 queries, 339 qrels rows over 300 ids
- **A15** no `--mv-hnsw-ef` today; the arm's beam is unset — `multivector.py:321-323`
- **A16** the changed logic is unit-testable offline — 12 tests, no Qdrant/TEI
- **A17** re-running into the old directory would destroy gate evidence
- **A18** dedupe must precede truncation — `collapse_by_doc` at `:74`
- **A19** the dev-only Qdrant key works
- **A21** `ingest.embed` is model-agnostic — `ingest.py:533`
- **A10** the one-vector exactness conclusion was falsified; the two-part beam rule replaced it

**Deferred by the spec to execution time** — these gate the *operator procedure*, not these tasks: **A1** (alias restore), **A9** (`PATCH` convergence), **A22** (gte model cache), **A23** (explicit-`hnsw_ef` on the `max_sim` path).

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `multivector.py` at `Iverson.Server/Iverson.LoadTest/scripts/` | `ls` — 18,564 B |
| P2 | File path | `test_multivector.py` at the same directory | `ls` — 4,177 B |
| P3 | File path | `report.py` at the same directory (Task 2 imports it) | `ls` — 47,023 B |
| P4 | File path | The original run's evidence exists | `gte-chunks-raw.chunks.trec` 639,872 B, `gte-multivector-raw.chunks.trec` 714,759 B in `runs/`; `qrels.trec` 5,702 B at the **run-dir root**, not in `runs/` |
| P5 | Signature | `report.per_query_values(qrels, run_path, measure, nugget_qrels=None)` → `{query_id: value}` | `report.py:489-499` |
| P6 | Signature | qrels load via `ir_measures.read_trec_qrels(path)` → list | `report.py:899` |
| P7 | Signature | `collapse_by_doc(scored, limit)` takes `[(doc_id, score)]` | `multivector.py:74-81` |
| P8 | Signature | `ingest.qdrant_request(method, path, body=None)` → `(status, parsed)` | `ingest.py:279-281` |
| P9 | Signature | `ingest.embed(text, model, document_prefix, embed_url)` | `ingest.py:533` |
| P10 | Signature | `collection_info(name)` → result dict or `None`; `require_collection(name)` → dict carrying `points_count`, `indexed_vectors_count`, `segments_count`, `status` | `multivector.py:117-125`, `:148-152`; live `GET /collections/...` |
| P23 | Signature | `CHUNK_VECTOR_NAME` = `"body_vector"`, `DOCUMENT_BUDGET` = 50, `CHUNK_TOP_K` = 250 | `multivector.py:30-34` |
| P24 | Signature | `scroll(name, with_vector, payload_fields)` | `multivector.py:127-131` |
| P11 | Signature | `trec_lines`/`summarize_latency` unchanged; new code calls `trec_lines(qid, ranked, tag)` with the same `[(doc_id, score)]` shape | `multivector.py:97-101` |
| P12 | Command | `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q` runs from the repo root | Executed: `12 passed in 0.08s` |
| P13 | Command | Importing `report` needs no `PYTHONPATH`; `probe` at run time does | `report.py` top-level imports are argparse/glob/json/os/sys/datetime only (`:101-106`); `ir_measures` imported inside functions (`:368, :495, :641, :724, :781`); `import report` succeeded with no `PYTHONPATH` |
| P14 | Command | Commit convention is lowercase imperative, no CC prefix | `git log -- .../scripts/`: "refuse a --scores-path that would destroy one of its own files", "report.py: alpha-nDCG@10 via --nugget-qrels ..." |
| P15 | Ordering | Task 2 references no symbol Task 1 introduces; both modify one file, so the order is forced | Task 2 uses `collapse_by_doc`, `scroll`, `ingest.*`, `report.per_query_values` — all pre-existing |
| P16 | Ordering | Task 1 does not modify `collapse_by_doc` itself | Task 1 adds a caller (`rank_multivector_points`); the helper body is untouched |
| P17 | Code validity | `POST /points/query` (the arm's endpoint) accepts `params: {hnsw_ef}` and honours it identically to `/points/search` | Live, 20 in-distribution vectors: `/points/query` `limit` 50 default vs `hnsw_ef` 65 → identical 16/20; vs `hnsw_ef` 100 → 20/20; `/points/query` `ef`=65 ≡ `/points/search` `ef`=65 → 20/20; both return HTTP 200 |
| P18 | Code validity | `R@50` builds as a measure object and `per_query_values` yields the 11 tail ids | `PYTHONPATH=.../python-libs`: `R@50` → `_R`; 300 per-query entries per run; diff → exactly 11 ids `129, 130, 146, 213, 312, 521, 692, 693, 834, 1049, 1110`; mean R@50 delta **−0.0300**, reproducing the gate |
| P19 | Code validity | Python 3.14.4 on this box | `python3 -VV` |
| P20 | Consumer impact | Routing the arm through `collapse_by_doc` keeps every downstream consumer's shape | `mv_ranked` is consumed only by the `short` check (`:330`) and `trec_lines` (`:333`); `collapse_by_doc` returns the same `[(doc_id, score)]`, and the `short` check then measures *distinct* documents, which is what it was always meant to |
| P21 | Consumer impact | No executable caller of `multivector.py query` exists, so a new CLI arg breaks nothing | `grep -rn "multivector.py"` over `docs/`, `Iverson.Server/`, `scripts/` — every hit is prose in a plan or the ranked-changes doc; no script, Makefile or compose entry invokes it |
| P22 | Consumer impact | No workflow legitimately re-runs into an existing run directory, so the refusal blocks nothing real | Same grep as P21 |

## Tasks

### Task 1: `cmd_query` — set the arm's beam, dedupe its rows, fail closed on index state and collisions

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/multivector.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py`

**Interfaces:**
- Produces: `rank_multivector_points(points, limit)` and `existing_run_files(runs_dir)` — Task 2 does not consume either, but both live in the pure-function section Task 2 also edits.

- [ ] **Step 1: Write the failing tests** in `test_multivector.py`, following the existing idiom (no new fixtures).

```python
def mvpoint(doc_id, score):
    return {"id": 1, "score": score, "payload": {"docId": doc_id}}


def test_rank_multivector_points_dedupes_by_docid_keeping_max():
    # Two points sharing a docId is what the gate warned would produce a malformed run.
    ranked = multivector.rank_multivector_points(
        [mvpoint("d1", 0.3), mvpoint("d2", 0.5), mvpoint("d1", 0.9)], 10)
    assert ranked == [("d1", 0.9), ("d2", 0.5)]


def test_rank_multivector_points_truncates_after_the_collapse():
    # Truncate-before-collapse would keep only d1.
    ranked = multivector.rank_multivector_points(
        [mvpoint("d1", 0.9), mvpoint("d1", 0.8), mvpoint("d2", 0.7)], 2)
    assert ranked == [("d1", 0.9), ("d2", 0.7)]


def test_existing_run_files_reports_only_the_two_run_files(tmp_path):
    runs = tmp_path / "runs"
    runs.mkdir()
    (runs / f"{multivector.CHUNKS_RUN_LABEL}.chunks.trec").write_text("x")
    (runs / "unrelated.txt").write_text("x")
    found = multivector.existing_run_files(str(runs))
    assert [f.rsplit("/", 1)[-1] for f in found] == [f"{multivector.CHUNKS_RUN_LABEL}.chunks.trec"]


def test_existing_run_files_empty_on_a_fresh_directory(tmp_path):
    assert multivector.existing_run_files(str(tmp_path)) == []
```

- [ ] **Step 2: Add the two pure functions** to `multivector.py`, in the pure-function section (after `rank_chunk_hits`, before `trec_lines`).

```python
def rank_multivector_points(points, limit):
    """MaxSim points -> document ranking. Qdrant returns one point per document here, but the
    arm must not depend on that: two points sharing a docId would produce a malformed run, and
    the chunk arm already collapses. Same helper, same order -- collapse, then truncate."""
    return collapse_by_doc([(p["payload"]["docId"], p["score"]) for p in points], limit)


def existing_run_files(runs_dir):
    """The run files a `query` into this directory would overwrite. The gate's own
    `gte-chunks-raw` / `gte-multivector-raw` files are unreproducible evidence, so cmd_query
    refuses rather than clobbering them."""
    names = (f"{CHUNKS_RUN_LABEL}.chunks.trec", f"{MULTIVECTOR_RUN_LABEL}.chunks.trec")
    paths = [os.path.join(runs_dir, n) for n in names]
    return [p for p in paths if os.path.exists(p)]
```

- [ ] **Step 3: Extend `cmd_query`'s precondition** — replace the loop that checks only `status`:

```python
    for name in (args.chunks_collection, args.multivector_collection):
        info = require_collection(name)
        if info["status"] != "green":
            sys.exit(f"'{name}' is {info['status']}: wait for indexing to finish before measuring")
        if info["indexed_vectors_count"] != info["points_count"]:
            sys.exit(
                f"'{name}' has {info['indexed_vectors_count']:,} of {info['points_count']:,} vectors "
                f"HNSW-indexed: a segment below indexing_threshold is searched exactly, which is the "
                f"index-state asymmetry this re-run exists to remove. Raise indexing_threshold and "
                f"re-check before measuring.")
        index_state[name] = {k: info[k] for k in ("status", "points_count", "indexed_vectors_count", "segments_count")}
```

- [ ] **Step 4: Refuse a run-file collision**, immediately after `os.makedirs(runs_dir, exist_ok=True)` and before the `try:` that writes anything:

```python
    clash = existing_run_files(runs_dir)
    if clash:
        sys.exit(f"refusing to overwrite {len(clash)} existing run file(s) in {runs_dir}: "
                 f"{', '.join(os.path.basename(p) for p in clash)} -- point --run-dir at a fresh directory")
```

- [ ] **Step 5: Set the arm's beam and route its rows through the collapse.** In the multivector branch of the query loop, replace the body-build and the `mv_ranked` line:

```python
            mv_body = {"query": [vec], "limit": DOCUMENT_BUDGET, "with_payload": ["docId"],
                       "params": {"hnsw_ef": args.mv_hnsw_ef}}
            t0 = time.perf_counter()
            status, resp = ingest.qdrant_request(
                "POST", f"/collections/{args.multivector_collection}/points/query", mv_body)
            latency["multivector"].append((time.perf_counter() - t0) * 1000)
            if status != 200:
                sys.exit(f"query {qid}: multivector query HTTP {status} {resp}")
            mv_ranked = rank_multivector_points(resp["result"]["points"], DOCUMENT_BUDGET)
```

- [ ] **Step 6: Add the CLI argument and record it in the sidecar.** On the `query` subparser:

```python
    q.add_argument("--mv-hnsw-ef", type=int, default=65,
                   help="params.hnsw_ef for the multivector arm (default 65: the corpus-fraction "
                        "match to the control's 250-node beam). Values below the query's own limit "
                        "are clamped up to it by Qdrant and are therefore inert.")
```

and add `"mv_hnsw_ef": args.mv_hnsw_ef` to the `sidecar` dict in `write_outputs`, beside `chunk_top_k` and `document_budget`, so the run's own evidence records the beam it was measured at.

- [ ] **Step 7: Run the suite.**
```bash
python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q
```
Expect 16 passed. Items 1 (`--mv-hnsw-ef`) and 3 (the precondition) are live-stack behaviour and are deliberately not unit-tested — do **not** invent a Qdrant mock for them.

- [ ] **Step 8: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/multivector.py Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py
git commit -m "multivector.py: set the arm's beam explicitly, dedupe its rows, and fail closed on index state and run-file collisions"
```

### Task 2: `probe` subcommand — the exactness probe of spec §3.5

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/multivector.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py`

**Interfaces:**
- Consumes: `collapse_by_doc`, `rank_chunk_hits`, `scroll`, `require_collection`, `ingest.embed`, `ingest.qdrant_request` — all pre-existing.

- [ ] **Step 1: Write the failing tests** for the two new pure functions.

```python
def test_tail_query_ids_are_the_queries_whose_value_differs():
    control = {"a": 1.0, "b": 0.5, "c": 0.25}
    arm = {"a": 1.0, "b": 0.75, "c": 0.25}
    assert multivector.tail_query_ids(control, arm) == ["b"]


def test_tail_query_ids_counts_a_query_missing_from_one_side():
    assert multivector.tail_query_ids({"a": 1.0, "b": 0.5}, {"a": 1.0}) == ["b"]


def test_probe_set_is_bulk_plus_tail_deduped_in_corpus_order():
    ids = ["q1", "q2", "q3", "q4", "q5"]
    assert multivector.probe_set(ids, ["q5", "q2"], 2) == ["q1", "q2", "q5"]


def test_probe_set_refuses_a_tail_id_absent_from_the_corpus():
    with pytest.raises(ValueError):
        multivector.probe_set(["q1", "q2"], ["q9"], 1)
```

- [ ] **Step 2: Add the two pure functions**, in the pure-function section.

```python
def tail_query_ids(control_values, arm_values):
    """Query ids whose per-query measure value differs between the two runs -- per the design
    these are the only queries whose exactness bears on the gate, because recall failures live
    in a small tail and a bulk sample lands in the ~85% where nothing moves. A query present on
    only one side counts as differing."""
    keys = set(control_values) | set(arm_values)
    return sorted(k for k in keys if control_values.get(k) != arm_values.get(k))


def probe_set(query_ids, tail_ids, bulk_n):
    """The probe set: the first bulk_n corpus queries plus every tail id, de-duplicated and
    returned in corpus order. A tail id absent from the corpus is an error rather than a silent
    drop -- it would mean the tail was derived against a different corpus."""
    missing = [t for t in tail_ids if t not in set(query_ids)]
    if missing:
        raise ValueError(f"tail query ids not present in the corpus: {missing}")
    chosen = set(query_ids[:bulk_n]) | set(tail_ids)
    return [q for q in query_ids if q in chosen]
```

- [ ] **Step 3: Add the sweep constants** beside the other module constants:

```python
# Every probe point must exceed its collection's own `limit`: Qdrant clamps params.hnsw_ef up
# to `limit`, so a smaller value is inert and would score as a spurious agreement.
CONTROL_HNSW_EF_SWEEP = (250, 500, 1000, 4000)
ARM_HNSW_EF_SWEEP = (65, 100, 250, 1000)
PROBE_BULK_QUERIES = 30
```

- [ ] **Step 4: Implement `cmd_probe`.** Structure, in order:

  1. `require_collection` on both collections (no index-state assertion here — the probe is diagnostic and must be runnable before §3.3 as well as after).
  2. Derive the tail set. Keep `import report`, `import ir_measures` and `from ir_measures import R` **function-local, inside `cmd_probe`** — matching `report.py`'s own convention (`:368, :495, ...`) and keeping `build`/`query`/`stats` free of any `ir_measures` dependency. Load qrels with `ir_measures.read_trec_qrels(args.tail_qrels)`, call `report.per_query_values(qrels, args.tail_control_run, R@50)` and the same for `args.tail_arm_run`, then `tail_query_ids(...)`. Print the count and the ids.
  3. Load `<run-dir>/beir/queries.jsonl`, build `probe_set(ids, tail, PROBE_BULK_QUERIES)`, embed each query once with `ingest.embed` and keep the vectors.
  4. **Step 0** — on the multivector collection at `limit` `DOCUMENT_BUDGET`, three configurations: no `params`, `hnsw_ef` 65, `hnsw_ef` 1000. Rank each with `rank_multivector_points`. Count queries where default ≠ 65 (`settable_n`) and where 65 ≠ 1000 (`positive_control_n`).
  5. **Step 1** — for each collection, sweep its constant. Control: `POST /points/search` at `limit` `CHUNK_TOP_K` with `params.hnsw_ef`, ranked by `rank_chunk_hits` through the `key_to_doc` map built from `scroll(args.object_collection, False, ["key", "docId"])`. Arm: `POST /points/query` at `limit` `DOCUMENT_BUDGET`, ranked by `rank_multivector_points`. For each collection report, per sweep point, the number of queries whose top-50 document ranking differs from that collection's operating point (250 for the control, 65 for the arm).
  6. Write `<run-dir>/runs/probe.json` with the tail ids, the probe-set ids, `settable_n`, `positive_control_n`, and the per-sweep-point agreement counts.
  7. **Fail closed:** if `settable_n == 0` **and** `positive_control_n == 0`, `sys.exit` with a message saying the arm's beam is not settable through `params.hnsw_ef`, that the design's correction therefore does not work, and that the run must stop for re-approval before any gated measurement.

- [ ] **Step 5: Add the subparser.**

```python
    p = sub.add_parser("probe", help="exactness probe: is either arm's retrieval approximate?")
    add_common_args(p)
    p.add_argument("--run-dir", required=True, help="run directory holding beir/queries.jsonl; writes runs/probe.json")
    p.add_argument("--model", required=True)
    p.add_argument("--embed-url", required=True)
    p.add_argument("--tail-qrels", required=True, help="qrels.trec of the ORIGINAL run (its run-dir root, not runs/)")
    p.add_argument("--tail-control-run", required=True, help="original gte-chunks-raw.chunks.trec")
    p.add_argument("--tail-arm-run", required=True, help="original gte-multivector-raw.chunks.trec")
    p.set_defaults(func=cmd_probe)
```

- [ ] **Step 6: Run the suite.**
```bash
python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q
```
Expect 20 passed. `cmd_probe` itself is live-stack behaviour and is not unit-tested.

- [ ] **Step 7: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/multivector.py Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py
git commit -m "add multivector.py probe: is either arm's retrieval approximate at its operating beam"
```

## Execution: operator procedure, not SDD tasks

The experiment itself is **not** in this plan as tasks, and no subagent should run it. Restoring Qdrant collections, recreating `tei-embed` and taking the gated measurement are side effects outside the worktree that `git revert` cannot undo, they require an idle box, and two of them carry explicit stop-and-re-approve branches (spec A1's alias-restore fallback, and §3.5 step 0's fail-closed exit). Those are precisely the conditions under which `subagent-driven-development` requires a subagent to stop and ask.

The procedure is spec §3.1-3.6 and is not restated here. Its preconditions are spec §6 — in particular, **the box must be idle**, because criterion 3 is a p95 latency ratio.

## Tasks NOT in this plan

Inherited from spec §7:

- **Adoption.** Even a clean PASS does not adopt the layout: the parent gate records that MaxSim does not report which row won, so `SearchChunks`'s chunk text/index contract would need a local argmax over retrieved rows plus chunk texts in the point payload — a second round-trip and payload growth, neither measured. That cost belongs to an adoption spec.
- **The long-document question.** This stays a layout verdict at 512/448 on SciFact. At 2048/1792 SciFact is 81% single-chunk and the arm degenerates.
- **The `gte-chunks-api` arm** and the model observation. Both were context in the original gate, not gated criteria; neither is re-run, so the API is never involved.
- Re-deciding gte-modernbert-base as a model (ranked-changes §7, closed).

## Known issues inherited from spec

From spec §8:

- `BUILD UNKNOWN` will print for both arms: `multivector.py` writes no build sidecar. Inert for the gated pair — both arms come from the same script in the same interleaved loop against the same Qdrant.
- The two modes are interleaved per query sharing one embedding, with the multivector query always second, so any warm-up effect is charged to one arm. A fully alternating order would remove this last ordering confound; it is not addressed here.

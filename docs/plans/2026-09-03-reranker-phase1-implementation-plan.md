# Cross-Encoder Reranking, Phase 1 (Harness Spike) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-03-reranker-design.md` (commit SHA: `39610d4`)

**Scope of this plan:** spec §3.3 (Phase 1), §7.1–§7.5 (evaluation protocol as it applies to Phase 1), and the
`reranker` compose service from §5. It ends with the §3.4 gate verdict. Phase 2 (§3.5, §4, §5 server
options, §6, Helm subchart) is a separate plan written after the gate result exists — per the user's
ruling at plan-write time.

**Goal:** Measure whether cross-encoder reranking of the 50 max-passage documents beats the unreranked
control on SciFact `chunks` nDCG@10, in the harness alone, with a statistically valid run family and no
server change.

**Architecture:** `benchmark-query` keeps requesting 250 fused+MMR'd chunks; after max-passage
aggregation to 50 documents it (optionally) sends each document's winning chunk to TEI `/rerank`,
replaces the document's score with the cross-encoder score, re-sorts, and writes the TREC run file.
`report.py` gains the baseline-materialisation fix, a `--pair RUN=BASELINE` family construction, and
the per-pair §7.1.2 pool checks. A `reranker` compose service (TEI CPU image) behind a profile serves
the models.

**Tech stack:** .NET 10 console app (`Iverson.LoadTest`), xunit 2.9.3 + FluentAssertions 7.0.0,
`System.Net.Http` + `System.Text.Json` (BCL), Python 3 + `ir_measures 0.4.3` / `scipy 1.18.1` via
`PYTHONPATH`, pytest 9.1.1 (system), Docker Compose 2.40.3, TEI `cpu-1.8`.

---

## Global Constraints

Copied from the spec; every task must hold to them.

- `DocumentBudget = 50` and `ChunkBudgetMultiplier = 5` are fixed for the whole sweep and never varied
  per configuration (`BenchmarkQueryScenario.cs:39-41`).
- **The control path is byte-identical to today.** With no `--rerank-url`, `benchmark-query` must
  produce exactly the run files it produces on `main` now.
- **Fail loud (§6).** Any reranker failure — unreachable, non-success status, timeout, malformed
  response — aborts the run. No degrade-to-fused-order path anywhere.
- **The unit reranked is the document** (§3.1): exactly `DocumentBudget` pairs per query, one per
  document, `Text` = that document's winning chunk.
- **Rescore then re-sort** (§3.3): after `Score := cross-encoder score`, re-sort with
  `DocumentRanking.CollapseByDocId(rescored, DocumentBudget)`. `TrecRunWriter` sorts nothing.
- **Batch size 8** for `/rerank` (§4: TEI `max_batch_requests: 8`, `max_client_batch_size: 32`; 8 is
  what §9 measured).
- **§7.1.2 per pair:** the run's per-query doc-id **set** must equal its control's for every query,
  and the ranked **sequence** must differ for at least **25 %** of queries. Either failure makes the
  arm invalid and the report exits non-zero.
- **One Holm construction at m = 3** over exactly the declared pairs A1–A0, A2–A0, A3–A0′ (§7.2, §7.5).
- **Never mount a model cache under `/tmp`** (§7.6.3) — it is a 4.9 GB tmpfs on this box.
- Statistics: `PERMUTATION_SEED = 20260831`, `PERMUTATION_RESAMPLES = 10_000`, `HOLM_ALPHA = 0.05`
  unchanged (`report.py:102-104`).
- Tests are written to fail against a specific mutation, and the mutations are **run** (§8).

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/scripts/test_report.py` — pytest suite for `report.py`'s paired section (baseline fix, `--pair` family, pool checks).
- `Iverson.Server/Iverson.LoadTest/Benchmark/TeiRerankClient.cs` — `/info` and batched `/rerank` over an `HttpClient`; throws on anything but a complete success.
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/TeiRerankClientTests.cs` — fake-handler tests for batching, index mapping, fail-loud.

**Modify**
- `Iverson.Server/Iverson.LoadTest/scripts/report.py` — materialise the baseline run; `--pair`; per-pair pool checks.
- `Iverson.Server/Iverson.LoadTest/Benchmark/DocumentRanking.cs` — 3-tuple overload keeping the winning chunk's text; the 2-tuple overload delegates to it.
- `Iverson.Server/Iverson.LoadTest/Benchmark/MaxPassageAggregator.cs` — 3-tuple overload returning `(DocId, Score, Text)` rows.
- `Iverson.Server/Iverson.LoadTest/Program.cs` — `--rerank-url`, `--rerank-model` flags and help text.
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` — `/info` guard and sidecar record; rescore + re-sort on the chunks path when enabled.
- `Iverson.Server/docker-compose.yml` — `reranker` service behind `profiles: ["reranker"]`; `reranker_models` volume; `VectorRanking__Lambda` interpolation on `iverson-api`.

**Test (extend)**
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/DocumentRankingTests.cs`
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/MaxPassageAggregatorTests.cs`

## Inherited from spec

The following were verified by `thorough-brainstorming` and four `critical-design-review` rounds at
spec-write time and are NOT re-verified here. Trusted as ground truth (spec §10, A1–A38); the ones
this plan leans on directly:

- A1 `ChunkSearchResponse` carries `chunk_text` (`object_search.proto:128`).
- A2 the harness requests `DocumentBudget × ChunkBudgetMultiplier` = 250 chunks (`BenchmarkQueryScenario.cs:300`).
- A3 `MaxPassageAggregator.Aggregate` takes chunks + key map + budget (`MaxPassageAggregator.cs:32`).
- A12 TEI `/rerank` returns objects carrying `index` and `score`; A13 `/info` exposes `model_id`, `max_input_length`, `auto_truncate`.
- A17 SciFact artifacts present (`corpus.jsonl`, `queries.jsonl`, `qrels.trec`, 339 rows); A19 the `chunked-512` collection restores from `scifact-512-qdrant-snapshots/` per its `RESTORE.md`.
- A20 3.85 chunks/document on the Phase 1 baseline; A32 Phase 1's group-by-doc-id partitions like `parent_id` (keymap 5,183 → 5,183 distinct).
- A21 / A25 `TrecRunWriter.cs:23-31` writes positionally, `rank = i + 1`, sorts nothing; the only score sort is `DocumentRanking.CollapseByDocId`.
- A23 / A26 `parent_key`, `chunk_text`, `score` arrive together; the harness discards `chunk_text` at `:309`; `ChunkAggregation.Ranked` is `(DocId, Score)` and the max-tracking lives in `CollapseByDocId`, which `RunSimilarAsync` (`:292`) also calls.
- A18 / A34 `report.py:403-482` implements the paired statistics; `report.py:552` binds the baseline generator once, so R@50 and AP are scored against an all-zero baseline (reproduced: same generator R@50 mean 0.0, materialised 0.921).
- A35 one `--baseline`; the family is every other discovered run; directory glob includes `.similar` files.
- A36 SciFact oracle@50 on `chunked-512` is 0.9216 (the stated 0.911 bound is conservative).
- A9 Compose 2.40.3 supports `profiles:`.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (2026-09-03, repo HEAD `39610d4`).

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `scripts/test_report.py`, `Benchmark/TeiRerankClient.cs`, `LoadTest.Tests/Benchmark/TeiRerankClientTests.cs` are new files | `ls` on all three: No such file or directory |
| 2 | File path | `Iverson.Server/docker-compose.yml` is the only compose file outside worktrees | `find . -name "docker-compose*.y*ml"` → that path plus three `.worktrees/`/`.claude/worktrees/` copies |
| 3 | File path | `docs/plans/` is gitignored | `.gitignore:49` `**/docs/plans/` → commit with `git add -f` |
| 4 | Signature | `DocumentRanking.CollapseByDocId(IEnumerable<(string DocId, double Score)>, int)` → `IReadOnlyList<(string DocId, double Score)>`; keeps max per doc, ties keep first-seen (`score > existing`) | `DocumentRanking.cs:15-32` |
| 5 | Signature | `MaxPassageAggregator.Aggregate(IEnumerable<(string ParentKey, double Score)>, IReadOnlyDictionary<string,string>, int)` → `ChunkAggregation(Ranked, UnresolvedParentKeys)` | `MaxPassageAggregator.cs:12-14, 32-49` |
| 6 | Signature | generated `ChunkSearchResponse` exposes `public string ParentKey` and `public string ChunkText` | `Iverson.Client.Contracts/obj/Debug/net10.0/ObjectSearch.cs:3498, 3513` |
| 7 | Signature | `CommandFlags` is a sealed class of `init` properties parsed by private `StrFlag(string[] a, string f, string d)`; `ConfigLabel` is the pattern to copy | `Program.cs:380-404, 420-428` |
| 8 | Signature | `LoadTestConfig(PostgresCs, StarRocksCs, KafkaBootstrap, HttpUrl)`; `HttpUrl` from `IVERSON_HTTP_URL` (default `http://localhost:8081`) | `Program.cs:34, 377-378` |
| 9 | Wire shape | TEI `/rerank` request body is `{"query": str, "texts": [str], "raw_scores": false}`; each batch's response is a JSON array of `{index, score}` with `index` relative to that batch | design-session measurement script (transcript): `body={"query":q,"texts":docs[i:i+batch_size],"raw_scores":False}` posted per batch to `/rerank`, `scores += json.load(r)`; A12 |
| 10 | Wire shape | `/info` JSON has top-level `max_input_length` (int), `auto_truncate` (bool), `max_batch_requests: 8`, `max_client_batch_size: 32`; `model_id` per A13 | design-session `/info` capture (transcript): `"max_input_length": 512, "max_batch_requests": 8, "max_client_batch_size": 32, "auto_truncate": false` |
| 11 | Code validity | `ir_measures.read_trec_run(path)` returns a generator; `list()` materialises it; `iter_calc` accepts the list; rows are `ScoredDoc(query_id, doc_id, score)` | reproduced this session: `type(run).__name__ == 'generator'`; re-iterating gives R@50 mean 0.0, materialised 0.921; `ScoredDoc._fields == ('query_id','doc_id','score')` |
| 12 | Signature | `paired_comparison(baseline_values, run_path, qrels, measure)` returns `None` or a dict with keys `n, baseline_ids, run_ids, delta, t_stat, t_p, perm_p, ci, d_z, mde, changed`; `print_compare_block(baseline_path, run_path, measure, comp, holm_p_adj, family_size, baseline_composite, run_composite)` prints `  delta            {delta:+.4f}` and `  Holm ({family_size} tests)`; `holm_adjust(pvalues)`; `load_build_composite(path)` reads only the sidecar's `composite` key | `report.py:375-390, 403-482, 483-520, 173-191` |
| 13 | Signature | `run_paired_statistics(qrels, run_paths, baseline_path, measures)` is called only from `main()`; `--run` is `action="append", required=True`; `measures = [nDCG@10, R@50, AP]`; `main()` is guarded by `if __name__ == "__main__"` | `report.py:531, 604-657`, last 3 lines |
| 14 | Code validity | `report.py`'s module-level imports are stdlib only (`argparse, glob, json, os, sys, datetime`); `ir_measures` is imported inside functions — so pytest can import it as module `report` without `PYTHONPATH` at import time | `report.py:90-95` |
| 15 | Code validity | `Iverson.LoadTest` targets `net10.0`; `HttpClient`, `System.Text.Json`, `JsonNode`/`JsonObject` are already used in `BenchmarkQueryScenario.cs` | `Iverson.LoadTest.csproj:4`; `BenchmarkQueryScenario.cs:1-2, 123, 147, 158` |
| 16 | Code validity | LoadTest.Tests uses xunit 2.9.3 + FluentAssertions 7.0.0 and references `Iverson.LoadTest.csproj`; the fake `HttpMessageHandler` shape to mirror is a `private sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler` overriding `SendAsync` and capturing `LastRequestBody` | `Iverson.LoadTest.Tests.csproj:10, 15, 22`; `Iverson.Embeddings.Tests/EmbeddingServiceTests.cs:14-30` |
| 17 | Code validity | C# `ValueTuple<string,double>` and `ValueTuple<string,double,string>` are distinct types, so same-name overloads resolve by arity; `chunks.Select(c => (c.ParentKey, c.Score))` converts to `IEnumerable<(string ParentKey, double Score)>` (tuple names are not part of the conversion) | language rule; `MaxPassageAggregator.Aggregate(chunks, …)` at `BenchmarkQueryScenario.cs:317` already passes a `List<(string ParentKey, double Score)>` |
| 18 | Command | `dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj` runs the 4 existing classes with no containers | run at plan-write time: `Passed! Failed: 0, Passed: 34, Duration: 858 ms` |
| 19 | Command | `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_report.py -q` — pytest 9.1.1 on system `python3`; `ir_measures 0.4.3`, `scipy 1.18.1`, `numpy 2.5.2` in `python-libs` | `python3 -c "import pytest"` → 9.1.1; `ls python-libs` |
| 20 | Command | image tag `ghcr.io/huggingface/text-embeddings-inference:cpu-1.8` exists and is already pulled on this box (design measured on `cpu-latest`; `cpu-1.8.3` also exists) | `docker manifest inspect` succeeded for both tags; `docker images` lists `cpu-1.8` (690 MB, 10 months old) |
| 21 | Command | `benchmark-query` invocation and env are recorded from the last SciFact sweep: `source /home/ben/iverson-benchmark-data/bench-env.sh`, `python3 scripts/stack.py query`, then from `Iverson.Server/Iverson.LoadTest`: `dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label <label>`, `RUN=~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26` (has `beir/queries.jsonl`, `keymap.json`, `qrels.trec`) | `scifact-run-2026-08-26/NEXT-STEPS.md:11-17`; `ls $RUN/beir` → `corpus.jsonl queries.jsonl`; `bench-env.sh` exists (see Task 7 step 1 check) |
| 22 | Command | λ on the server is `VectorRankingOptions.Lambda` (default 0.70) bound from section `"VectorRanking"` → env `VectorRanking__Lambda`; compose sets no such variable today | `VectorRankingOptions.cs:5, 17`; `ServiceCollectionExtensions.cs:58`; `grep VectorRanking__ docker-compose.yml` → none |
| 23 | Command | Compose `qdrant` publishes `6333:6333` with API key `dev-only-not-for-production-qdrant-key-0123456789`, matching `RESTORE.md`'s curl | `docker-compose.yml:106-115`; `RESTORE.md` |
| 24 | Command | Compose has a top-level `volumes:` block (`postgres_data`, `qdrant_data`, `ollama_data`, …) to add `reranker_models` to; `ollama` is the optional-service shape (image, container_name, ports, volumes) | `docker-compose.yml:124-136, 521-527` |
| 25 | Command | Commit messages are lower-case imperative with no prefix | `git log --oneline -12` |
| 26 | Ordering | Task 5 consumes Task 3's overloads and Task 4's client; Task 7 consumes Tasks 1–6; Tasks 1–2 (Python) are independent of 3–6; Task 6 is independent of 3–5; nothing consumes a later task's symbol | plan construction; Tasks 1 and 2 both edit `run_paired_statistics`, so 1 precedes 2 |
| 27 | Consumer impact | `CollapseByDocId` callers: `MaxPassageAggregator.cs:48`, `BenchmarkQueryScenario.cs:292`, `DocumentRankingTests.cs` (7 tests); `MaxPassageAggregator.Aggregate` callers: `BenchmarkQueryScenario.cs:317`, `MaxPassageAggregatorTests.cs` (6 tests). Both existing signatures are kept; the 2-tuple `CollapseByDocId` delegates with `""` as text and projects it away, so max-and-tie behaviour is unchanged | `grep -rn "CollapseByDocId\|MaxPassageAggregator.Aggregate" Iverson.Server` |
| 28 | Consumer impact | `.meta.json` sidecar consumer `load_build_composite` reads only `composite`; adding a `reranker` object is safe | `report.py:173-191` |
| 29 | Consumer impact | The unmerged branch `benchmark-exclude-self` also edits `Program.cs` (+13) and `BenchmarkQueryScenario.cs` (+31/−9); Task 5 will conflict with it on merge. Not a dependency of this plan | `git diff main...benchmark-exclude-self --stat` |
| 30 | Command | `cross-encoder/ms-marco-MiniLM-L-6-v2` and `BAAI/bge-reranker-base` are NOT in the local TEI model cache; Task 7's first start of each downloads it (network required) | `ls ~/.cache/tei-bench-models` → embedding models only |
| 31 | Code validity | Task 1's self-comparison test fails against the current script exactly as stated: identical 4-query run files paired via `run_paired_statistics` print one `delta +0.0000` (nDCG@10) and then `delta +1.0000` (R@50) and `delta +0.7500` (AP) — each the run's own aggregate; the regex `delta\s+\+0\.0000` matches once, so `assert 1 == 3` | run at plan-write time against `report.py` at `39610d4` with the plan's `write_run`/`write_qrels` fixtures |

## Tasks

### Task 1: `report.py` — materialise the baseline run, with the self-comparison test

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/report.py:551-558`
- Create: `Iverson.Server/Iverson.LoadTest/scripts/test_report.py`

**Interfaces:**
- Produces: `test_report.py`'s `write_run(path, rows)` / `write_qrels(path, rows)` helpers, reused by Task 2's tests.

- [ ] **Step 1: Write the failing test.** Create `scripts/test_report.py`:

```python
"""pytest suite for report.py's paired-statistics section. Run with:

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \
        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_report.py -q

report.py imports ir_measures lazily inside functions, so importing it here needs no PYTHONPATH;
running the paired section does."""
import os
import re
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import report  # noqa: E402


def write_qrels(path, rows):
    """rows: [(qid, docid, rel)]"""
    with open(path, "w", encoding="utf-8") as f:
        for qid, docid, rel in rows:
            f.write(f"{qid} 0 {docid} {rel}\n")


def write_run(path, rows, tag="t"):
    """rows: [(qid, [docid, ...])] -- ranks 1..n, descending scores n..1 like TrecRunWriter."""
    with open(path, "w", encoding="utf-8") as f:
        for qid, docids in rows:
            for i, docid in enumerate(docids):
                f.write(f"{qid} Q0 {docid} {i + 1} {float(len(docids) - i):.6f} {tag}\n")


QRELS = [("q1", "d1", 1), ("q2", "d2", 1), ("q3", "d3", 1), ("q4", "d4", 1)]
BASE = [("q1", ["d1", "d2", "d3"]), ("q2", ["d1", "d2", "d3"]),
        ("q3", ["d3", "d1", "d2"]), ("q4", ["d2", "d4", "d1"])]


@pytest.fixture
def qrels_path(tmp_path):
    p = tmp_path / "qrels.trec"
    write_qrels(p, QRELS)
    return str(p)


def load_qrels(path):
    import ir_measures
    return list(ir_measures.read_trec_qrels(path))


def test_self_comparison_is_zero_delta_on_every_measure(tmp_path, qrels_path, capsys):
    """A run compared to a byte-identical copy must report delta +0.0000 for nDCG@10, R@50 AND AP.
    Against the unfixed script only the first measure does: the baseline generator is consumed by
    nDCG@10, and R@50 / AP are then paired against an all-zero baseline (spec A34)."""
    from ir_measures import AP, R, nDCG
    base = tmp_path / "base.chunks.trec"
    copy = tmp_path / "copy.chunks.trec"
    write_run(base, BASE, tag="base")
    write_run(copy, BASE, tag="copy")

    report.run_paired_statistics(load_qrels(qrels_path), [str(base), str(copy)], str(base),
                                 [nDCG @ 10, R @ 50, AP])

    out = capsys.readouterr().out
    assert len(re.findall(r"delta\s+\+0\.0000", out)) == 3, out
```

- [ ] **Step 2: Run it and watch it fail on R@50 and AP.**

```bash
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_report.py -q
```
Expected: `assert 1 == 3` (only nDCG@10 reports `+0.0000`; R@50 and AP report the run's own aggregate).

- [ ] **Step 3: Fix `run_paired_statistics`.** At `report.py:552` replace

```python
    baseline_run = ir_measures.read_trec_run(baseline_path)
```
with
```python
    # read_trec_run returns a GENERATOR. Bound once and re-iterated per measure below, the first
    # measure consumed it and every later measure paired against an EMPTY baseline -- which
    # ir_measures scores as 0.0 per query without error, so R@50 and AP deltas came back as each
    # run's own aggregate (spec A34). Materialise it once; the compared runs are re-read per
    # measure by per_query_values and were never affected.
    baseline_run = list(ir_measures.read_trec_run(baseline_path))
```

- [ ] **Step 4: Run the test again.** Same command; expected `1 passed`.

- [ ] **Step 5: Run the mutation.** Revert the `list(...)` wrapper, run the test, confirm it fails with `assert 1 == 3`; restore the fix. Record the mutation and its failure line in the commit message body.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/report.py Iverson.Server/Iverson.LoadTest/scripts/test_report.py
git commit -m "materialise report.py's baseline run so every measure is paired against real values"
```

### Task 2: `report.py` — `--pair RUN=BASELINE` family and the per-pair §7.1.2 pool checks

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/report.py` (`run_paired_statistics` region `:531-585`, `main()` `:596-657`, module docstring step 4)
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/test_report.py`

**Interfaces:**
- Consumes: Task 1's `write_run` / `write_qrels` helpers.
- Produces: the `--pair` CLI used verbatim by Task 7 step 8.

- [ ] **Step 1: Write the failing tests.** Append to `test_report.py`:

```python
def _three_arm_family(tmp_path):
    """A0 (control), A1 and A2 differ from A0 in ORDER on every query (same sets); A0' is a
    second control with a different doc SET on q4; A3 reorders A0'. Returns paths."""
    a0 = tmp_path / "a0.chunks.trec"
    a1 = tmp_path / "a1.chunks.trec"
    a2 = tmp_path / "a2.chunks.trec"
    a0p = tmp_path / "a0prime.chunks.trec"
    a3 = tmp_path / "a3.chunks.trec"
    reorder = lambda rows: [(q, list(reversed(d))) for q, d in rows]
    write_run(a0, BASE, "a0")
    write_run(a1, reorder(BASE), "a1")
    write_run(a2, [(q, d[1:] + d[:1]) for q, d in BASE], "a2")
    base_prime = BASE[:3] + [("q4", ["d2", "d4", "d5"])]
    write_run(a0p, base_prime, "a0p")
    write_run(a3, reorder(base_prime), "a3")
    return a0, a1, a2, a0p, a3


def test_pair_family_is_exactly_the_declared_pairs(tmp_path, qrels_path, capsys):
    from ir_measures import AP, R, nDCG
    a0, a1, a2, a0p, a3 = _three_arm_family(tmp_path)
    pairs = report.parse_pairs([f"{a1}={a0}", f"{a2}={a0}", f"{a3}={a0p}"])

    report.run_pair_statistics(load_qrels(qrels_path), pairs, [nDCG @ 10, R @ 50, AP])

    out = capsys.readouterr().out
    assert out.count("Holm (3 tests)") == 9, out          # 3 pairs x 3 measures
    assert "a3.chunks.trec  vs  a0prime.chunks.trec" in out
    assert "a3.chunks.trec  vs  a0.chunks.trec" not in out


def test_pool_check_rejects_a_changed_document_set(tmp_path, qrels_path):
    from ir_measures import AP, R, nDCG
    a0, a1, a2, a0p, a3 = _three_arm_family(tmp_path)
    # a3 vs a0 (NOT its own control): q4 holds d5 instead of d1 -- the pool moved.
    with pytest.raises(SystemExit) as e:
        report.run_pair_statistics(load_qrels(qrels_path),
                                   report.parse_pairs([f"{a3}={a0}"]), [nDCG @ 10, R @ 50, AP])
    assert "pool changed" in str(e.value) and "1 of 4" in str(e.value)


def test_pool_check_rejects_too_few_reordered_queries(tmp_path, qrels_path):
    from ir_measures import AP, R, nDCG
    a0 = tmp_path / "a0.chunks.trec"
    a1 = tmp_path / "a1.chunks.trec"
    write_run(a0, BASE, "a0")
    # Identical to a0 on 3 of 4 queries; 25% differ -- exactly at the threshold, so it PASSES...
    write_run(a1, BASE[:3] + [("q4", list(reversed(BASE[3][1])))], "a1")
    report.check_pool(str(a1), str(a0))
    # ...and 0 of 4 differ fails.
    write_run(a1, BASE, "a1")
    with pytest.raises(SystemExit) as e:
        report.check_pool(str(a1), str(a0))
    assert "0 of 4" in str(e.value) and "25" in str(e.value)


def test_pair_and_baseline_are_mutually_exclusive(tmp_path, qrels_path, monkeypatch):
    a0 = tmp_path / "a0.chunks.trec"
    write_run(a0, BASE, "a0")
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(a0), "--qrels", qrels_path,
                                      "--baseline", str(a0), "--pair", f"{a0}={a0}"])
    with pytest.raises(SystemExit) as e:
        report.main()
    assert "--pair" in str(e.value) and "--baseline" in str(e.value)
```

- [ ] **Step 2: Run them; expected failures are `AttributeError: module 'report' has no attribute 'parse_pairs'` (and `check_pool`, `run_pair_statistics`).**

- [ ] **Step 3: Implement.** In `report.py`, after `run_paired_statistics` (`:585`), add:

```python
# ── Step 4b: declared-pair statistics (--pair RUN=BASELINE) ────────────────────────────

POOL_MIN_REORDERED_FRACTION = 0.25   # spec §3.3: differs-from-control, per-query ranked doc-id sequence


def parse_pairs(values):
    """Each --pair value is RUN=BASELINE (split on the FIRST '='; paths may not contain '=').
    Both must exist and differ. Returns [(run_path, baseline_path)] in the order given, which
    is also the order the family is printed in."""
    pairs = []
    for value in values:
        if "=" not in value:
            sys.exit(f"--pair {value}: expected RUN=BASELINE")
        run_path, baseline_path = value.split("=", 1)
        for p in (run_path, baseline_path):
            if not os.path.isfile(p):
                sys.exit(f"--pair {value}: {p} is not a file")
        if os.path.abspath(run_path) == os.path.abspath(baseline_path):
            sys.exit(f"--pair {value}: a run cannot be its own baseline")
        pairs.append((run_path, baseline_path))
    return pairs


def ranked_doc_ids(run_path):
    """{query_id: [doc_id, ...]} in FILE order -- TrecRunWriter writes rank order positionally
    (spec A25), so file order is the ranked sequence. read_trec_run is a generator; consumed once here."""
    import ir_measures

    sequences = {}
    for row in ir_measures.read_trec_run(run_path):
        sequences.setdefault(row.query_id, []).append(row.doc_id)
    return sequences


def check_pool(run_path, baseline_path):
    """Spec §7.1.2, both halves, per pair. Reranking rescores the documents the control already
    selected, so (1) every query's doc-id SET must equal the control's -- if any differs the pool
    changed and the arm is invalid -- and (2) the ranked SEQUENCE must differ for at least
    POOL_MIN_REORDERED_FRACTION of the queries, or the reranker did not run (a silent fallback
    would otherwise score as a null result and read as 'reranking does not help'). Both fail
    loud via sys.exit; nothing downstream may run on an invalid arm. Prints one [pool] line."""
    run_seq = ranked_doc_ids(run_path)
    base_seq = ranked_doc_ids(baseline_path)
    common = sorted(set(run_seq) & set(base_seq))
    if not common:
        sys.exit(f"[pool] {os.path.basename(run_path)} vs {os.path.basename(baseline_path)}: "
                 "no overlapping queries")
    set_changed = [q for q in common if set(run_seq[q]) != set(base_seq[q])]
    reordered = [q for q in common if run_seq[q] != base_seq[q]]
    fraction = len(reordered) / len(common)
    print(
        f"[pool] {os.path.basename(run_path)}  vs  {os.path.basename(baseline_path)}: "
        f"{len(common):,} queries, set changed on {len(set_changed):,}, "
        f"sequence differs on {len(reordered):,} ({fraction * 100:.1f}%)"
    )
    if set_changed:
        sys.exit(
            f"[pool] ARM INVALID: pool changed -- the document set differs from "
            f"{os.path.basename(baseline_path)} on {len(set_changed)} of {len(common)} queries "
            f"(first: {set_changed[0]}). Reranking cannot change which documents are in the pool "
            "(spec §7.1.2); this arm was produced by a different pool and must not be scored."
        )
    if fraction < POOL_MIN_REORDERED_FRACTION:
        sys.exit(
            f"[pool] ARM INVALID: ranked sequence differs from {os.path.basename(baseline_path)} "
            f"on only {len(reordered)} of {len(common)} queries ({fraction * 100:.1f}%); at least "
            f"{POOL_MIN_REORDERED_FRACTION * 100:.0f}% is required (spec §3.3). The reranker "
            "most likely did not run."
        )


def run_pair_statistics(qrels, pairs, measures):
    """Like run_paired_statistics, but the family is EXACTLY the declared pairs -- each run
    against its own baseline -- and Holm runs once per measure over those pairs and nothing
    else (spec §7.2: A1-A0, A2-A0, A3-A0'; one construction at m = 3). Every pair passes
    check_pool before any statistic is computed. Baseline per-query values are materialised
    once per distinct baseline per measure (Task 1's fix applies here by construction)."""
    import ir_measures

    for run_path, baseline_path in pairs:
        check_pool(run_path, baseline_path)

    baseline_runs = {
        p: list(ir_measures.read_trec_run(p)) for p in {b for _, b in pairs}
    }
    composites = {p: load_build_composite(p) for pair in pairs for p in pair}

    for measure in measures:
        baseline_values = {
            p: {m.query_id: m.value for m in ir_measures.iter_calc([measure], qrels, run)}
            for p, run in baseline_runs.items()
        }
        comparisons = [
            (run_path, baseline_path,
             paired_comparison(baseline_values[baseline_path], run_path, qrels, measure))
            for run_path, baseline_path in pairs
        ]
        valid = [(r, b, c) for r, b, c in comparisons if c is not None]
        holm_adjusted = holm_adjust([c["perm_p"] for _, _, c in valid])
        holm_by_run = dict(zip((r for r, _, _ in valid), holm_adjusted))
        family_size = len(valid)

        for run_path, baseline_path, comp in comparisons:
            if comp is None:
                print(f"\n[compare] {os.path.basename(run_path)}  vs  "
                      f"{os.path.basename(baseline_path)}        ({measure})")
                print("  !! NO OVERLAPPING QUERIES -- cannot compute paired statistics")
                continue
            print_compare_block(
                baseline_path, run_path, measure, comp, holm_by_run[run_path], family_size,
                composites[baseline_path], composites[run_path],
            )
```

In `main()`, add the argument after `--baseline`:

```python
    ap.add_argument(
        "--pair", action="append", default=None, metavar="RUN=BASELINE",
        help=(
            "a run and the baseline it is paired with; repeatable. The Holm family is exactly the "
            "pairs given, so arms with different controls (A3 vs A0' at a different lambda) can share "
            "one correction (spec 7.2). Each pair must pass the pool checks (identical per-query "
            "document sets; ranked order differs on >= 25% of queries) or the report exits non-zero. "
            "Mutually exclusive with --baseline"
        ),
    )
```

and, before `run_paths = resolve_run_paths(...)`:

```python
    if args.pair and args.baseline:
        sys.exit("--pair and --baseline are mutually exclusive: --pair declares the family explicitly")
```

and at the end of `main()`:

```python
    if args.pair:
        run_pair_statistics(qrels, parse_pairs(args.pair), measures)
```

Also add one sentence to the module docstring's step 4: "With `--pair RUN=BASELINE` (repeatable) the family is exactly the declared pairs, each checked for pool invariance first; `--baseline` keeps the discover-everything behaviour."

- [ ] **Step 4: Run the suite.** `PYTHONPATH=... python3 -m pytest .../test_report.py -q` → `5 passed`.

- [ ] **Step 5: Run three mutations and record each failure.**
  1. `set_changed` check removed → `test_pool_check_rejects_a_changed_document_set` fails.
  2. `fraction < POOL_MIN_REORDERED_FRACTION` → `fraction <= ...` → `test_pool_check_rejects_too_few_reordered_queries` fails (the at-threshold case now exits).
  3. `holm_adjust` called per pair instead of over `valid` → `Holm (1 tests)`, `test_pair_family_is_exactly_the_declared_pairs` fails.
  Restore after each.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/report.py Iverson.Server/Iverson.LoadTest/scripts/test_report.py
git commit -m "add --pair to report.py so the Holm family is the declared pairs, with per-pair pool checks"
```

### Task 3: Carry the winning chunk's text through aggregation

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Benchmark/DocumentRanking.cs:15-32`
- Modify: `Iverson.Server/Iverson.LoadTest/Benchmark/MaxPassageAggregator.cs`
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/DocumentRankingTests.cs`, `MaxPassageAggregatorTests.cs`

**Interfaces:**
- Produces: `DocumentRanking.CollapseByDocId(IEnumerable<(string DocId, double Score, string Text)>, int)` and `MaxPassageAggregator.Aggregate(IEnumerable<(string ParentKey, double Score, string Text)>, keyMap, limit)` → `WinningChunkAggregation`, consumed by Task 5.

- [ ] **Step 1: Write the failing tests.** Append to `DocumentRankingTests.cs`:

```csharp
    [Fact]
    public void CollapseByDocId_WithText_KeepsTheTextOfTheMaximumChunk()
    {
        var scored = new[] { ("d1", 0.2, "low"), ("d1", 0.9, "winner"), ("d1", 0.5, "mid"), ("d2", 0.7, "only") };

        var result = DocumentRanking.CollapseByDocId(scored, limit: 10);

        result.Should().Equal(("d1", 0.9, "winner"), ("d2", 0.7, "only"));
    }

    [Fact]
    public void CollapseByDocId_WithText_TieKeepsTheFirstSeenChunk()
    {
        // Same rule as the 2-tuple overload (`score > existing`): on an exact tie the first chunk wins.
        var scored = new[] { ("d1", 0.5, "first"), ("d1", 0.5, "second") };

        DocumentRanking.CollapseByDocId(scored, limit: 10).Should().Equal(("d1", 0.5, "first"));
    }
```

and to `MaxPassageAggregatorTests.cs`:

```csharp
    [Fact]
    public void Aggregate_WithText_SurfacesEachDocumentsWinningChunk()
    {
        var keyMap = new Dictionary<string, string> { ["p1"] = "d1", ["p2"] = "d2" };
        var chunks = new[] { ("p1", 0.3, "p1 weak"), ("p2", 0.8, "p2 best"), ("p1", 0.6, "p1 best"), ("p3", 0.9, "orphan") };

        var result = MaxPassageAggregator.Aggregate(chunks, keyMap, limit: 10);

        result.Ranked.Should().Equal(("d2", 0.8, "p2 best"), ("d1", 0.6, "p1 best"));
        result.UnresolvedParentKeys.Should().Equal("p3");
    }
```

- [ ] **Step 2: Run `dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj`; expected: 3 compile errors (no 3-tuple overloads).**

- [ ] **Step 3: Implement.** `DocumentRanking.cs` becomes:

```csharp
public static class DocumentRanking
{
    public static IReadOnlyList<(string DocId, double Score)> CollapseByDocId(
        IEnumerable<(string DocId, double Score)> scored,
        int limit) =>
        CollapseByDocId(scored.Select(s => (s.DocId, s.Score, Text: "")), limit)
            .Select(r => (r.DocId, r.Score))
            .ToList();

    /// <summary>
    /// Same collapse, carrying a per-row payload — the chunk text — and keeping the payload of the
    /// row that supplied the maximum. Phase 1 of the reranker needs the winning chunk itself, not
    /// only its score (spec §3.3, A26); the 2-tuple overload above delegates here so there is one
    /// max-tracking rule (first-seen wins an exact tie).
    /// </summary>
    public static IReadOnlyList<(string DocId, double Score, string Text)> CollapseByDocId(
        IEnumerable<(string DocId, double Score, string Text)> scored,
        int limit)
    {
        var maxByDoc = new Dictionary<string, (double Score, string Text)>();

        foreach (var (docId, score, text) in scored)
        {
            if (!maxByDoc.TryGetValue(docId, out var existing) || score > existing.Score)
                maxByDoc[docId] = (score, text);
        }

        return maxByDoc
            .OrderByDescending(kv => kv.Value.Score)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value.Score, kv.Value.Text))
            .ToList();
    }
}
```

`MaxPassageAggregator.cs`: add after `ChunkAggregation`:

```csharp
/// <summary>
/// <see cref="ChunkAggregation"/> plus each document's winning (max-passage) chunk text — the
/// input the cross-encoder scores in Phase 1 (spec §3.3).
/// </summary>
public sealed record WinningChunkAggregation(
    IReadOnlyList<(string DocId, double Score, string Text)> Ranked,
    IReadOnlyList<string>                                    UnresolvedParentKeys);
```

and inside the class, a second `Aggregate` with the same resolve loop over 3-tuples:

```csharp
    public static WinningChunkAggregation Aggregate(
        IEnumerable<(string ParentKey, double Score, string Text)> chunks,
        IReadOnlyDictionary<string, string> keyMap,
        int limit)
    {
        var resolved   = new List<(string DocId, double Score, string Text)>();
        var unresolved = new List<string>();

        foreach (var (parentKey, score, text) in chunks)
        {
            if (keyMap.TryGetValue(parentKey, out var docId))
                resolved.Add((docId, score, text));
            else
                unresolved.Add(parentKey);
        }

        return new WinningChunkAggregation(DocumentRanking.CollapseByDocId(resolved, limit), unresolved);
    }
```

- [ ] **Step 4: Run the suite; expected `Passed: 37`** (34 + 3). The seven pre-existing `DocumentRankingTests` and six `MaxPassageAggregatorTests` must still pass unchanged — they are the control-path guarantee.

- [ ] **Step 5: Mutations, run and recorded:** (a) `score > existing.Score` → `>=` fails the tie test; (b) drop `.OrderByDescending` in the 3-tuple overload → `CollapseByDocId_OrdersByScoreDescending` (existing, via delegation) fails; (c) `maxByDoc[docId] = (score, text)` → `(score, existing.Text)` fails the winning-chunk tests.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/Benchmark/DocumentRanking.cs Iverson.Server/Iverson.LoadTest/Benchmark/MaxPassageAggregator.cs Iverson.Server/Iverson.LoadTest.Tests/Benchmark/DocumentRankingTests.cs Iverson.Server/Iverson.LoadTest.Tests/Benchmark/MaxPassageAggregatorTests.cs
git commit -m "carry each document's winning chunk text through max-passage aggregation"
```

### Task 4: `TeiRerankClient`

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Benchmark/TeiRerankClient.cs`
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/TeiRerankClientTests.cs`

**Interfaces:**
- Produces: `TeiRerankClient(HttpClient http)`, `Task<TeiInfo> GetInfoAsync(ct)`, `Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> texts, ct)`, `TeiRerankClient.BatchSize = 8`; consumed by Task 5.

- [ ] **Step 1: Write the failing tests.**

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class TeiRerankClientTests
{
    // Mirrors Iverson.Embeddings.Tests/EmbeddingServiceTests.cs:14-30. Records every request body so
    // batching can be asserted, and answers each /rerank batch from a caller-supplied function so the
    // response can deliberately come back in score-descending order, as TEI's does.
    private sealed class FakeHandler(Func<int, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return respond(Bodies.Count - 1, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TeiRerankClient Client(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://reranker.test/") });

    // TEI returns each batch sorted by score DESCENDING with `index` relative to the batch.
    private static string RerankResponseFor(string body)
    {
        var texts = JsonDocument.Parse(body).RootElement.GetProperty("texts").EnumerateArray()
            .Select(t => t.GetString()!).ToList();
        // Score = the number embedded in the text ("t7" -> 7), so expected scores are known.
        var scored = texts.Select((t, i) => (Index: i, Score: double.Parse(t[1..]))).OrderByDescending(s => s.Score);
        return "[" + string.Join(",", scored.Select(s => $"{{\"index\":{s.Index},\"score\":{s.Score}}}")) + "]";
    }

    [Fact]
    public async Task ScoreAsync_BatchesAtEight_AndMapsScoresBackByIndex()
    {
        var handler = new FakeHandler((_, body) => Json(RerankResponseFor(body)));
        var texts   = Enumerable.Range(0, 50).Select(i => $"t{(i * 7) % 50}").ToList();

        var scores = await Client(handler).ScoreAsync("q", texts, CancellationToken.None);

        handler.Bodies.Should().HaveCount(7);                                   // 8,8,8,8,8,8,2
        handler.Bodies.Take(6).Should().OnlyContain(b => JsonDocument.Parse(b).RootElement.GetProperty("texts").GetArrayLength() == 8);
        JsonDocument.Parse(handler.Bodies[6]).RootElement.GetProperty("texts").GetArrayLength().Should().Be(2);
        scores.Should().Equal(texts.Select(t => double.Parse(t[1..])));         // position i <-> texts[i]
        JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("query").GetString().Should().Be("q");
        JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("raw_scores").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ScoreAsync_NonSuccessStatus_Throws()
    {
        var handler = new FakeHandler((_, _) => Json("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError));

        var act = () => Client(handler).ScoreAsync("q", ["t1"], CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ScoreAsync_ResponseMissingAnIndex_Throws()
    {
        var handler = new FakeHandler((_, _) => Json("[{\"index\":0,\"score\":1.0}]"));

        var act = () => Client(handler).ScoreAsync("q", ["t1", "t2"], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2 texts*1 score*");
    }

    [Fact]
    public async Task GetInfoAsync_ReadsModelIdMaxInputLengthAndAutoTruncate()
    {
        var handler = new FakeHandler((_, _) => Json(
            "{\"model_id\":\"cross-encoder/ms-marco-MiniLM-L-6-v2\",\"max_input_length\":512,\"auto_truncate\":true,\"max_batch_requests\":8}"));

        var info = await Client(handler).GetInfoAsync(CancellationToken.None);

        info.Should().Be(new TeiInfo("cross-encoder/ms-marco-MiniLM-L-6-v2", 512, true));
    }
}
```

- [ ] **Step 2: Run; expected compile errors (no `TeiRerankClient`, `TeiInfo`).**

- [ ] **Step 3: Implement `TeiRerankClient.cs`.**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iverson.LoadTest.Benchmark;

/// <summary>What TEI's <c>/info</c> reports about the loaded model (spec A13, §4 startup guard).</summary>
public sealed record TeiInfo(string ModelId, int MaxInputLength, bool AutoTruncate);

/// <summary>
/// Text Embeddings Inference (TEI) <c>/rerank</c> over one <see cref="HttpClient"/> whose
/// <c>BaseAddress</c> is the reranker. Fail loud (spec §6): a non-success status, a timeout, or a
/// response that does not carry exactly one score per text throws — there is no
/// degrade-to-fused-order path, because a silent fallback writes a run file indistinguishable from a
/// reranked one.
/// </summary>
public sealed class TeiRerankClient(HttpClient http)
{
    // TEI reports max_batch_requests: 8 and max_client_batch_size: 32; 8 is what spec §9 measured.
    public const int BatchSize = 8;

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record RerankRequest(string Query, IReadOnlyList<string> Texts, bool RawScores);
    private sealed record RerankRow(int Index, double Score);
    private sealed record InfoResponse(
        [property: JsonPropertyName("model_id")]         string ModelId,
        [property: JsonPropertyName("max_input_length")] int    MaxInputLength,
        [property: JsonPropertyName("auto_truncate")]    bool   AutoTruncate);

    public async Task<TeiInfo> GetInfoAsync(CancellationToken ct)
    {
        var info = await http.GetFromJsonAsync<InfoResponse>("info", ct)
                   ?? throw new InvalidOperationException("TEI /info returned an empty body.");
        return new TeiInfo(info.ModelId, info.MaxInputLength, info.AutoTruncate);
    }

    /// <summary>One cross-encoder score per text, in the order of <paramref name="texts"/>.</summary>
    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> texts, CancellationToken ct)
    {
        var scores = new double[texts.Count];
        for (var offset = 0; offset < texts.Count; offset += BatchSize)
        {
            var batch = texts.Skip(offset).Take(BatchSize).ToList();
            using var response = await http.PostAsJsonAsync("rerank", new RerankRequest(query, batch, RawScores: false), SerializerOptions, ct);
            response.EnsureSuccessStatusCode();

            // TEI returns the batch sorted by score descending; `index` is the position within
            // the batch it was asked to score, so scores are mapped back by index, never by order.
            var rows = await response.Content.ReadFromJsonAsync<List<RerankRow>>(SerializerOptions, ct)
                       ?? throw new InvalidOperationException("TEI /rerank returned an empty body.");
            if (rows.Count != batch.Count || rows.Select(r => r.Index).Distinct().Count() != batch.Count
                || rows.Any(r => r.Index < 0 || r.Index >= batch.Count))
                throw new InvalidOperationException(
                    $"TEI /rerank scored a batch of {batch.Count} texts with {rows.Count} score(s) " +
                    $"(indices: {string.Join(",", rows.Select(r => r.Index))}).");
            foreach (var row in rows)
                scores[offset + row.Index] = row.Score;
        }
        return scores;
    }
}
```

- [ ] **Step 4: Run the suite; expected `Passed: 41`.**

- [ ] **Step 5: Mutations, run and recorded:** (a) `scores[offset + row.Index]` → `scores[offset + i]` (assign by response order) fails the batching test; (b) `BatchSize = 32` fails it (2 requests, not 7); (c) remove `EnsureSuccessStatusCode()` fails the non-success test; (d) remove the count check fails the missing-index test.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/Benchmark/TeiRerankClient.cs Iverson.Server/Iverson.LoadTest.Tests/Benchmark/TeiRerankClientTests.cs
git commit -m "add a fail-loud TEI rerank client for the benchmark harness"
```

### Task 5: Wire the rescore into `benchmark-query`

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs:257-262` (help), `:380-404` (`CommandFlags`)
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs:55-76` (flag checks), `:121-171` (sidecar), `:295-319` (`RunChunksAsync`)

**Interfaces:**
- Consumes: Task 3's 3-tuple `Aggregate` / `CollapseByDocId`; Task 4's `TeiRerankClient`, `TeiInfo`.
- Produces: `--rerank-url <url>` and `--rerank-model <id>` used by Task 7.

No unit test covers this task: `BenchmarkQueryScenario` is constructed over a live gRPC client and
Authentik identities and has no test seam today (`BenchmarkQueryScenario.cs:30-34`); its logic is in
Tasks 3–4, which are tested. The control-path guarantee is proven in Task 7 step 3 by a byte
comparison. Do not introduce a test seam for this task.

- [ ] **Step 1: Flags.** In `CommandFlags` add after `ConfigLabel`:

```csharp
    public string RerankUrl   { get; init; } = "";
    public string RerankModel { get; init; } = "";
```
and in `Parse`:
```csharp
        RerankUrl   = StrFlag(args, "--rerank-url",   ""),
        RerankModel = StrFlag(args, "--rerank-model", ""),
```
Help text, after the `--config-label` lines:
```
              --rerank-url <url>    benchmark-query only: rescore each query's 50 max-passage documents
                                     with a TEI cross-encoder at this base URL (e.g. http://127.0.0.1:8090)
                                     before writing the chunks run file. Omitted = today's control path.
              --rerank-model <id>   Refuse to start unless the reranker's /info model_id equals this
                                     (requires --rerank-url)
```

- [ ] **Step 2: Guard and sidecar.** In `RunAsync`, after the `ConfigLabel` check (`:76`):

```csharp
        if (!string.IsNullOrWhiteSpace(flags.RerankModel) && string.IsNullOrWhiteSpace(flags.RerankUrl))
        {
            Console.Error.WriteLine("benchmark-query: --rerank-model requires --rerank-url.");
            throw new InvalidOperationException("--rerank-model was given without --rerank-url.");
        }
```

Declare before the `/build` block:

```csharp
        // Reranking is opt-in per run (spec §3.3). The client lives for the whole sweep; every failure
        // it throws aborts the run (spec §6) -- a reranked arm whose reranker silently fell back would
        // score as a null result and read as "reranking doesn't help".
        TeiRerankClient? reranker = null;
        TeiInfo?         rerankerInfo = null;
        if (!string.IsNullOrWhiteSpace(flags.RerankUrl))
        {
            reranker = new TeiRerankClient(new HttpClient { BaseAddress = new Uri(flags.RerankUrl.TrimEnd('/') + "/") });
            rerankerInfo = await reranker.GetInfoAsync(ct);
            if (!string.IsNullOrWhiteSpace(flags.RerankModel) && rerankerInfo.ModelId != flags.RerankModel)
            {
                Console.Error.WriteLine(
                    $"REFUSING: reranker at {flags.RerankUrl} serves '{rerankerInfo.ModelId}', not " +
                    $"'{flags.RerankModel}' -- an arm attributed to the wrong model must not start.");
                throw new InvalidOperationException("reranker model_id does not match --rerank-model.");
            }
            Console.WriteLine(
                $"[benchmark-query] Reranking with {rerankerInfo.ModelId} at {flags.RerankUrl} " +
                $"(max_input_length={rerankerInfo.MaxInputLength}, auto_truncate={rerankerInfo.AutoTruncate}).");
        }
```

Inside the sidecar object (`:158-164`) add, after `recordedAtUtc`:

```csharp
                ["reranker"]      = rerankerInfo is null ? null : new JsonObject
                {
                    ["baseUrl"]        = flags.RerankUrl,
                    ["modelId"]        = rerankerInfo.ModelId,
                    ["maxInputLength"] = rerankerInfo.MaxInputLength,
                    ["autoTruncate"]   = rerankerInfo.AutoTruncate,
                },
```

- [ ] **Step 3: Rescore on the chunks path.** Change the `RunChunksAsync` call at `:204` to pass the client and query text: `await RunChunksAsync(query, headers, keyMap, reranker, ct)`. Rewrite `RunChunksAsync` (`:295-319`):

```csharp
    private async Task<(IReadOnlyList<(string DocId, double Score)> Ranked, int Failed, IReadOnlyList<string> Unresolved)> RunChunksAsync(
        CorpusQuery query, Metadata headers, IReadOnlyDictionary<string, string> keyMap,
        TeiRerankClient? reranker, CancellationToken ct)
    {
        var request = Query.Chunks<BenchmarkDocument>(d => d.Body)
            .Text(query.Text)
            .TopK((uint)(DocumentBudget * ChunkBudgetMultiplier))
            .Build();

        var chunks = new List<(string ParentKey, double Score, string Text)>();
        var failed = 0;
        try
        {
            using var call = search.SearchChunks(request, headers, cancellationToken: ct);
            await foreach (var r in call.ResponseStream.ReadAllAsync(ct))
                chunks.Add((r.ParentKey, r.Score, r.ChunkText));
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "SearchChunks failed for QueryId={QueryId}", query.QueryId);
            failed = 1;
        }

        if (reranker is null)
        {
            // Control path: byte-identical to the pre-reranker harness.
            var aggregated = MaxPassageAggregator.Aggregate(chunks.Select(c => (c.ParentKey, c.Score)), keyMap, DocumentBudget);
            return (aggregated.Ranked, failed, aggregated.UnresolvedParentKeys);
        }

        // Spec §3.3: aggregate to DocumentBudget documents FIRST (the unit reranked is the document,
        // scored through its winning chunk), rescore, then re-sort -- TrecRunWriter sorts nothing.
        var winners  = MaxPassageAggregator.Aggregate(chunks, keyMap, DocumentBudget);
        var scores   = await reranker.ScoreAsync(query.Text, winners.Ranked.Select(w => w.Text).ToList(), ct);
        var rescored = winners.Ranked.Select((w, i) => (w.DocId, scores[i]));
        return (DocumentRanking.CollapseByDocId(rescored, DocumentBudget), failed, winners.UnresolvedParentKeys);
    }
```

- [ ] **Step 4: Build and run the suite.** `dotnet build Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj` clean; `dotnet test ...LoadTest.Tests.csproj` → `Passed: 41`.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/Program.cs Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs
git commit -m "add --rerank-url to benchmark-query: rescore the 50 max-passage documents with a TEI cross-encoder"
```

### Task 6: `reranker` compose service

**Files:**
- Modify: `Iverson.Server/docker-compose.yml` (after the `ollama-init` service, `:150`; `iverson-api` environment, `:371`; `volumes:`, `:521`)

- [ ] **Step 1: Add the service** after `ollama-init`:

```yaml
  # Cross-encoder reranker (spec docs/specs/2026-09-03-reranker-design.md §5). Behind a profile so
  # nothing starts unless asked:   docker compose --profile reranker up -d reranker
  # Switch model per arm with RERANKER_MODEL_ID (the container serves ONE model). The model cache is
  # a named volume, never /tmp (a 4.9 GB tmpfs on the dev box, spec §7.6.3). --auto-truncate is the
  # stated head-truncation policy (§3.5); the harness records /info in each run's sidecar.
  reranker:
    image: ghcr.io/huggingface/text-embeddings-inference:cpu-1.8
    container_name: iverson-reranker
    profiles: ["reranker"]
    command: ["--model-id", "${RERANKER_MODEL_ID:-cross-encoder/ms-marco-MiniLM-L-6-v2}", "--auto-truncate"]
    ports:
      - "8090:80"
    volumes:
      - reranker_models:/data
```

- [ ] **Step 2: λ for the A0′/A3 arms.** In `iverson-api`'s `environment:`, after `Embeddings__BaseUrl`, add:

```yaml
      # MMR lambda; 0.70 is VectorRankingOptions' default. The reranker evaluation's A0'/A3 arms run
      # the API at 1.00 (spec §7.2):   VECTOR_RANKING_LAMBDA=1.00 docker compose up -d iverson-api
      - VectorRanking__Lambda=${VECTOR_RANKING_LAMBDA:-0.70}
```

- [ ] **Step 3: Volume.** Add `reranker_models:` to the top-level `volumes:` block.

- [ ] **Step 4: Validate.** From `Iverson.Server/`: `docker compose config --quiet` (no output = valid) and `docker compose config --services` must NOT list `reranker` (the profile hides it) while `docker compose --profile reranker config --services` must. `docker compose config | grep VectorRanking__Lambda` → `VectorRanking__Lambda: "0.70"`.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/docker-compose.yml
git commit -m "add a profile-gated TEI reranker service and a VectorRanking__Lambda knob to compose"
```

### Task 7: Run the arm family and record the gate verdict

**Files:**
- Create (outside this repo): `~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/runs/rerank-*.{chunks,similar}.trec`, `rerank-*.meta.json`, `GATE-2026-09.md`

This task is operational: it needs the compose stack, network access for the first model pull, and
several hours (§9's cold-box 10 min/arm for ms-marco and 78 min/arm for bge-reranker-base are
4–5× optimistic per §11). One arm at a time; nothing else on the box (§7.6.1).

- [ ] **Step 1: Restore the baseline.** `docker compose up -d` from `Iverson.Server/`; check `test -f /home/ben/iverson-benchmark-data/bench-env.sh`; restore per `scifact-512-qdrant-snapshots/RESTORE.md`; verify 5,183 object points and 19,967 chunk points. `export RUN=~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26`.

- [ ] **Step 2: A0 (control, λ = 0.70, no reranker), twice — once with the pre-plan harness.** The
server build is newer than every preserved run, so the only valid identity check is old harness vs
new harness against the SAME server. Check out the plan's base commit into a worktree and run the
control from there first:
```bash
source /home/ben/iverson-benchmark-data/bench-env.sh
git worktree add .worktrees/reranker-control 39610d4
(cd .worktrees/reranker-control/Iverson.Server/Iverson.LoadTest && dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label rerank-a0-preplan)
cd Iverson.Server/Iverson.LoadTest
dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label rerank-a0
git worktree remove .worktrees/reranker-control
```

- [ ] **Step 3: Prove the control path is byte-identical.** `diff <(cut -d' ' -f1-5 $RUN/runs/rerank-a0.chunks.trec) <(cut -d' ' -f1-5 $RUN/runs/rerank-a0-preplan.chunks.trec)` must be empty, and the same for the two `.similar.trec` files (columns 1–5; column 6 is the run tag). If either differs, stop: the harness changed the control, and no reranked arm may be scored against it.

- [ ] **Step 4: A1 (ms-marco, λ = 0.70).**
```bash
(cd ../ && docker compose --profile reranker up -d reranker)   # first start pulls the model
until curl -sf http://127.0.0.1:8090/info >/dev/null; do sleep 5; done
dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label rerank-a1 --rerank-url http://127.0.0.1:8090 --rerank-model cross-encoder/ms-marco-MiniLM-L-6-v2
```

- [ ] **Step 5: A2 (bge-reranker-base, λ = 0.70).**
```bash
(cd ../ && RERANKER_MODEL_ID=BAAI/bge-reranker-base docker compose --profile reranker up -d --force-recreate reranker)
until curl -sf http://127.0.0.1:8090/info | grep -q bge-reranker-base; do sleep 5; done
dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label rerank-a2 --rerank-url http://127.0.0.1:8090 --rerank-model BAAI/bge-reranker-base
```

- [ ] **Step 6: A0′ (control at λ = 1.00, no reranker).**
```bash
(cd ../ && VECTOR_RANKING_LAMBDA=1.00 docker compose up -d iverson-api)
dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label rerank-a0prime
```

- [ ] **Step 7: A3 (ms-marco, λ = 1.00).** Recreate the reranker with the default model (step 4's command, `--force-recreate`), wait for `/info` to report ms-marco, then:
```bash
dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label rerank-a3 --rerank-url http://127.0.0.1:8090 --rerank-model cross-encoder/ms-marco-MiniLM-L-6-v2
``` Afterwards restore λ: `docker compose up -d iverson-api` (env unset → 0.70) and `docker compose --profile reranker stop reranker`.

- [ ] **Step 8: Score the declared family in ONE invocation.**
```bash
cd $RUN/runs
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 \
  /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts/report.py \
  --qrels $RUN/qrels.trec \
  --run rerank-a0.chunks.trec --run rerank-a1.chunks.trec --run rerank-a2.chunks.trec \
  --run rerank-a0prime.chunks.trec --run rerank-a3.chunks.trec \
  --pair rerank-a1.chunks.trec=rerank-a0.chunks.trec \
  --pair rerank-a2.chunks.trec=rerank-a0.chunks.trec \
  --pair rerank-a3.chunks.trec=rerank-a0prime.chunks.trec | tee report-rerank-2026-09.txt
```
The `[pool]` lines must show `set changed on 0` for all three pairs. Any `ARM INVALID` exit stops the
gate: investigate, do not re-declare.

- [ ] **Step 9: Record the verdict** in `$RUN/runs/GATE-2026-09.md`: for A1 vs A0 on nDCG@10, the delta, paired t, permutation p, Holm p_adj at m = 3, 95 % CI, d_z, MDE, queries changed; the same rows for A2 and A3; R@50 per arm (must match its control to 4 decimals); whether nDCG@10 exceeds the oracle ceiling 0.9216 (must not, §7.1.3 / A36); and one line: **GATE PASSED / GATE FAILED** per §3.4 (A1 vs A0 Holm p_adj < 0.05 with a positive delta). No Phase 2 plan is written on a failed gate.

- [ ] **Step 10: Commit nothing in this repo** for step 9 (the corpora directory is a separate repository); commit `GATE-2026-09.md` and `report-rerank-2026-09.txt` there with `git -C ~/repositories/iverson-benchmark-corpora add ...`.

## Tasks NOT in this plan

Inherited from spec §2 (prose form preserved):

- **`SearchSimilar`.** Its candidates are whole field texts. Measured on the real SciFact workload,
  **7 of 50 candidates exceed the cross-encoder's 512-token window** (mean ~395 tokens, max ~696), so
  reranking it requires a truncation policy (head / tail / best-window) that is its own design
  decision with its own quality consequences. Chunks fit whole **at the 128-token chunking used
  for measurement**; at `main`'s default `[IversonChunk]` (`maxTokens = 512`, a 2,048-character
  window) a SciFact chunk averages ~308 tokens and **16.8 % exceed 512 tokens once the query is
  prepended**, so Phase 2 carries a truncation policy regardless — stated in §3.5, not deferred.
- **Migrating embeddings from Ollama to TEI.** Measured at 1.57× faster with a published quality
  upgrade at unchanged dimensions, and genuinely attractive — but changing the embedding model would
  move the baseline this reranker is measured against — the rule established by the embedding-prefix
  work, which recorded explicitly: do not fix the encoder and run the sweep at the same time. It
  would also invalidate every number in §1, all of which were computed from nomic-based run files.
  Reranking is query-time only and needs no re-ingest, so bundling buys no efficiency. Separate spec.
- **Re-tuning `WCentroid` / `w`.** Required eventually (see §7.4) but a separate arm family measured
  against R@K, run only after a reranker exists.

Plus, by the plan-scope ruling: **Phase 2** — spec §3.5 (server-side max-passage selection,
`CrossEncoderScorer`, the `SearchChunks` contract change, `fetchLimit = pool × OverFetchFactor`, the
diversity-vector guard re-key), §4 (component contract, startup guard), §5 (`RerankerOptions`, Helm
subchart), §6 (server fail-loud), and §7.6 throughput re-measurement. A new plan is written after
Task 7's verdict.

## Known issues inherited from spec

- **The reranking latencies in §9 are cold-box** and must be re-measured under §7.6 before arm
  durations are planned against them.
- **`gte-base-en-v1.5` cannot be served by TEI** at 1.8.3 or 1.9.3. Irrelevant to this spec (it is an
  embedding model) but it constrains the deferred migration, since it is the strongest model in the
  published quality table.
- **The `chunked-512` baseline is from an unmerged experiment branch**
  (`chunk-size-512-experiment`). Phase 1 measures against that configuration; results do not
  automatically transfer to main's 2048-char default.
- **`SearchSimilar` reranking is unaddressed**, pending a truncation policy (§2).

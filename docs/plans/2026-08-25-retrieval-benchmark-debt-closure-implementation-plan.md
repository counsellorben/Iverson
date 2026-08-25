# Retrieval-Benchmark Debt Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-25-retrieval-benchmark-debt-closure-design.md` (commit SHA: `3fd990e`)

**Goal:** Close the four open retrieval-benchmark debts — normalise FreshStack through a converter so λ stays measurable, remove the dead C# paths, and correct the prior spec — without running the sweep.

**Architecture:** A Python converter normalises FreshStack's HuggingFace parquet into the BEIR-shaped JSONL the harness already parses, deriving TREC qrels from the nested `nuggets[]` at export. Nothing FreshStack-specific then survives in C#, so the guessed parser is deleted rather than rewritten, both `ParseQrels` implementations die, and the surviving parser is renamed to match what it now reads.

**Tech stack:** .NET 10 (`net10.0`), xunit 2.9.3, FluentAssertions 7.0.0; Python 3 with HuggingFace `datasets`.

---

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/scripts/freshstack_to_jsonl.py` — normalises one FreshStack topic to `corpus.jsonl` / `queries.jsonl` / `qrels.tsv`

**Modify**
- `Iverson.Server/Iverson.LoadTest/Corpus/BeirCorpusParser.cs` → renamed to `JsonlCorpusParser.cs`; loses `ParseQrels`, gains the empty-query-text guard
- `Iverson.Server/Iverson.LoadTest/Corpus/CorpusModels.cs` — loses the `Qrel` record
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs` — FreshStack arm repointed; doc comments updated
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` — FreshStack arm repointed
- `Iverson.Server/Iverson.LoadTest/Program.cs` — help text gains a converter pointer
- `Iverson.slnx` — add the missing `Iverson.LoadTest.Tests` project line
- `docs/specs/2026-07-31-retrieval-quality-benchmark-design.md` — the four corrections

**Delete**
- `Iverson.Server/Iverson.LoadTest/Corpus/FreshStackCorpusParser.cs`

**Test**
- `Iverson.Server/Iverson.LoadTest.Tests/Corpus/BeirCorpusParserTests.cs` → renamed to `JsonlCorpusParserTests.cs`; loses 1 qrels test, gains 2 guard tests

---

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here. Trusted as ground truth — see the spec's `Verified assumptions` table for each item's evidence:

- **V1–V3** — FreshStack corpus/query/nugget schemas and per-topic sizes (godot 25,482 docs / 99 queries is the smallest viable topic).
- **V4** — corpus-id join is NOT verifiable from docs; absorbed as the converter's runtime assertion.
- **V5** — no separate qrels dataset ships.
- **V6** — query text composition is NOT documented upstream; decided explicitly in spec §1.1.
- **V7** — `datasets` selects a config via the `name` argument (second positional), not `subset=`.
- **V8** — `ir_measures` reads subtopic ids from the qrels iteration field.
- **V9–V15** — C# blast radii, test count (15), and the root-`Iverson.slnx` omission.
- **V16, V19** — prior-spec structure: line 92, A1/A10/A21 at 161/170/181, Known issues 184, §7 at 135, §5 at 120–126.
- **V17** — the repo's stdlib-only Python convention and why this tool departs from it.
- **V18** — registration validates `OwnerField` regardless of roles.

---

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | All 9 files the plan modifies/deletes exist at the cited paths | `test -f` on each returned OK: both parsers, `BeirCorpusParserTests.cs`, `CorpusModels.cs`, `Iverson.slnx`, the 2026-07-31 spec, both scenarios, `Program.cs` |
| 2 | File path | `Iverson.Server/Iverson.LoadTest/scripts/` does not exist; Task 2 creates it | `ls` returned absent |
| 3 | Signature | `ParseCorpus(TextReader)` / `ParseQueries(TextReader)` — callsites pass `StreamReader`, which derives from `TextReader` | `BeirCorpusParser.cs:13` `public static List<CorpusDocument> ParseCorpus(TextReader reader)`; `:55` same shape for `ParseQueries` |
| 4 | Signature | `CorpusDocument(string DocId, string Title, string Text)`, `CorpusQuery(string QueryId, string Text)` | `CorpusModels.cs:3-4` |
| 5 | Code-in-plan | The guard pattern being mirrored throws `FormatException` with the line number interpolated | `BeirCorpusParser.cs:32` and `:46` — `throw new FormatException($"BEIR corpus line {lineNumber} ...")` |
| 6 | Code-in-plan | Test conventions: xunit `[Fact]`, FluentAssertions `act.Should().Throw<FormatException>().WithMessage("*line N*")`, C# raw string literals for fixtures | `BeirCorpusParserTests.cs:1-3` (usings), `:44-46`, `:54-56` |
| 7 | Command | `dotnet build` / `dotnet test` with explicit `.csproj` paths — the repo has no Makefile, justfile, or test script | `ls Makefile justfile` → absent; `scripts/` holds only `reap-testcontainers.sh`; no `dotnet test` in `.github/workflows/` |
| 8 | Command | Test project targets `net10.0` with xunit 2.9.3 / FluentAssertions 7.0.0 | `Iverson.LoadTest.Tests.csproj` |
| 9 | Command | Commit convention: `fix(loadtest):` for LoadTest fixes, `spec:` for spec edits, plain imperative sentences otherwise — all three already in use; no new convention introduced | `git log`: `6ea1e86 fix(loadtest): reject corpus documents with empty body text` (directly analogous), `5b0553c spec: ...`, `028579b stop the load test assigning entity keys it cannot own` |
| 10 | Command | Standalone Python scripts are invoked `python3 <path> --flags` | `mint_acting_user_token.py:12` documents exactly that form |
| 11 | **Code-in-plan (limited)** | `datasets` is **NOT installed on this machine** — `import datasets` raises `ModuleNotFoundError`. Task 2 therefore includes an install step, and the `config_name` assertion below is verified **from documentation, not by execution** | `python3 -c "import datasets"` → traceback |
| 12 | Code-in-plan | `get_dataset_config_names(repo)` is a documented public function, and `DatasetInfo.config_name` carries the loaded config's name | HF docs: "use `get_dataset_config_names()` to retrieve a list of all the possible configurations"; `dataset.info` returns `DatasetInfo`, whose `config_name` "represents the name of the dataset configuration" |
| 13 | Ordering | Task 2's only reference to a Task 1 symbol is its end-to-end verification step (`JsonlCorpusParser`); Task 3 depends on neither | Task 2's script imports nothing from the C# project; Task 3 edits only `docs/specs/2026-07-31-...md` |
| 14 | Consumer impact | Renaming `BeirCorpusParser` breaks nothing outside the 3 known C# sites — no `.csproj`, `.slnx`, `.md`, or `.json` reference exists, and `Iverson.LoadTest.csproj` is SDK-style with implicit globbing (no `Compile Include` to update) | `grep` across `*.csproj *.slnx *.md *.json` returned no hits outside our own spec/review docs; `Iverson.LoadTest.csproj` has no `<Compile>` items |
| 15 | Consumer impact | `Qrel` has no non-`.cs` consumer either; deleting it needs no project-file change | same grep as #14 |
| 16 | Consumer impact | Adding `Iverson.LoadTest.Tests` to root `Iverson.slnx` cannot double-add | `grep -c "LoadTest.Tests" Iverson.slnx` → `0` |
| 17 | Sibling sweep | **Complete** corpus-arm enumeration — the spec's §2 prose list was incomplete (it cited `BenchmarkIngestScenario.cs:19-20` for XML docs; the real mentions are `:14`, `:20`, `:21`). Task 1 carries the grep-verified list | `grep -rn -io "beir\|freshstack" --include=*.cs` over `Iverson.LoadTest/` and `Iverson.LoadTest.Tests/` |
| 18 | Sibling sweep | Every identifier the plan names resolves at its point of use: `FreshStackCorpusParser`, `BeirCorpusParser`, `ParseCorpus`, `ParseQueries`, `ParseQrels`, `Qrel`, `CorpusDocument`, `CorpusQuery`, `FormatException`, `TrecRunWriter`, `KeyMap`, `BenchmarkIngestScenario`, `BenchmarkQueryScenario`, `Iverson.LoadTest.Tests`, `Iverson.slnx` | Each read or grep'd this round; all resolve |
| 19 | Consumer impact | `BeirCorpusParser.cs` hardcodes "BEIR" in its class doc and three exception messages, which the rename must also carry — otherwise a FreshStack parse failure reports "BEIR corpus line N" | `:6-7` class doc "Parses BEIR-format corpus files"; messages at `:32`, `:46`, `:75` (`:110`/`:117` vanish with `ParseQrels`) |

---

## Tasks

### Task 1: C# normalisation

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Corpus/BeirCorpusParser.cs` (→ `JsonlCorpusParser.cs`)
- Modify: `Iverson.Server/Iverson.LoadTest/Corpus/CorpusModels.cs:6`
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs:233,251-252`
- Modify: `Iverson.slnx`
- Delete: `Iverson.Server/Iverson.LoadTest/Corpus/FreshStackCorpusParser.cs`
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Corpus/BeirCorpusParserTests.cs` (→ `JsonlCorpusParserTests.cs`)

**Interfaces:**
- Produces: `JsonlCorpusParser` — Task 2's end-to-end verification step parses the converter's output with it.

- [ ] **Step 1: Rename the parser and its test class, and complete the rename in prose**
```bash
git mv Iverson.Server/Iverson.LoadTest/Corpus/BeirCorpusParser.cs \
       Iverson.Server/Iverson.LoadTest/Corpus/JsonlCorpusParser.cs
git mv Iverson.Server/Iverson.LoadTest.Tests/Corpus/BeirCorpusParserTests.cs \
       Iverson.Server/Iverson.LoadTest.Tests/Corpus/JsonlCorpusParserTests.cs
```
Rename `public static class BeirCorpusParser` → `JsonlCorpusParser` and `public class BeirCorpusParserTests` → `JsonlCorpusParserTests`, updating the 3 callsites (`BenchmarkIngestScenario.cs:81`, `BenchmarkQueryScenario.cs:200`, and every reference in the test file).

Then complete the rename where it is not just the identifier (assumption #19) — the class doc at `:6-7` currently reads "Parses BEIR-format corpus files", and the two surviving exception messages at `:32` and `:46` interpolate `"BEIR corpus line {lineNumber}"` / `"BEIR queries line {lineNumber}"`. Restate all three so they name the *format* rather than the corpus: this parser now reads FreshStack-derived files too, and a FreshStack parse failure that reports "BEIR corpus line 12" misdirects exactly the debugging the guard exists for. The existing tests assert with the wildcard `WithMessage("*line 2*")`, so the wording change does not break them.

- [ ] **Step 2: Delete the qrels path**
Delete `ParseQrels` from `JsonlCorpusParser.cs` (`:86-124` — the method's full extent in a 125-line file, taking the `"BEIR qrels"` strings at `:110`/`:117` with it), delete the `Qrel` record and its `<param>` doc comment at `CorpusModels.cs:5-6`, and delete the `ParseQrels_HeaderIsSkipped_AndSubtopicIsZero` test at `JsonlCorpusParserTests.cs:96-112` (its `[Fact]` line through its closing brace, in a 113-line file).

Qrels are produced by the converter (Task 2) and consumed by the external scorer; the harness never holds them.

- [ ] **Step 3: Delete `FreshStackCorpusParser` and repoint its callsites**
```bash
git rm Iverson.Server/Iverson.LoadTest/Corpus/FreshStackCorpusParser.cs
```
Point `BenchmarkIngestScenario.cs:90` and `BenchmarkQueryScenario.cs:207` at `JsonlCorpusParser.ParseCorpus` / `.ParseQueries`. Both already pass a `StreamReader` and the signatures are identical (assumption #3), so the change is the type name only.

- [ ] **Step 4: Write the two failing guard tests**
Add to `JsonlCorpusParserTests.cs`, matching the file's existing conventions:
```csharp
[Fact]
public void ParseQueries_MissingText_ThrowsRatherThanYieldingAnEmptyQuery()
{
    var input = """{"_id": "q1"}""";

    var act = () => JsonlCorpusParser.ParseQueries(new StringReader(input));

    act.Should().Throw<FormatException>().WithMessage("*line 1*");
}

[Fact]
public void ParseQueries_EmptyText_ThrowsRatherThanYieldingAnEmptyQuery()
{
    var input = """{"_id": "q1", "text": "   "}""";

    var act = () => JsonlCorpusParser.ParseQueries(new StringReader(input));

    act.Should().Throw<FormatException>().WithMessage("*line 1*");
}
```
Run them and confirm they FAIL before writing the guard:
```bash
dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj --filter "FullyQualifiedName~ParseQueries"
```

- [ ] **Step 5: Add the guard**
In `JsonlCorpusParser.ParseQueries`, after the `_id` check and in place of the bare `?? ""` fallback:
```csharp
var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";

// Mirrors the ParseCorpus guard above, for the same reason on the query side: an empty query
// string is embedded and searched without error, returns a meaningless ranking, and produces a
// run file that scores zero relevance with success reported at every checkpoint.
if (string.IsNullOrWhiteSpace(text))
{
    throw new FormatException(
        $"Queries line {lineNumber} (_id \"{id}\"): missing or empty \"text\". A query with no " +
        "text is embedded as an empty vector and silently scores nothing.");
}
```
Re-run the filtered test; both should now pass.

- [ ] **Step 6: Update every member of the corpus-arm set**
Grep-verified complete list (assumption #17) — the spec's prose list was incomplete:
- `BenchmarkIngestScenario.cs` — XML doc at `:14`, `:20`, `:21`; path resolution `:52-55`; the guard and message at `:57`, `:60-61`; the ordering comment at `:74`, `:76`; both arms at `:77-90`
- `BenchmarkQueryScenario.cs` — message at `:74-76`; path resolution and both arms at `:196-207`
- `Program.cs` — `:233`, `:251-252`; add a pointer to `scripts/freshstack_to_jsonl.py` for producing the `freshstack/` directory

The `beir/` and `freshstack/` directory layout does not change — only the parser type name and the prose that named a corpus-specific parser.

- [ ] **Step 7: Add the missing test project to the root solution**
In `Iverson.slnx`, inside the `/Iverson.Server/` folder, add alongside the existing `Iverson.LoadTest` entry at line 23:
```xml
<Project Path="Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj" />
```
Without this the 15 existing tests — and the 2 added in Step 4 — are invisible to any root-solution build.

- [ ] **Step 8: Build and run the full suite**
```bash
dotnet build Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj
dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj
```
Expect **16 tests passing** (15 − 1 deleted qrels test + 2 new guard tests). Report the actual number; do not assume it.

- [ ] **Step 9: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/Corpus Iverson.Server/Iverson.LoadTest.Tests/Corpus \
        Iverson.Server/Iverson.LoadTest/Scenarios Iverson.Server/Iverson.LoadTest/Program.cs \
        Iverson.slnx
git commit -m "normalise the corpus parser, retire the qrels path, and guard empty query text"
```

---

### Task 2: The FreshStack → JSONL converter

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/freshstack_to_jsonl.py`

**Interfaces:**
- Consumes: `JsonlCorpusParser` (Task 1) — only in Step 3's end-to-end check.

- [ ] **Step 1: Write the converter**
Create `Iverson.Server/Iverson.LoadTest/scripts/freshstack_to_jsonl.py`. Load-bearing content:

```python
#!/usr/bin/env python3
"""Normalise one FreshStack topic into the BEIR-shaped JSONL the benchmark harness parses.

FreshStack ships as HuggingFace parquet and ships NO qrels file — its judgments are nested
inside the query rows. This script flattens both into <out-dir>/freshstack/:

    corpus.jsonl   {"_id", "text"}
    queries.jsonl  {"_id", "text"}
    qrels.tsv      query_id \t nugget_id \t corpus_id \t relevance

Unlike deploy/scripts/mint_acting_user_token.py this is NOT stdlib-only: it needs the
HuggingFace datasets library. That script must run on any machine; this one is a dev tool
run deliberately before a sweep. Install with:

    pip install datasets

    python3 Iverson.Server/Iverson.LoadTest/scripts/freshstack_to_jsonl.py \
        --topic godot --out-dir /path/to/corpora
"""

import argparse
import json
import os
import sys

from datasets import get_dataset_config_names, load_dataset

CORPUS_REPO = "freshstack/corpus-oct-2024"
QUERIES_REPO = "freshstack/queries-oct-2024"
TOPICS = ["angular", "godot", "langchain", "laravel", "yolo"]


def load_config(repo, topic):
    """Load one topic. The config is the SECOND POSITIONAL argument to load_dataset --
    there is no `subset=` parameter (FreshStack's own README says otherwise, and an unknown
    keyword would fall through to **config_kwargs without selecting the topic), so the
    loaded config is asserted against the request rather than trusted."""
    available = get_dataset_config_names(repo)
    if topic not in available:
        sys.exit(f"{repo}: topic '{topic}' not among configs {sorted(available)}")

    ds = load_dataset(repo, topic, split="test")

    loaded = getattr(ds.info, "config_name", None)
    if loaded != topic:
        sys.exit(f"{repo}: asked for config '{topic}' but loaded '{loaded}'")
    return ds


def check_no_whitespace(kind, values):
    """Every id emitted into a whitespace-delimited file must be whitespace-free.

    qrels.tsv carries three id columns and TREC qrels readers split on whitespace rather
    than strictly on tabs; TrecRunWriter emits the run file space-separated. One id with a
    space shifts every later column in its row, and the reader takes the tail of that id as
    the next field -- wrong scores, no error anywhere."""
    bad = sorted({v for v in values if any(c.isspace() for c in v)})
    if bad:
        sys.exit(
            f"{len(bad)} {kind} value(s) contain whitespace, which would shift columns in "
            f"qrels.tsv and the TREC run file. First few: {bad[:5]}"
        )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--topic", required=True, choices=TOPICS)
    ap.add_argument("--out-dir", required=True)
    args = ap.parse_args()

    out = os.path.join(args.out_dir, "freshstack")
    os.makedirs(out, exist_ok=True)

    corpus = load_config(CORPUS_REPO, args.topic)
    queries = load_config(QUERIES_REPO, args.topic)

    # corpus.jsonl -- `metadata` dropped; `title` omitted entirely rather than emitted
    # empty, since FreshStack has no such field and the parser defaults a missing one to "".
    corpus_ids = set()
    with open(os.path.join(out, "corpus.jsonl"), "w", encoding="utf-8") as f:
        for row in corpus:
            corpus_ids.add(row["_id"])
            f.write(json.dumps({"_id": row["_id"], "text": row["text"]}) + "\n")
    check_no_whitespace("corpus _id", corpus_ids)

    # queries.jsonl -- title + body. This composition is a DECISION, not a verified fact:
    # FreshStack does not document how it composes retrieval query text (spec section 1.1).
    query_ids = set()
    with open(os.path.join(out, "queries.jsonl"), "w", encoding="utf-8") as f:
        for row in queries:
            query_ids.add(row["query_id"])
            text = f"{row['query_title']}\n\n{row['query_text']}"
            f.write(json.dumps({"_id": row["query_id"], "text": text}) + "\n")
    check_no_whitespace("query_id", query_ids)

    # qrels.tsv -- the nugget id lands in column 2, the TREC iteration field, which is what
    # makes alpha-nDCG computable: ir_measures reads subtopic ids from exactly that column.
    nugget_ids = set()
    rows, missing_relevant, dropped_non_relevant = [], [], 0
    for row in queries:
        for nugget in row["nuggets"]:
            nugget_ids.add(nugget["_id"])
            for cid in nugget["relevant_corpus_ids"]:
                # Hard-fail: nuggets are generated FROM the corpus, so every relevant id
                # should resolve by construction. If one does not, the join encoded here is
                # wrong and the right response is to stop.
                if cid not in corpus_ids:
                    missing_relevant.append(cid)
                rows.append((row["query_id"], nugget["_id"], cid, 1))
            for cid in nugget["non_relevant_corpus_ids"]:
                # Drop and report: these come from a retrieval judgment pool, not from
                # nugget generation, so the by-construction argument does not transfer.
                if cid not in corpus_ids:
                    dropped_non_relevant += 1
                    continue
                rows.append((row["query_id"], nugget["_id"], cid, 0))

    check_no_whitespace("nugget _id", nugget_ids)

    if missing_relevant:
        sys.exit(
            f"{len(missing_relevant)} relevant_corpus_id(s) are absent from the '{args.topic}' "
            f"corpus. First few: {sorted(set(missing_relevant))[:5]}"
        )

    with open(os.path.join(out, "qrels.tsv"), "w", encoding="utf-8") as f:
        for qid, nid, cid, rel in rows:
            f.write(f"{qid}\t{nid}\t{cid}\t{rel}\n")

    print(f"corpus.jsonl  {len(corpus_ids):,} documents")
    print(f"queries.jsonl {len(query_ids):,} queries")
    print(f"qrels.tsv     {len(rows):,} judgments ({len(nugget_ids):,} nuggets)")
    if dropped_non_relevant:
        print(f"dropped {dropped_non_relevant:,} unresolvable non-relevant judgment(s)")


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Install the dependency**
```bash
pip install datasets
```
`datasets` is not currently installed (assumption #11), so this is required before Step 3 and cannot be skipped.

- [ ] **Step 3: Run against the smallest topic and verify the output end-to-end**
```bash
python3 Iverson.Server/Iverson.LoadTest/scripts/freshstack_to_jsonl.py \
    --topic godot --out-dir /tmp/freshstack-check
```
**This step needs network access and downloads a real dataset** — godot is the smallest topic at 25,482 documents and 99 queries. It parses the corpus; it does **not** ingest it into Iverson, so it costs a download, not an embedding run.

Confirm: all three files are written; the printed document and query counts match V1/V2 (25,482 and 99); the run exits 0, meaning the config-name, whitespace, and relevant-join assertions all passed. Note the dropped-non-relevant count if any — that is expected behaviour, not a failure.

Then confirm the renamed parser actually reads what the converter wrote:
```bash
head -2 /tmp/freshstack-check/freshstack/corpus.jsonl
head -2 /tmp/freshstack-check/freshstack/qrels.tsv
```
The corpus lines must carry `_id` and `text` and no `title`; the qrels lines must have exactly four tab-separated columns.

If the relevant-join assertion fires, **stop and report** rather than relaxing it — it means V4's unverifiable assumption is false and the spec's understanding of the join is wrong.

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/freshstack_to_jsonl.py
git commit -m "add the FreshStack to JSONL converter, deriving TREC qrels from nuggets"
```

---

### Task 3: Corrections to the 2026-07-31 spec

**Files:**
- Modify: `docs/specs/2026-07-31-retrieval-quality-benchmark-design.md`

- [ ] **Step 1: Correct the false §3 sentence and scope A21**
At line 92, replace "That identity's bypass role sets `ownershipRequired` false, so no `OwnerId` property is needed" with the rule that actually governs: registration validates `OwnerField` against declared scalars regardless of roles (`SchemaRegistrationOrchestrator.cs:82-84`, via `ValidateFieldReference`), so `BenchmarkDocument` requires `OwnerId` — which the shipped code has.

Add an assumptions-table row scoping **A21 to evaluation only**. Conflating evaluation with registration is what generated the original Critical, so the scoping is the part that stops it recurring.

- [ ] **Step 2: Correct A1 with the verified schema**
At line 161, replace A1 with the measured facts: five topics at angular 117,288 / laravel 52,351 / langchain 49,514 / yolo 27,207 / godot 25,482 documents, and 129 / 184 / 203 / 57 / 99 queries. Nuggets are **1–7** per question, not "3–4". Record that FreshStack ships **no qrels file** — judgments are nested in the query rows — and that the corpus carries **no title field**.

- [ ] **Step 3: Record the scoring decision in §5**
In §5 `Scoring is external` (lines 120–126, whose lines 123–125 carry the statement being amended), record that scoring targets `ir_measures` with a converter-derived TREC qrels file, rather than FreshStack's own evaluation package — which takes three objects (`qrels_nuggets`, `qrels_query`, `query_to_nuggets`) the harness does not produce. The prior spec called its own package "lower-risk"; this makes the choice a recorded decision rather than a silent default.

- [ ] **Step 4: Record the sweep's feasibility evidence under A10**
Under the existing A10 entry (line 170) and "Known issues" (line 184), record what is now known:
- The 2026-08-24 live run ingested 400 documents and drove a 4-core box to load ~20, hard enough that Kafka's `FIND_COORDINATOR` lookup began timing out — reproduced at the broker, not a client artifact (`8db2bf4`, `docs/runbooks/integration-test-flake-signatures.md`).
- The smallest viable FreshStack topic is godot at 25,482 documents with 99 queries — roughly 64× the volume that produced that load, and one topic alone matches the ~100-question statistical power the prior spec assumed from two.

Do **not** add a runbook: §7 (line 135) already documents the scratch-branch sweep procedure, and duplicating it would drift.

- [ ] **Step 5: Commit**
```bash
git add -f docs/specs/2026-07-31-retrieval-quality-benchmark-design.md
git commit -m "spec: correct the owner-field claim and the FreshStack schema, and record A10's evidence"
```
`docs/specs` is gitignored in this repo, so `-f` is required — the existing specs were added the same way.

---

## Tasks NOT in this plan

Inherited verbatim from the spec's "Not in this spec". A new spec → plan cycle is required to add any of these.

- Running the sweep, editing any constant, or producing any measurement.
- Whether other projects are missing from `Iverson.slnx` beyond `Iverson.LoadTest.Tests`.
- BEIR corpus acquisition, which is unchanged and already documented.

## Known issues inherited from spec

These exist in the implementation by design — accepted by the user during brainstorming.

- **λ remains unmeasured.** This design makes λ *measurable*; it does not measure it. Ben deferred the run (2026-08-25).
- **A10 is still open, and now quantified rather than unknown.** Whether 25K documents ingest in tolerable time on the available hardware is unresolved, and the evidence available is not encouraging.
- **Query composition is a decision, not a verified fact.** Numbers produced under it are internally comparable across the sweep's eight configurations, which is what the ablation needs, but are not directly comparable to FreshStack's published leaderboard.
- **The nugget → iteration-field convention moves to Python** and is no longer covered by the C# test suite. Accepted: the failure that matters is upstream schema drift, which the C# suite could never have caught.

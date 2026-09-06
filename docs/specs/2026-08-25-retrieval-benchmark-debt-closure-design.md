# Retrieval-Benchmark Debt Closure — FreshStack Normalisation and Three Open Items

## Context

The retrieval-quality benchmark harness ([2026-07-31 design](2026-07-31-retrieval-quality-benchmark-design.md),
[2026-08-04 plan](../plans/2026-08-04-retrieval-quality-benchmark-implementation-plan.md)) is merged and
pushed. `benchmark-ingest` ran against the live dev stack for the first time on 2026-08-24 (`83d0b67`),
posting 400 documents at 400 succeeded / 0 failed after two defects were fixed. Four debts remain open:

1. The eight-configuration ablation sweep has never run, so the fusion weights (0.60/0.30/0.10) and
   MMR λ (0.70) are still **chosen, not measured**.
2. §3 of the 2026-07-31 spec still carries a sentence that is known false.
3. `FreshStackCorpusParser` is entirely `UNRESOLVED` placeholders — field names guessed, never
   validated against the real dataset.
4. `ParseQrels` is unused on both parsers.

Ben's ruling (2026-08-25): **close the debts, defer the run** — but retain the ability to measure λ,
which is what FreshStack exists for. FreshStack is therefore retained and improved, not deleted.

## Goal

Make the harness in `main` honest and λ-capable: every corpus path validated against the real upstream
schema, every dead path removed, every false claim in the prior spec corrected, and the deferred sweep's
feasibility evidence recorded where the next person will find it.

Explicitly **not** a goal: running the sweep, or producing any measurement.

## Design

### 1. The converter — `Iverson.Server/Iverson.LoadTest/scripts/freshstack_to_jsonl.py`

FreshStack ships as HuggingFace parquet with a schema that does not match the harness's on-disk
expectations, and **ships no qrels file at all** — its judgments are nested inside the query rows.
A converter normalises the dataset into the BEIR-shaped JSONL the harness already parses.

```
--topic {angular,godot,langchain,laravel,yolo}   required
--out-dir <corpus-path>                          writes into <out-dir>/freshstack/
```

Loads `freshstack/corpus-oct-2024` from its **`train`** split and `freshstack/queries-oct-2024` from
its **`test`** split, at the given topic (`load_dataset(repo, topic, split=split)` — the config is the
**second positional** argument; there is no `subset=` parameter, and an unknown keyword falls through
to `**config_kwargs` without selecting the topic. The split differs between the two repos, so it is
passed alongside the topic rather than fixed once for both) and writes three files:

| File | Shape | Derivation |
|---|---|---|
| `corpus.jsonl` | `{"_id", "text"}` | corpus rows; `metadata` dropped, `title` omitted |
| `queries.jsonl` | `{"_id", "text"}` | `query_id`; text composed per §1.1 |
| `qrels.tsv` | `query_id ⇥ nugget_id ⇥ corpus_id ⇥ relevance` | nested `nuggets[]`, per §1.2 |

`title` is omitted rather than emitted empty: FreshStack has no such field, and the parser already
defaults a missing `title` to `""`.

The converter asserts the loaded config's name matches `--topic` rather than trusting the call, so an
upstream rename fails loudly instead of silently converting a different topic.

#### 1.1 Query text composition — an explicit, upstream-unverified choice

FreshStack does not document how it composes retrieval query text from `query_title` (24–147 chars)
and `query_text` (173–9.27k chars). The README, the project homepage, and the paper page were all
checked; none states it, and PyPI was unreachable.

**Decision: `query_title + "\n\n" + query_text`.** These are Stack Overflow questions, where the title
is the question and the body elaborates it; dropping either loses signal. This is recorded as a
decision rather than an assumption because getting it wrong does not fail — it silently changes what
is being measured. Anyone comparing numbers against FreshStack's published leaderboard must confirm
this matches upstream first.

#### 1.2 Nugget → qrels derivation

For each query, for each nugget in `nuggets[]`:

- every id in `relevant_corpus_ids` emits a row with relevance `1`
- every id in `non_relevant_corpus_ids` emits a row with relevance `0`

The nugget's `_id` lands in **column 2 — the TREC iteration field**. This is what makes α-nDCG
computable: `ir_measures` reads subtopic ids from exactly that column. Judged-nonrelevant rows are
emitted rather than skipped, because the metric's pooling expects them.

#### 1.3 The join assertion

Whether the nugget id lists resolve to `_id`s in the same topic's corpus **cannot be verified from
published documentation**. Rather than assume it, the converter checks it: it builds the corpus id set
and validates every qrels id against it — but the two lists get **different treatment, because their
guarantees differ**.

- **`relevant_corpus_ids` — hard-fail.** FreshStack's nuggets are generated *from* the corpus, so every
  relevant id should resolve by construction. If even one does not, the understanding of the join
  encoded here is wrong, and the right response is to stop rather than emit a qrels file that is
  quietly missing judgments. The converter exits non-zero with a count.
- **`non_relevant_corpus_ids` — drop and report.** These come from a retrieval judgment pool rather
  than from nugget generation, so the by-construction argument does not transfer to them. An
  unresolvable non-relevant id is dropped and counted, and the converter prints the total it dropped.
  Aborting on these would let a legitimate corpus block the λ measurement outright.

**An excluded document is not a join break.** §1.5 omits corpus ids containing whitespace; a judgment
referencing one must be tested against that exclusion set and dropped *before* the missing-id test
above, or excluding those documents trips the very hard-fail this section exists to raise.

A `qrels.tsv` referencing documents that were never ingested scores zero relevance and exits cleanly —
the same silent-success failure family that has already cost this branch three defects (`4330667`,
`5f25c35`, `cc674fa`).

#### 1.4 Dependency

The converter requires `datasets` (HuggingFace). The repo's only other standalone Python script,
`Iverson.Server/deploy/scripts/mint_acting_user_token.py`, is deliberately stdlib-only and says so at
`:87`. That rule is scoped to a smoke-test script that must run on any machine; this is a dev-only tool
run deliberately before a sweep, so the rationale does not carry. The dependency and its install command
are documented in the script's docstring. **Ben approved taking the dependency (2026-08-25).**

#### 1.5 The doc-id whitespace assertion

`TrecRunWriter` emits `qid Q0 docid rank score runtag` **space-separated**
(`Iverson.Server/Iverson.LoadTest/Benchmark/TrecRunWriter.cs:30-31`) — correct TREC format, and fine for
BEIR's opaque ids. FreshStack's `_id` is derived from repository paths and byte offsets, and real
GitHub paths can contain spaces. One such id shifts every subsequent column in its row, so the scorer
reads the wrong doc id, rank and score with no error anywhere — while `qrels.tsv`, being
tab-separated, survives intact, leaving run file and qrels to disagree silently rather than both
failing.

All three ids reach `qrels.tsv`, and TREC qrels readers split on whitespace rather than strictly on
tabs, so the column shift applies to each. The columns are nevertheless handled differently, because
measurement shows only one of them actually carries the problem and because the remedies differ:

- **`query_id` and `nugget_id` — hard-fail.** Both measured clean (0 violations across godot's 99
  queries and 325 nuggets). An offending value here cannot simply be dropped: excluding a query or a
  nugget silently changes what is being measured. The converter exits non-zero with a count.
- **`corpus_id` — exclude and report.** 24 of godot's 25,482 corpus ids contain spaces, because those
  ids are GitHub paths and real paths have spaces in them. Aborting would make the smallest topic —
  the one §4 recommends — unconvertible, so those documents are omitted from `corpus.jsonl`, the
  judgments referencing them are dropped, and both counts are printed.

#### 1.6 Duplicate ids and blank text

Two further conditions are settled in the converter rather than left to the harness:

- **Duplicate `_id` — first occurrence wins.** godot's 25,482 corpus rows carry only 25,477 distinct
  ids: five ids each cover two documents. Writing both files two documents under one judgment key and
  lets a single run file list the same doc id at two ranks, which TREC scorers treat as malformed or
  silently collapse — either way the ranking scored is not the ranking produced. The converter emits
  the first row for each id, skips the rest, and prints the count.
- **Blank `text` — hard-fail.** `JsonlCorpusParser` rejects empty text on both the corpus and the query
  path (§2), so a renamed upstream field would otherwise surface as a `FormatException` at the front of
  a `benchmark-ingest` run, against a corpus already written to disk. The converter tests the same
  condition — on the *composed* query string, so what is validated is what is written — and exits
  non-zero before writing anything. Measured clean on godot.

### 2. C# consequences of normalisation

After normalisation there is nothing FreshStack-specific left for C# to know. The work is subtraction
plus one guard:

- **Delete `Corpus/FreshStackCorpusParser.cs`.** Its two callsites
  (`BenchmarkIngestScenario.cs:90`, `BenchmarkQueryScenario.cs:207`) move to the surviving parser.
- **Rename `BeirCorpusParser` → `JsonlCorpusParser`** (and its test class). It will parse
  FreshStack-derived files too, so the current name would actively mislead.
- **Delete `ParseQrels`** from the surviving parser and the `Qrel` record from `CorpusModels.cs`, with
  its one test. Qrels are produced by the converter and consumed by the external scorer; the harness
  never holds them.
- **Add an empty-text guard to `ParseQueries`**, mirroring the `ParseCorpus` guard and its stated
  reasoning. This is a live defect: a missing or renamed text field currently yields empty query
  strings, an embedded-empty search, a zero-relevance run file, and exit 0.
- **Update every member of the corpus-arm set** — the four `Path.Combine` sites
  (`BenchmarkIngestScenario.cs:52,53`, `BenchmarkQueryScenario.cs:196,203`), both message pairs
  (`:60-61`, `:74-76`), the XML doc comments at `BenchmarkIngestScenario.cs:19-20`, and the
  `Program.cs` help text, which gains a pointer to the converter.

### 3. Corrections to the 2026-07-31 spec

- **§3, line 92.** Replace "That identity's bypass role sets `ownershipRequired` false, so no
  `OwnerId` property is needed" with the rule that actually governs: registration validates
  `OwnerField` against declared scalars regardless of roles
  (`SchemaRegistrationOrchestrator.cs:82-84`, via `ValidateFieldReference`), so `BenchmarkDocument` requires `OwnerId` — which the
  shipped code has. Add an assumptions-table row scoping **A21 to evaluation only**; conflating
  evaluation with registration is what generated the original Critical.
- **A1 (line 161).** Correct with the verified schema and measured sizes (§"Verified assumptions"
  below). A1's "3–4 nuggets per question" is actually 1–7. Record that FreshStack ships **no qrels
  file** and that the corpus carries **no title field**.
- **§2 / §5.** Record that scoring targets `ir_measures` with a converter-derived TREC file rather than
  FreshStack's own evaluation package (which takes three objects: `qrels_nuggets`, `qrels_query`,
  `query_to_nuggets`). The prior spec called its own package "lower-risk"; this is now a recorded
  decision rather than a silent default.

### 4. Recording the deferred sweep

Under the existing **A10** entry and "Known issues" (line 184), record the feasibility evidence that
now exists:

- The 2026-08-24 live run ingested 400 documents and drove a 4-core box to load ~20, hard enough that
  Kafka's `FIND_COORDINATOR` lookup began timing out — reproduced at the broker, not a client artifact
  (`8db2bf4`, `docs/runbooks/integration-test-flake-signatures.md`).
- The smallest viable FreshStack topic is **godot at 25,482 documents with 99 queries** — roughly 64×
  the volume that produced that load, and one topic alone matches the ~100-question statistical power
  the prior spec assumed from two.

**No new runbook.** §7 already documents the scratch-branch sweep procedure; duplicating it would drift.

### 5. Solution-file fix

`Iverson.LoadTest.Tests` appears in `Iverson.Server/Iverson.Server.slnx:18` but **not** in the root
`Iverson.slnx`, which lists only `Iverson.LoadTest`. All 15 LoadTest tests are invisible to a
root-solution build — the same defect class as the StarRocks projects that had never been in `Iverson.slnx`.
Add the one missing `<Project Path=...>` line. **Ben approved folding this in (2026-08-25).**

## Testing

- `dotnet build` on `Iverson.LoadTest`.
- `dotnet test` on `Iverson.LoadTest.Tests`: **15 today → 16** (−1 qrels test, +2 empty-query-text
  guards: missing field, empty string).
- Converter run against one topic, confirming it produces files the renamed parser reads and that the
  join assertion passes on real data.

The converter's own correctness is verified by running it, not by a unit test. Its two failure modes
that matter — a schema that does not match, and qrels that do not join — are properties of the upstream
dataset, which no fixture can test. This mirrors the lesson from `project-derived-vector-signals`: when
a design turns on "the external service produces X", a mock that produces X is not evidence.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| V1 | `corpus-oct-2024` has per-topic configs with `_id`/`text`/`metadata{start_byte,end_byte,url}`, no `title` | HF dataset viewer; sizes angular 117,288 / laravel 52,351 / langchain 49,514 / yolo 27,207 / godot 25,482 |
| V2 | `queries-oct-2024` has `query_id`/`query_title`/`query_text`/`nuggets`/`answer_id`/`answer_text`/`metadata`, split `test`, 672 rows | HF dataset viewer; per-topic langchain 203 / laravel 184 / angular 129 / godot 99 / yolo 57 |
| V3 | Each nugget carries `_id`/`text`/`relevant_corpus_ids`/`non_relevant_corpus_ids`, 1–7 per query | HF dataset viewer |
| V4 | Corpus ids in the nugget lists resolve to corpus `_id`s | **NOT VERIFIABLE from docs.** Absorbed as the converter's runtime join assertion (§1.3) |
| V5 | No separate qrels dataset ships | FreshStack README exposes only `dataloader.load_qrels(...)` deriving from the loaded queries; no qrels repo published |
| V6 | Query text composition | **NOT DOCUMENTED upstream.** Decided explicitly in §1.1 |
| V7 | `datasets` selects a topic config via the **`name`** argument (second positional), not `subset=`; the five topic names are correct | HF loading docs: config selection "is done by providing `datasets.load_dataset()` with a `name` argument" (`load_dataset('glue','sst2')`); unknown keywords fall through to `**config_kwargs`. FreshStack's README writes `subset=` — upstream's error, faithfully transcribed |
| V8 | `ir_measures` reads subtopic ids from the qrels iteration field | ir-measures docs: "we include the 'subtopic ID' as the optional 'iteration' field of a qrel"; TREC 4-col is query_id, iteration, doc_id, relevance |
| V9 | `Qrel` has no consumer outside `ParseQrels` and one test | `grep -rn "\bQrel\b" --include=*.cs` — `CorpusModels.cs:6`, both parsers, `BeirCorpusParserTests.cs:108-110`, nothing else |
| V10 | `BeirCorpusParser` rename blast radius | `BenchmarkIngestScenario.cs:81`, `BenchmarkQueryScenario.cs:200`, `BeirCorpusParserTests.cs` |
| V11 | `FreshStackCorpusParser` has exactly 2 callsites | `BenchmarkIngestScenario.cs:90`, `BenchmarkQueryScenario.cs:207` |
| V12 | `ParseQueries` has no empty-text guard; `ParseCorpus` throws `FormatException` | `BeirCorpusParser.cs:77` vs the guard above it |
| V13 | `Iverson.LoadTest.Tests` has 15 tests | 3 KeyMap + 8 BeirCorpusParser + 4 MaxPassageAggregator |
| V14 | All four corpus arms resolve `<CorpusPath>/<corpus>/<file>` identically | `Path.Combine` at ingest `:52,53`, query `:196,203` |
| V15 | `Iverson.LoadTest.Tests` is absent from root `Iverson.slnx` | Full file read; root lists `Iverson.LoadTest` only, at line 23 |
| V16 | Prior spec structure — line 92 false sentence, A1/A10/A21 at 161/170/181, Known issues 184, §7 sweep procedure at 135 | Read directly |
| V17 | Python stdlib-only convention exists and is deliberate | `mint_acting_user_token.py:87` — "No third-party dependency (pyotp isn't guaranteed to be installed on every ...)" |
| V18 | Registration validates `OwnerField` against declared scalars regardless of roles | `SchemaRegistrationOrchestrator.cs:82-84` — `var ownerField = descriptor.Authorization?.OwnerField;` then `ValidateFieldReference(descriptor, ownerField, "owner_field")`; test at `SchemaRegistrationOrchestratorTests.cs:48`; `SchemaRegistrar.cs:47` rethrows |
| V19 | Prior spec §5 `Scoring is external` spans lines 120–126; lines 123–125 carry the `ir_measures` / FreshStack-package statement §3's third bullet amends | Read directly |
| V20 | The two repos expose **different splits**: `corpus-oct-2024` has `train`, `queries-oct-2024` has `test` | Executed against `datasets` 5.0.1: `get_dataset_split_names("freshstack/corpus-oct-2024", "godot")` → `['train']`; the same call on the queries repo → `['test']` |
| V21 | `row["nuggets"]` is a list of dicts, so §1.2's per-nugget iteration is correct | Executed against `datasets` 5.0.1: feature type `List({'_id':…, 'non_relevant_corpus_ids':…, 'relevant_corpus_ids':…, 'text':…})`; `type(row["nuggets"])` is `list` |

## Known issues / accepted as out of scope

- **λ remains unmeasured.** This design makes λ *measurable*; it does not measure it. Ben deferred the
  run (2026-08-25).
- **A10 is still open, and now quantified rather than unknown.** See §4. Whether 25K documents ingest
  in tolerable time on the available hardware is unresolved, and the evidence available is not
  encouraging.
- **Query composition (§1.1) is a decision, not a verified fact.** Numbers produced under it are
  internally comparable across the sweep's eight configurations, which is what the ablation needs, but
  are not directly comparable to FreshStack's published leaderboard.
- **The nugget → iteration-field convention moves to Python** and is no longer covered by the C# test
  suite. Accepted: the failure that matters is upstream schema drift, which the C# suite could never
  have caught.
- **The converted corpus is 24 documents short of upstream's** (§1.5), and the judgments referencing
  them are dropped with them. Immaterial to comparing the sweep's eight configurations against each
  other, since the omission is identical across all eight; material to any comparison against
  FreshStack's published numbers.
- **`qrels.tsv` is subtopic-scoped only, and that is correct for α-nDCG but not for query-level
  metrics.** The nugget id in column 2 (the TREC iteration field) is exactly what makes α-nDCG and
  Coverage computable (§1.2) — but it is the only qrels file the harness emits, and the 2026-07-31
  design also promises Recall@50 and nDCG from FreshStack. A query-level reader keys on `(qid, docid)`
  and ignores the iteration column; measured on real godot output, 464 of 585 relevant `(qid, docid)`
  pairs carry both a `1` and a `0` under different nuggets, so collapsing to query-level loses 43–55%
  of relevant judgments and Recall@50 would come out roughly halved with no error anywhere. **Ruling:
  document the limitation, do not emit a second qrels file** — a `qrels.query.tsv` this spec's §1 file
  table does not list would create a fresh spec/code divergence, which is the disease this branch
  exists to cure; a query-level file is a code change that belongs in its own spec round.

## Not in this spec

- Running the sweep, editing any constant, or producing any measurement.
- Whether other projects are missing from `Iverson.slnx` beyond `Iverson.LoadTest.Tests`.
- BEIR corpus acquisition, which is unchanged and already documented.

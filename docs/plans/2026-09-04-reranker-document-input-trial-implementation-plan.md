# Reranker Document-Input Trial Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-04-reranker-document-input-trial-design.md` (commit SHA: `907de55`)

**Goal:** Measure whether feeding ms-marco MiniLM-L-6-v2 the title + full abstract (the published BEIR input) instead of the winning chunk changes the Phase 1 reranker verdict on SciFact, and record the answer.

**Architecture:** `benchmark-query` gains `--rerank-input winning-chunk|document`. In `document` mode the harness loads `beir/corpus.jsonl` once into a doc id → text map and hands the cross-encoder the corpus text of each of the 50 max-passage documents instead of its winning chunk; everything else in the rescore path (fetch, max-passage selection, TEI client, re-sort, run-file writer) is unchanged. One new arm, A4, is run and scored against the preserved A0 and A1 in two single-pair `report.py` invocations.

**Tech stack:** .NET 10 / C# (`Iverson.LoadTest`, xunit 2.9.3 + FluentAssertions 7.0.0), TEI `cpu-1.8` reranker in compose, `report.py` (Python 3.14, `ir_measures` 0.4.3 / `scipy` 1.18.1 via `PYTHONPATH`).

---

## Global Constraints

Copied from the spec; every task must hold to them.

- **Fail loud (spec §4, parent §6).** Corpus file missing or unparsable, a winner absent from the corpus map, an empty input text, any TEI failure: the run aborts. No fallback to chunk text, no skip.
- **Phase 1 semantics are untouched.** With no `--rerank-url`, `benchmark-query` produces exactly the run files it produces on `main` now. With `--rerank-url` and the default `--rerank-input winning-chunk`, it produces exactly what A1 produced.
- **The unit reranked is the document:** exactly `DocumentBudget = 50` inputs per query, one per document, in `winners.Ranked` order; batch size 8; rescore then re-sort through `DocumentRanking.CollapseByDocId`. `TrecRunWriter` sorts nothing.
- **Attribution from disk:** the `.meta.json` sidecar's `reranker` object carries `"input"`, and the banner prints it.
- **Statistics (spec §5):** two `report.py` invocations, one `--pair` each, Holm m = 1. The gate pair (A4 vs A0) must pass `check_pool` (identical per-query doc-id sets; ≥ 25 % sequences differ). An `ARM INVALID` exit on the A4 vs A1 invocation's ≥ 25 % half is a format finding, not an invalid arm. `PERMUTATION_SEED = 20260831`, `PERMUTATION_RESAMPLES = 10_000`, `HOLM_ALPHA = 0.05` unchanged.
- **Control drift test is columns 1–5** of both `.chunks.trec` and `.similar.trec` (column 6 is the run tag); whole-file comparison fails by construction.
- **A1 is reused, not re-run**, only if the columns-1–5 control check passes.
- Tests are written to fail against a specific named mutation.

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/Benchmark/RerankInputs.cs` — `RerankInput` enum; `RerankInputs.Parse` (flag value + url guard) and `RerankInputs.Select` (which text each winner sends to the cross-encoder).
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/RerankInputsTests.cs` — six tests for the two functions.
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/CommandFlagsTests.cs` — the flag parses, and its default is `winning-chunk`.
- (outside this repo, untracked) `~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/runs/rerank-a0-2026-09-04.*`, `rerank-a4.*`, `report-rerank-a4-2026-09.txt`.

**Modify**
- `Iverson.Server/Iverson.LoadTest/Program.cs` — `CommandFlags.RerankInput` + parse + help line + one `using`.
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` — guard, banner, sidecar, corpus map load, `RunChunksAsync` input selection.
- `docs/plans/2026-09-GATE-reranker-phase1.md` — append the A4 result.
- `docs/specs/2026-09-03-reranker-design.md` — one line in the §3.4 closing note.

## Inherited from spec

The following were verified by `thorough-brainstorming` (and CDR rounds 1–2) at spec-write time and are NOT re-verified here. Trusted as ground truth:

- V1 `CommandFlags` is init-only with a `Parse` reading `--rerank-url`/`--rerank-model` (`Program.cs:385-414`); the validation point is the guard at `BenchmarkQueryScenario.cs:81`.
- V2 `JsonlCorpusParser.ParseCorpus(TextReader)` (`JsonlCorpusParser.cs:13`) yields `CorpusDocument(DocId, Title, Text)` and refuses empty `text` (`:43-46`).
- V3 Every key-map doc id exists in `corpus.jsonl` (5,183 = 5,183, 0 missing either way).
- V4 The indexed chunk text is the corpus `text`; `chunk_index` 0 starts with the title.
- V5 The live stack holds the Phase 1 SciFact state (19,967 chunks / 5,183 docs, λ = 0.70) and `runs/rerank-a0.*`, `runs/rerank-a1.*` are preserved.
- V6 `Ranked` items carry the key-map-resolved corpus id (`MaxPassageAggregator.cs:59-77`).
- V7 `report.py` reads only the sidecar's `composite` key (`report.py:193`).
- V8 `--pair` runs Holm over the declared pairs with a per-pair pool check (`report.py:602-628`, `:631-666`, `:668-`).
- V9 TEI truncates, not rejects, long inputs (A1 sidecar `maxInputLength` 512, `autoTruncate` true).
- V10 10.9–17.1 % of abstracts are truncated; recorded, no design change.
- V11 No client-side length or payload limit trips on abstracts (`BatchSize` 8 < TEI cap 32; 55,193-char batch accepted).
- V12 `WinningChunkAggregation.Ranked` is `(DocId, Score, Text)` (`MaxPassageAggregator.cs:20-21`); `RunChunksAsync` is private; no test names the scenario except a comment.
- V13 Nothing constructs `CommandFlags` outside `Parse` (type-name grep: declaration `:385`, `Parse` `:400`, the call `:18`, seven `RunAsync` parameters).
- V14 `--config-label L` yields `L.chunks.trec` / `L.similar.trec` / `L.meta.json` (`BenchmarkQueryScenario.cs:258-259`, `:208`).
- V15 The CSRF re-mint fix `cf9cbb8` is on `main`.
- V16 The compose reranker defaults to ms-marco (`docker-compose.yml:160`); volume `iversonserver_reranker_models` exists; `/info` ready in < 60 s.
- V17 The gate doc and the parent spec's closing note exist on `main` (`12571d3`).
- V18 5,183 / 5,183 texts begin with the title; no prepend needed.
- V19 The winners' `Text` is consumed only at `BenchmarkQueryScenario.cs:373` (guard) and `:379` (`ScoreAsync`).
- V20 The live `/build` composite `31583db5aea49136` equals both preserved sidecars.
- V21 `corpus.jsonl` `_id` values are unique (5,183 distinct).
- V22 `RerankInput` / `RerankInputs` collide with no existing symbol.
- V23 Columns 1–5 are the drift test; `TrecRunWriter.cs:29-30` appends the run tag from `flags.ConfigLabel` (`BenchmarkQueryScenario.cs:262`).

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `Benchmark/RerankInputs.cs`, `Tests/Benchmark/RerankInputsTests.cs`, `Tests/Benchmark/CommandFlagsTests.cs` are new; `Tests/Benchmark/` exists | `ls` → all three "No such file"; directory holds 5 existing test files |
| P2 | Signature | `Program.cs` lacks `using Iverson.LoadTest.Benchmark;` (Task 2 adds it); `CommandFlags` is `public sealed` in the global namespace, callable from the test project | `Program.cs:1-9` usings (Auth, Entities, … no Benchmark); `:385` `public sealed class CommandFlags`, no `namespace` line in the file; test csproj `ProjectReference ../Iverson.LoadTest/Iverson.LoadTest.csproj` |
| P3 | Signature | `StrFlag(string[] a, string f, string d)` returns `a[i+1]` when the flag is present, else `d` | `Program.cs:427-436` |
| P4 | Signature | `TeiRerankClient.ScoreAsync(string query, IReadOnlyList<string> texts, CancellationToken ct)` returns `Task<IReadOnlyList<double>>` | `TeiRerankClient.cs:39` |
| P5 | Behaviour | An exception thrown from `RunAsync` on the `benchmark-query` path is unhandled at top level: message printed to stderr, non-zero exit — the same path the existing guards (`Console.Error` + `throw`) take | `Program.cs:191-192` dispatch has no `try`; the only `catch` blocks are `:109` and `:165` (schema registration) |
| P6 | Command | `dotnet test Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj` from `Iverson.Server/` → 43 passed today; `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_report.py -q` → 6 passed, 1 warning | run 2026-09-04 on `main` after the Phase 1 merge; no code change since |
| P7 | Code validity | FluentAssertions 7.0.0 supports `.Should().Throw<T>().WithMessage("*x*")` and `.Should().Equal(...)` on `IReadOnlyList<string>` | `JsonlCorpusParserTests.cs:46,56,66,76` use `WithMessage`; `~/.nuget/packages/fluentassertions/7.0.0/lib/net6.0/FluentAssertions.xml` documents `GenericCollectionAssertions.Equal` |
| P8 | Ordering | Task 1 consumes nothing new; Task 2 consumes Task 1's `RerankInput`/`RerankInputs`; Task 3 consumes Task 2's flag. No forward reference | by construction of the task bodies below |
| P9 | Consumer | `RunChunksAsync` has exactly one caller; adding two parameters breaks nothing else | `BenchmarkQueryScenario.cs:245` is the sole call |
| P10 | Consumer | Only `:373-377` (empty-text guard) and `:379` (`ScoreAsync` projection) are replaced; `:380-381` (`rescored`, `CollapseByDocId`) unchanged | read of `:359-382` |
| P11 | Signature | `ParseCorpus` returns `List<CorpusDocument>`; `CorpusDocument` is `record (string DocId, string Title, string Text)` | `JsonlCorpusParser.cs:13`; `CorpusModels.cs:3` |
| P12 | Code validity | Both projects target `net10.0`, so collection expressions, switch expressions and primary constructors compile | `Iverson.LoadTest.csproj:4`, `Iverson.LoadTest.Tests.csproj:3` |
| P13 | Command | `docs/plans/2026-09-GATE-reranker-phase1.md` and `docs/specs/2026-09-03-reranker-design.md` are tracked (dirs gitignored), so `git commit -- <path>` works; this plan needs `git add -f` | `git ls-files` from the repo root lists both |
| P14 | Command | `/home/ben/iverson-benchmark-data/bench-env.sh` exists; the run form is `dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label <L>` from `Iverson.Server/Iverson.LoadTest` | `test -f` ok; Phase 1 plan Task 7 steps 2–7 and its execution report |
| P15 | Command | `docker compose --profile reranker up -d reranker` / `… stop reranker` from `Iverson.Server/` start and stop ms-marco | run today during spec verification; `/info` reported `cross-encoder/ms-marco-MiniLM-L-6-v2 512 True` |
| P16 | Command | `report.py --qrels … --run A --run B --pair B=A` prints one `[pool]` line, three `[compare]` blocks, `Holm (1 tests)`, exit 0; the capture file `runs/report-rerank-a4-2026-09.txt` does not exist yet | CDR round 2 ran the shape with A1 for A4; `ls` → no such file |
| P17 | Sibling set (every referenced name resolves at its point of use) | scenario has `using Iverson.LoadTest.Benchmark;` and `using Iverson.LoadTest.Corpus;`; test files need `using Iverson.LoadTest.Benchmark;` (+ `FluentAssertions`, `Xunit`); `Program.cs` gets the Benchmark using in Task 2 | `BenchmarkQueryScenario.cs:8-9`; `MaxPassageAggregatorTests.cs:1-3`; P2 |
| P18 | Behaviour | The help text is one raw string literal; a line can be inserted after the `--rerank-model` lines | `Program.cs:262-266` inside the literal that ends at `:267` |
| P19 | Signature | `winners.Ranked[i].DocId` / `.Text` compile: `Ranked` is `IReadOnlyList<(string DocId, double Score, string Text)>` | `MaxPassageAggregator.cs:20-21` |
| P20 | Environment | The `_iverson_schema` row for `BenchmarkDocument` present today is the one Phase 1's A3 ran against; Task 3 step 1 re-checks it by grepping `benchmark-query --help` output for `Schemas registered.` — with `bench-env.sh` sourced that line is the fifth printed (tenant-provisioning preamble first, `Program.cs:93`, `:107`, `:152`, `:163`), so the check is on content, not on a line prefix or exit status | `psql` → one row `BenchmarkDocument`; Phase 1 report step 1; CIR round 1 §2.2 |
| P21 | Command | The Phase 1 score-collapse check is `awk '{print $1,$5}' <run>.chunks.trec \| sort \| uniq -d \| wc -l`, with the R16 acceptance rule (> 0.5 % rows, runs ≥ 3, or straddling rank 10 → stop) | `rerank-2026-09-task7-report.md:171-202` |
| P22 | Environment | `stack.py query`'s six services are the spec's "query tier" | `stack.py:84` → `["qdrant", "ollama", "postgres", "redis", "authentik-server", "iverson-api"]`, the six Phase 1 recorded healthy (CIR round 2) |
| P23 | Environment | Schema registration succeeds with StarRocks, Kafka, ZooKeeper and Jaeger stopped | Phase 1 ran `SchemaRegistrar` to `Schemas registered.` under exactly that tier (`rerank-2026-09-task7-report.md` step 2) (CIR round 2) |
| P24 | Consumer | `report.py` ignores the non-`.trec` files step 4 drops into `$RUN/runs` (`rerank-a4.dupes.txt`, `rerank-a4.log`) | `resolve_run_paths` globs `*.trec` and only for directory-valued `--run` (`report.py:117-152`); the plan passes explicit files (CIR round 2) |
| P25 | Command | The `15000` row denominator in step 4's collapse check is this arm's row count | `wc -l` on `rerank-a0/a1/a2.chunks.trec` = 15,000 each (300 queries × `DocumentBudget` 50) (CIR round 2) |

## Tasks

### Task 1: `RerankInputs` — parse the flag, select the text

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Benchmark/RerankInputs.cs`
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/RerankInputsTests.cs`

**Interfaces**
- Produces: `RerankInput` enum, `RerankInputs.WinningChunkFlag` / `DocumentFlag`, `RerankInputs.Parse(string value, string rerankUrl)`, `RerankInputs.Select(RerankInput, IReadOnlyList<(string DocId, double Score, string Text)>, IReadOnlyDictionary<string,string>?)` — consumed by Task 2.

- [ ] **Step 1: Write the failing tests.**

```csharp
using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class RerankInputsTests
{
    private static readonly IReadOnlyList<(string DocId, double Score, string Text)> Winners =
    [
        ("doc-a", 0.9, "chunk of a"),
        ("doc-b", 0.5, "chunk of b"),
    ];

    // Mutation: returning WinningChunk for "document" (or the default) would pass a no-op Parse.
    [Fact]
    public void Parse_Document_WithUrl_IsDocument()
    {
        RerankInputs.Parse("document", "http://127.0.0.1:8090").Should().Be(RerankInput.Document);
        RerankInputs.Parse("winning-chunk", "").Should().Be(RerankInput.WinningChunk);
    }

    // Mutation: a `_ => RerankInput.WinningChunk` default arm would silently run a mislabelled arm.
    [Fact]
    public void Parse_UnknownValue_Throws_NamingTheValue()
    {
        var act = () => RerankInputs.Parse("title-chunk", "http://127.0.0.1:8090");

        act.Should().Throw<InvalidOperationException>().WithMessage("*title-chunk*");
    }

    // Mutation: dropping the url check lets an input mode be declared for a reranker that is not there.
    [Fact]
    public void Parse_DocumentWithoutUrl_Throws()
    {
        var act = () => RerankInputs.Parse("document", "");

        act.Should().Throw<InvalidOperationException>().WithMessage("*--rerank-url*");
    }

    // Mutation: any reordering, or reading the corpus map in this mode, breaks the A1 semantics.
    [Fact]
    public void Select_WinningChunk_ReturnsChunkTextsInRankedOrder()
    {
        var texts = RerankInputs.Select(RerankInput.WinningChunk, Winners, corpusText: null);

        texts.Should().Equal("chunk of a", "chunk of b");
    }

    // Mutation: returning the chunk text, or iterating the dictionary instead of the winners, fails.
    [Fact]
    public void Select_Document_ReturnsCorpusTextsInRankedOrder()
    {
        var corpus = new Dictionary<string, string>
        {
            ["doc-b"] = "Title B. Abstract of b",
            ["doc-a"] = "Title A. Abstract of a",
        };

        var texts = RerankInputs.Select(RerankInput.Document, Winners, corpus);

        texts.Should().Equal("Title A. Abstract of a", "Title B. Abstract of b");
    }

    // Mutation: a TryGetValue fallback to "" or to the chunk text would be scored silently.
    [Fact]
    public void Select_Document_MissingDocId_Throws_NamingTheDocId()
    {
        var corpus = new Dictionary<string, string> { ["doc-a"] = "Title A. Abstract of a" };

        var act = () => RerankInputs.Select(RerankInput.Document, Winners, corpus);

        act.Should().Throw<InvalidOperationException>().WithMessage("*doc-b*");
    }
}
```

- [ ] **Step 2: Run the tests; all six fail to compile** (`RerankInputs` does not exist).
```bash
cd /home/ben/repositories/Iverson/Iverson.Server && dotnet test Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj
```

- [ ] **Step 3: Implement.**

```csharp
namespace Iverson.LoadTest.Benchmark;

/// <summary>Which text each of the 50 max-passage documents sends to the cross-encoder (spec §3.3).</summary>
public enum RerankInput
{
    /// <summary>The document's winning chunk, as Phase 1 scored (arm A1).</summary>
    WinningChunk,
    /// <summary>The document's full <c>corpus.jsonl</c> text — title + abstract, as the published BEIR setup (arm A4).</summary>
    Document,
}

public static class RerankInputs
{
    public const string WinningChunkFlag = "winning-chunk";
    public const string DocumentFlag     = "document";

    /// <summary>
    /// Parses <c>--rerank-input</c>. An unknown value is refused (a silent default would run a mislabelled
    /// arm), and <c>document</c> without <c>--rerank-url</c> is refused: an input mode for a reranker that
    /// is not there is a mislabelled arm too (spec §3.1).
    /// </summary>
    public static RerankInput Parse(string value, string rerankUrl)
    {
        var mode = value switch
        {
            WinningChunkFlag => RerankInput.WinningChunk,
            DocumentFlag     => RerankInput.Document,
            _ => throw new InvalidOperationException(
                $"--rerank-input '{value}' is not one of '{WinningChunkFlag}', '{DocumentFlag}'."),
        };
        if (mode == RerankInput.Document && string.IsNullOrWhiteSpace(rerankUrl))
            throw new InvalidOperationException("--rerank-input document was given without --rerank-url.");
        return mode;
    }

    /// <summary>
    /// The texts to score, one per winner, in <paramref name="winners"/> order — the caller re-pairs
    /// scores positionally. In <see cref="RerankInput.Document"/> mode a winner absent from the corpus
    /// map throws (fail loud, spec §4); it is never scored through a fallback text.
    /// </summary>
    public static IReadOnlyList<string> Select(
        RerankInput mode,
        IReadOnlyList<(string DocId, double Score, string Text)> winners,
        IReadOnlyDictionary<string, string>? corpusText)
    {
        if (mode == RerankInput.WinningChunk)
            return winners.Select(w => w.Text).ToList();

        if (corpusText is null)
            throw new InvalidOperationException("--rerank-input document requires the corpus text map.");

        var texts = new List<string>(winners.Count);
        foreach (var w in winners)
        {
            if (!corpusText.TryGetValue(w.DocId, out var text))
                throw new InvalidOperationException(
                    $"DocId={w.DocId} is absent from corpus.jsonl -- this arm must not be scored.");
            texts.Add(text);
        }
        return texts;
    }
}
```

- [ ] **Step 4: Run the suite: 49 passed** (43 + 6). Then run each mutation named in the test comments (one at a time, revert after) and confirm the named test fails.

- [ ] **Step 5: Commit.**
```bash
cd /home/ben/repositories/Iverson
git add Iverson.Server/Iverson.LoadTest/Benchmark/RerankInputs.cs Iverson.Server/Iverson.LoadTest.Tests/Benchmark/RerankInputsTests.cs
git commit -m "add RerankInputs: parse --rerank-input and select winning-chunk or full-document text for the cross-encoder"
```

### Task 2: Wire `--rerank-input` into `benchmark-query`

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs:1-9` (using), `:385-414` (`CommandFlags`), `:262-266` (help)
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs:81-85` (guard), `:150-152` (banner), `:198-204` (sidecar), `:214-215` (corpus map), `:245` (call), `:336-382` (`RunChunksAsync`)
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/CommandFlagsTests.cs`

**Interfaces**
- Consumes: Task 1's `RerankInput`, `RerankInputs.Parse`, `RerankInputs.Select`, `RerankInputs.WinningChunkFlag`.
- Produces: `--rerank-input` flag; sidecar `reranker.input`; banner `input=` — consumed by Task 3.

- [ ] **Step 1: Write the failing flag test.**

```csharp
using FluentAssertions;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class CommandFlagsTests
{
    // Mutation: a default of "document", or a misspelt flag name, would change every flag-less arm's meaning.
    [Fact]
    public void Parse_RerankInput_ParsesDocument_AndDefaultsToWinningChunk()
    {
        CommandFlags.Parse(["--rerank-input", "document"]).RerankInput.Should().Be("document");
        CommandFlags.Parse([]).RerankInput.Should().Be("winning-chunk");
    }
}
```

- [ ] **Step 2: `Program.cs`.** Add `using Iverson.LoadTest.Benchmark;` to the using block at `:1-9` (alphabetical: after `using Iverson.LoadTest.Auth;`). In `CommandFlags`:

```csharp
    public string RerankUrl   { get; init; } = "";
    public string RerankModel { get; init; } = "";
    public string RerankInput { get; init; } = RerankInputs.WinningChunkFlag;
```
and in `Parse`:
```csharp
        RerankUrl   = StrFlag(args, "--rerank-url",   ""),
        RerankModel = StrFlag(args, "--rerank-model", ""),
        RerankInput = StrFlag(args, "--rerank-input", RerankInputs.WinningChunkFlag),
```
Help, inserted after the `--rerank-model` lines (`:265-266`), same indentation:
```
              --rerank-input <mode> winning-chunk (default) scores each document through its winning chunk;
                                     document scores it through its full beir/corpus.jsonl text (title +
                                     abstract). Requires --rerank-url.
```

- [ ] **Step 3: Scenario — guard.** After the `--rerank-model requires --rerank-url` block (`:81-85`):

```csharp
        RerankInput rerankInput;
        try
        {
            rerankInput = RerankInputs.Parse(flags.RerankInput, flags.RerankUrl);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"benchmark-query: {ex.Message}");
            throw;
        }
```

- [ ] **Step 4: Scenario — banner and sidecar.** The `Console.WriteLine` at `:150-152` becomes (the `}` at `:153` stays):
```csharp
            Console.WriteLine(
                $"[benchmark-query] Reranking with {rerankerInfo.ModelId} at {flags.RerankUrl} " +
                $"(max_input_length={rerankerInfo.MaxInputLength}, auto_truncate={rerankerInfo.AutoTruncate}, " +
                $"input={flags.RerankInput}).");
```
Sidecar reranker object (`:198-204`) gains one line after `["autoTruncate"]`:
```csharp
                    ["autoTruncate"]   = rerankerInfo.AutoTruncate,
                    ["input"]          = flags.RerankInput,
```

- [ ] **Step 5: Scenario — corpus map.** After the key-map load (`:214-215`), before `LoadQueries`:

```csharp
        // document mode scores each winner through its full corpus text (spec §3.2). Loaded once; a
        // missing or unparsable corpus file aborts here, before the first query (fail loud, spec §4).
        IReadOnlyDictionary<string, string>? corpusText = null;
        if (rerankInput == RerankInput.Document)
        {
            var corpusFile = Path.Combine(flags.CorpusPath, "beir", "corpus.jsonl");
            using var reader = new StreamReader(corpusFile);
            corpusText = JsonlCorpusParser.ParseCorpus(reader)
                .ToDictionary(d => d.DocId, d => d.Text, StringComparer.Ordinal);
            Console.WriteLine(
                $"[benchmark-query] Loaded corpus text ({corpusText.Count:N0} documents) from {corpusFile} " +
                "for --rerank-input document.");
        }
```

- [ ] **Step 6: Scenario — `RunChunksAsync`.** Call site (`:245`):
```csharp
            var chunks  = await RunChunksAsync(query, headers, keyMap, reranker, rerankInput, corpusText, ct);
```
Signature (`:336-338`):
```csharp
    private async Task<(IReadOnlyList<(string DocId, double Score)> Ranked, int Failed, IReadOnlyList<string> Unresolved)> RunChunksAsync(
        CorpusQuery query, Metadata headers, IReadOnlyDictionary<string, string> keyMap,
        TeiRerankClient? reranker, RerankInput rerankInput, IReadOnlyDictionary<string, string>? corpusText,
        CancellationToken ct)
```
Replace `:370-379` (the empty-text guard comment + guard + `ScoreAsync` line) with:
```csharp
        // Which text each winner sends (spec §3.3). A winner absent from the corpus map throws here,
        // naming the query as well as the document (spec §4).
        IReadOnlyList<string> inputs;
        try
        {
            inputs = RerankInputs.Select(rerankInput, winners.Ranked, corpusText);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"QueryId={query.QueryId}: {ex.Message}", ex);
        }

        // Fail loud (spec §6) at query 1, not hour 3: an empty input text would be scored by TEI as
        // some arbitrary constant for every such document, which is silent corruption of that query's
        // ranking, not a visible failure. Runs over the selected inputs, so it covers both modes.
        for (var i = 0; i < inputs.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(inputs[i]))
                throw new InvalidOperationException(
                    $"QueryId={query.QueryId}: the rerank input text for DocId={winners.Ranked[i].DocId} " +
                    "is empty -- this arm must not be scored.");
        }

        var scores   = await reranker.ScoreAsync(query.Text, inputs, ct);
```
Lines `:380-381` (`rescored`, `return … CollapseByDocId`) stay as they are.

- [ ] **Step 7: Build and run the suite: 50 passed.** Then:
  - mutate `RerankInput`'s default in `CommandFlags` to `"document"` → `Parse_RerankInput_…` fails; revert.
  - confirm the no-reranker path is untouched: `git diff main -- Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` shows no hunk inside the `if (reranker is null)` block (`:359-364`) or in `RunSimilarAsync`.
```bash
cd /home/ben/repositories/Iverson/Iverson.Server && dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj -c Release && dotnet test Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj
```

- [ ] **Step 8: Commit.**
```bash
cd /home/ben/repositories/Iverson
git add Iverson.Server/Iverson.LoadTest/Program.cs Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs Iverson.Server/Iverson.LoadTest.Tests/Benchmark/CommandFlagsTests.cs
git commit -m "add --rerank-input to benchmark-query: score each document through its full corpus text when asked, and record the mode in the sidecar"
```

### Task 3: Run A4 and record the result

**Files:**
- Create (outside this repo, untracked — the corpora directory is not a git repository): `~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/runs/rerank-a0-2026-09-04.{chunks,similar}.trec`, `rerank-a0-2026-09-04.meta.json`, `rerank-a4.{chunks,similar}.trec`, `rerank-a4.meta.json`, `report-rerank-a4-2026-09.txt`
- Modify: `docs/plans/2026-09-GATE-reranker-phase1.md` (append a section), `docs/specs/2026-09-03-reranker-design.md` (one line in the §3.4 outcome block)

**Interfaces**
- Consumes: Task 2's `--rerank-input document`, sidecar `reranker.input`, banner `input=`.

This task is operational: A0 ≈ 11 min, A4 ≈ 3.5 h (spec §5). One arm at a time; nothing else on the box. The run crosses Authentik's 2 h token validity; V15 (`cf9cbb8`) is what makes that survivable — if the harness dies with `Authentication flow did not complete`, that fix has regressed and the run must not be re-tried until it is found.

- [ ] **Step 1: Environment.** The six-service query tier is what the spec assumes and what Phase 1 ran on — `docker compose up -d` would also start `iverson-worker` (the Kafka→Qdrant consumer, which can rewrite the benchmark collections) and re-run `authentik-migrate`. But the live containers were created from the since-removed `reranker-phase1` worktree, and `postgres` / `authentik-server` carry relative bind mounts, so even the tier-only `up` can recreate them under a running API. Ask Compose first, and change nothing in the step that proves nothing moved:
```bash
cd /home/ben/repositories/Iverson/Iverson.Server
docker compose --dry-run up -d --no-deps qdrant ollama postgres redis authentik-server iverson-api   # every line must read "Running"
```
If every line reads `Running`: `python3 Iverson.LoadTest/scripts/stack.py query --timeout 300` (a no-op `up` plus its out-of-tier stop). If any line reads `Recreate`: do NOT run `stack.py` or any `compose up` — the six are already up and healthy from Phase 1 — and only stop the out-of-tier containers:
```bash
docker stop iverson-worker iverson-starrocks iverson-kafka iverson-zookeeper iverson-jaeger 2>/dev/null   # absent names are fine
```
Either way:
```bash
docker ps --format '{{.Names}}' | grep -q '^iverson-worker$' && echo WORKER-RUNNING-STOP   # must print nothing
```
Verify the Phase 1 state the spec relies on, then the harness's schema path as Phase 1 did:
```bash
K=dev-only-not-for-production-qdrant-key-0123456789
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*'   # 19967
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_tenant_bypass        | grep -o '"points_count":[0-9]*'   # 5183
docker inspect iverson-api | grep -o '"VectorRanking__Lambda=[^"]*"'                                                          # 0.70
curl -s 127.0.0.1:8081/build | grep -o '"composite":"[^"]*"'                                                                   # 31583db5aea49136
export RUN=/home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
source /home/ben/iverson-benchmark-data/bench-env.sh
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest
dotnet run -c Release -- benchmark-query --help 2>&1 | grep -q "Schemas registered." && echo SCHEMA-OK   # must print SCHEMA-OK (P20); the command's exit status is ignored -- it refuses on the missing --corpus-path after the banner
```
Any mismatch in the four state checks stops the task: the spec's V5/V20 no longer hold and A1 cannot be reused. A missing `SCHEMA-OK` is an auth or schema-registration problem to investigate (Phase 1 report step 2 is the precedent), not index drift. The worker check is repeated before step 4.

- [ ] **Step 2: Same-session control.**
```bash
dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label rerank-a0-2026-09-04
diff <(cut -d' ' -f1-5 $RUN/runs/rerank-a0-2026-09-04.chunks.trec)  <(cut -d' ' -f1-5 $RUN/runs/rerank-a0.chunks.trec)  && echo CHUNKS-IDENTICAL
diff <(cut -d' ' -f1-5 $RUN/runs/rerank-a0-2026-09-04.similar.trec) <(cut -d' ' -f1-5 $RUN/runs/rerank-a0.similar.trec) && echo SIMILAR-IDENTICAL
```
Both must print. If either diff is non-empty: stop and report — the index or server drifted, A1 may not be reused (spec §9), and this plan does not decide what to do about it.

- [ ] **Step 3: Reranker up.**
```bash
(cd /home/ben/repositories/Iverson/Iverson.Server && docker compose --profile reranker up -d reranker)
until curl -sf http://127.0.0.1:8090/info >/dev/null; do sleep 5; done
curl -s http://127.0.0.1:8090/info | grep -o '"model_id":"[^"]*"\|"max_input_length":[0-9]*\|"auto_truncate":[a-z]*'
```
Must be `cross-encoder/ms-marco-MiniLM-L-6-v2`, `512`, `true`.

- [ ] **Step 4: A4.**
```bash
dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label rerank-a4 --rerank-url http://127.0.0.1:8090 --rerank-model cross-encoder/ms-marco-MiniLM-L-6-v2 --rerank-input document 2>&1 | tee $RUN/runs/rerank-a4.log
grep -o '"input": "[a-z-]*"' $RUN/runs/rerank-a4.meta.json     # "document"
grep -c "input=document" $RUN/runs/rerank-a4.log               # 1 (the banner)
# score-collapse check (P21): groups, rows / % of rows, and the ranks in each group -- the R16 rule's operands
awk '{print $1,$5}' $RUN/runs/rerank-a4.chunks.trec | sort | uniq -cd | tee $RUN/runs/rerank-a4.dupes.txt | wc -l
awk '{s+=$1} END {print s+0, (s+0)/15000*100 "%"}' $RUN/runs/rerank-a4.dupes.txt
awk '{print $2,$3}' $RUN/runs/rerank-a4.dupes.txt | while read q sc; do awk -v q="$q" -v sc="$sc" '$1==q && $5==sc {print $4}' $RUN/runs/rerank-a4.chunks.trec | paste -sd,; done
```
The banner must show `input=document`; the sidecar must carry it; the run must finish 300/300 with no `ARM INVALID`, no timeout, no `Authentication flow` error. Score-collapse rule from Phase 1 (R16), read off the three outputs above: duplicates are acceptable unless the duplicated rows exceed 0.5 % of rows (second line), any group's count is ≥ 3 (first column of the dupes file), or any group's ranks straddle 10 (third output) — any of those stops the task. Afterwards `docker compose --profile reranker stop reranker`.

- [ ] **Step 5: Score — two invocations, captured to one file.**
```bash
cd $RUN/runs
REPORT=/home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts/report.py
export PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs
{ echo "### gate: A4 vs A0"; python3 $REPORT --qrels $RUN/qrels.trec --run rerank-a0.chunks.trec --run rerank-a4.chunks.trec --pair rerank-a4.chunks.trec=rerank-a0.chunks.trec; echo "exit=$?"; } | tee report-rerank-a4-2026-09.txt
{ echo "### format: A4 vs A1"; python3 $REPORT --qrels $RUN/qrels.trec --run rerank-a1.chunks.trec --run rerank-a4.chunks.trec --pair rerank-a4.chunks.trec=rerank-a1.chunks.trec; echo "exit=$?"; } | tee -a report-rerank-a4-2026-09.txt
```
The gate invocation's `[pool]` line must show `set changed on 0` and ≥ 25 % sequences differ; its `exit=0`. On the format invocation, `set changed on 0` is required; an `ARM INVALID` on the ≥ 25 % half is the format finding (the reordering fraction is on the `[pool]` line, and both arms' macro scores print before it) and is recorded as such, not retried.

- [ ] **Step 6: Record the verdict** in this repo. Append to `docs/plans/2026-09-GATE-reranker-phase1.md` a section `## Document-input trial (A4, <date>)` giving, for A4 vs A0 on nDCG@10: delta, paired *t* p, permutation p (= Holm p_adj at m = 1), 95 % CI, d_z, MDE, queries changed; R@50 for A4 (must equal A0's to 4 decimals); the same rows for A4 vs A1 or, if that invocation exited `ARM INVALID`, the `[pool]` reordering fraction and both arms' macro nDCG@10 / R@50 / AP; whether A4's nDCG@10 exceeds the oracle ceiling 0.9216 (must not); the A4 wall time; and one line **TRIAL PASSED / TRIAL FAILED** per spec §5 step 5 (A4 vs A0 positive delta with p < 0.05 on both tests). State that A1 was reused on the strength of step 2's columns-1–5 identity. Then add one line to the `> **Outcome (2026-09-04): GATE FAILED.**` block in `docs/specs/2026-09-03-reranker-design.md` §3.4: `> Document-input trial (<date>, spec 2026-09-04-reranker-document-input-trial-design.md): A4 vs A0 <delta>, p <p> — <TRIAL PASSED/FAILED>; see the gate doc.`

- [ ] **Step 7: Commit both.** The run files, log and report capture stay in the corpora directory untracked, like every run file already there.
```bash
cd /home/ben/repositories/Iverson
git commit -m "record the reranker document-input trial result" -- docs/plans/2026-09-GATE-reranker-phase1.md docs/specs/2026-09-03-reranker-design.md
```

## Tasks NOT in this plan

Inherited from spec §2:

- Harness-only. No server change, no NFCorpus, no bge-reranker-base, no second input variant, no λ
  recording in the sidecar.

## Known issues inherited from spec

- Abstracts over the window lose their tail to `--auto-truncate` (10.9–17.1 %). This is the published
  setup; a windowing or head+tail policy is not part of this trial.
- A1 is reused rather than re-run. The columns-1–5 A0 check in §5 step 1 is the evidence that reuse
  is sound; if it fails, A1 must be re-run before A4 is compared to it.
- NFCorpus is not run. The trial answers the SciFact gate question only.

# Decay-Weight Sensitivity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-31-decay-weight-sensitivity-design.md` (commit SHA: `18fd90a4649df8b097caf97949d249dec5d9926a`)

**Goal:** Measure whether fusion triples A (0.50/0.50/0.10) and B (0.45/0.45/0.10) reorder results differently on the centroid-present branch, and apply the spec's fixed decision rule.

**Architecture:** One instrumented `benchmark-query` run captures the fusion's *inputs* (base score, centroid cosine, document id, centroid presence) per candidate. Because those inputs are weight-independent, every weight triple and every synthetic timestamp scenario is then evaluated offline in Python without re-running the stack. The instrumentation lives on a scratch branch and is never committed.

**Tech stack:** C# / .NET (server), Qdrant + Ollama (`snowflake-arctic-embed:s`, 384d) via docker compose, Python 3 for offline analysis.

---

## File Structure

**Modify (scratch branch only — never committed):**
- `Iverson.Server/Iverson.Vector/IResultReranker.cs` — add `ParentId` to `RerankCandidate`
- `Iverson.Server/Iverson.Vector/ResultReranker.cs` — locked capture of fusion inputs
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — populate `ParentId` at both call sites

**Create (scratchpad — not committed, per the stats.py / crossover.sh precedent):**
- `scratchpad/decay_sensitivity.py` — offline scenario sweep and reporting

**Modify (committed):**
- `docs/centroid-weighting-proposal.md` — the measured outcome and the decision it licenses

**Artifacts (not in git; `iverson-benchmark-corpora` is not a repository):**
- `iverson-benchmark-corpora/decay-capture-2026-08-31/fusion-capture.csv`

## Inherited from spec

The 21 assumptions in the spec's `Verified assumptions` table (A1–A21) were verified at spec-write time and are **not** re-verified here. The ones this plan leans on most directly:

- **A1** — centroid cosine is computed only at `ResultReranker.cs:40`, so the capture must live inside `Rerank`.
- **A13** — `ResultReranker` is a singleton serving concurrent requests; the append must be locked.
- **A17** — every query returns 50 results on both endpoints, so no `Rerank` call has an empty candidate list.
- **A18** — benchmark candidates always carry a centroid; the capture's `hasCentroid` column is expected to be uniformly 1 and doubles as a check on this.
- **A20** — all chunks of a document share one decay value, which is why ages are keyed by `parentId`.
- **A21** — `RerankCandidate` carries no payload, so `ParentId` must be plumbed onto the record.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `IResultReranker.cs`, `ResultReranker.cs`, `ObjectSearchGrpcService.cs`, `stack.py`, `bench-env.sh` all exist at the cited paths | Each read: 14, 61, 1001, 230 and 5 lines respectively |
| P2 | Consumer impact | Adding a **trailing defaulted** `ParentId` keeps all 13 `new RerankCandidate(...)` sites compiling | `grep -rn "new RerankCandidate("` → 13 hits: 11 in `Iverson.Vector.Tests/ResultRerankerTests.cs`, all using named arguments (`BaseScore:`, `Centroid:`, `Decay:`); 2 in `ObjectSearchGrpcService.cs`, both modified by this plan anyway |
| P3 | Signature | `benchmark-query` takes exactly `--corpus-path`, `--key-map-path`, `--output-dir`, `--config-label` | `BenchmarkQueryScenario.cs:46-62` — each flag has its own required-argument guard |
| P4 | Command | `python3 Iverson.Server/Iverson.LoadTest/scripts/stack.py query` is valid | `stack.py:84` defines a `"query"` tier; `:214` `add_argument("action", choices=[*TIERS.keys(), "down"])` |
| P5 | Command | `docker compose build iverson-api` (from `Iverson.Server`), `stack.py query`, and `dotnet run -c Release -- benchmark-query` are the working invocations | `scratchpad/crossover.sh:46-58` ran all three successfully 10 times across both crossover arms |
| P6 | Command | The API container is named `iverson-api`, so `docker cp` can reach it | `docker ps --format '{{.Names}}'` lists `iverson-api` |
| P7 | Code validity | The container process can write the capture file | `docker exec iverson-api id` → `uid=1000(ubuntu)`; `/tmp`, `/app`, `/var/tmp` all confirmed writable |
| P8 | File path | The 5.66 chunks/doc corpus has the inputs `benchmark-query` needs | `freshstack-chunk256-2026-08-30/` contains `beir/`, `keymap.json`, `qrels.trec`, `runs/` |
| P9 | File path | The analysis script cannot be committed beside the capture | `git -C iverson-benchmark-corpora rev-parse` → `fatal: not a git repository`. Script therefore lives in `scratchpad/` (uncommitted), matching `stats.py` and `crossover.sh` |
| P10 | Task ordering | Task 2 consumes Task 1's capture; there is no reverse dependency | Task 2 reads only `fusion-capture.csv` and `qrels.trec`; it modifies no file Task 1 touches |
| P11 | Command | Commit messages are plain imperative — no Conventional Commits prefix | `git log --oneline -20`: entries like `record the re-chunking crossover…`, `spec: decay-weight sensitivity…`; no `feat:`/`fix:` prefixes |

## Tasks

### Task 1: Instrument the fusion and capture one run

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/IResultReranker.cs:3-7`
- Modify: `Iverson.Server/Iverson.Vector/ResultReranker.cs:14-60`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:256-260` and `:441-447`

**Interfaces:**
- Produces: `iverson-benchmark-corpora/decay-capture-2026-08-31/fusion-capture.csv`, consumed by Task 2.

**Nothing in this task is committed.** The instrumentation is scratch-only, matching the existing practice for edited fusion constants. Step 7 restores the tree.

- [ ] **Step 1: Create the scratch branch**
```bash
cd /home/ben/repositories/Iverson
git checkout -b decay-capture-scratch
```

- [ ] **Step 2: Add `ParentId` to `RerankCandidate`**

Trailing and defaulted, so all 13 construction sites keep compiling (P2).

```csharp
public sealed record RerankCandidate(
    ulong    Id,
    double   BaseScore,
    float[]? Centroid,
    double?  Decay,
    string?  ParentId = null);
```

- [ ] **Step 3: Populate `ParentId` at both call sites**

`ObjectSearchGrpcService.cs:256-260` (SearchSimilar — candidates are objects, so the candidate *is* the document):

```csharp
var candidates = results.Select(r => new RerankCandidate(
    Id:        r.Id,
    BaseScore: r.Score,
    Centroid:  centroids.TryGetValue(r.Id, out var centroid) ? centroid : null,
    Decay:     DecayFor(r, decayField, now),
    ParentId:  r.Id.ToString(CultureInfo.InvariantCulture))).ToList();
```

`ObjectSearchGrpcService.cs:441-447` (SearchChunks — the parent key already drives the centroid lookup):

```csharp
var candidates = results.Select(r =>
{
    float[]? centroid = null;
    string?  parentId = null;
    if (r.Payload.TryGetValue("parent_id", out var parent) && !string.IsNullOrEmpty(parent))
    {
        parentId = parent;
        centroids.TryGetValue(IntelligenceStoreConsumer.KeyToUlong(parent), out centroid);
    }

    return new RerankCandidate(r.Id, r.Score, centroid, DecayFor(r, decayField, now), parentId);
}).ToList();
```

- [ ] **Step 4: Capture the fusion inputs inside `Rerank`**

`centroidCos` is hoisted out of the `if (hasCentroid)` block so it survives to the capture, and the rows for one call are appended under a single lock (A13). Add `using System.Globalization;` to **this** file — `ImplicitUsings` covers `System.Threading` and `System.IO` but not `System.Globalization`. `ObjectSearchGrpcService.cs` already imports it at line 1, so Step 3 needs no using change.

```csharp
private static readonly object CaptureLock = new();
private static int _callIndex = -1;
private const string CapturePath = "/tmp/fusion-capture.csv";
```

Inside `Rerank`, before the loop:

```csharp
var callIndex = Interlocked.Increment(ref _callIndex);
var captureRows = new List<string>(candidates.Count);
```

Inside the `foreach`, declare `double? centroidCos = null;` alongside `hasCentroid` / `hasDecay`, assign it where the cosine is already computed:

```csharp
var centroidSimilarity = TensorPrimitives.CosineSimilarity(queryVector, candidate.Centroid!);
centroidCos = centroidSimilarity;
```

and append one row per candidate just before `results.Add(...)` — placed after the `if/else` so the short-circuit branch is captured too:

```csharp
captureRows.Add(string.Create(CultureInfo.InvariantCulture,
    $"{callIndex},{candidate.Id},{candidate.ParentId},{(hasCentroid ? 1 : 0)},{candidate.BaseScore:R},{centroidCos?.ToString("R", CultureInfo.InvariantCulture)}"));
```

After the loop, before the `return`:

```csharp
lock (CaptureLock)
    File.AppendAllLines(CapturePath, captureRows);
```

- [ ] **Step 5: Build, deploy, and confirm the model**

```bash
cd /home/ben/repositories/Iverson/Iverson.Server && docker compose build iverson-api
cd /home/ben/repositories/Iverson && python3 Iverson.Server/Iverson.LoadTest/scripts/stack.py query
docker logs iverson-api 2>&1 | grep "EmbeddingService initialized" | tail -1
```

Expect `model=snowflake-arctic-embed:s dimension=384`. Stop if it differs — the capture would describe a different model than the crossover it is compared against.

- [ ] **Step 6: Run the capture**

No other traffic may hit the API for the duration (spec, "Run procedure"). ~30 minutes.

```bash
docker exec iverson-api rm -f /tmp/fusion-capture.csv
set -a; . /home/ben/iverson-benchmark-data/bench-env.sh; set +a
OUT=/home/ben/repositories/iverson-benchmark-corpora/decay-capture-2026-08-31
CORPUS=/home/ben/repositories/iverson-benchmark-corpora/freshstack-chunk256-2026-08-30
mkdir -p "$OUT"
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest
dotnet run -c Release -- benchmark-query \
  --corpus-path "$CORPUS" --key-map-path "$CORPUS/keymap.json" \
  --output-dir "$OUT" --config-label capture 2>&1 | tee "$OUT/capture-run.log"
docker cp iverson-api:/tmp/fusion-capture.csv "$OUT/fusion-capture.csv"
```

- [ ] **Step 7: Run the validation gate, then restore the tree**

The gate is the spec's; it refuses the analysis if a pre-`Rerank` throw shifted the call parity.

```bash
OUT=/home/ben/repositories/iverson-benchmark-corpora/decay-capture-2026-08-31
CALLS=$(cut -d, -f1 "$OUT/fusion-capture.csv" | sort -u | wc -l)
echo "distinct callIndex = $CALLS (expect 1344)"
grep -c "search RPC(s) failed" "$OUT/capture-run.log" || echo "no failure line — failures == 0"
[ "$CALLS" = "1344" ] || echo "GATE FAILED — do not proceed to Task 2"
```

Then restore, whatever the gate said:

```bash
cd /home/ben/repositories/Iverson
git checkout -- Iverson.Server/Iverson.Vector/IResultReranker.cs \
                Iverson.Server/Iverson.Vector/ResultReranker.cs \
                Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs
git checkout main && git branch -D decay-capture-scratch
cd Iverson.Server && docker compose build iverson-api
cd /home/ben/repositories/Iverson && python3 Iverson.Server/Iverson.LoadTest/scripts/stack.py query
```

### Task 2: Score the scenarios offline and apply the decision rule

**Files:**
- Create: `scratchpad/decay_sensitivity.py`
- Modify: `docs/centroid-weighting-proposal.md`

**Interfaces:**
- Consumes: `fusion-capture.csv` from Task 1 (columns `callIndex, candidateId, parentId, hasCentroid, baseScore, centroidCos`).

- [ ] **Step 1: Write the scenario sweep**

The two fusions, branch-selected by `hasCentroid` exactly as the spec's table specifies:

```python
def fuse(wb, wc, wd, base, centroid, decay, has_centroid):
    if not has_centroid and decay is None:
        return base                      # mirrors ResultReranker.cs:24-31
    num, den = wb * base, wb
    if has_centroid:
        num += wc * centroid; den += wc
    if decay is not None:
        num += wd * decay;    den += wd
    return num / den

A = lambda b, c, d, h: fuse(0.50, 0.50, 0.10, b, c, d, h)
B = lambda b, c, d, h: fuse(0.45, 0.45, 0.10, b, c, d, h)
```

Ages are assigned **per `parentId`**, never per candidate (A20), and decay mirrors `DecayFieldResolver.ComputeDecay`:

```python
def decay_for(age_days):
    return min(1.0, 0.5 ** (age_days / 180.0))

SCENARIOS = {
    "uniform": lambda rng, n: [365.0] * n,                     # control: must reorder nothing
    "narrow":  lambda rng, n: rng.uniform(0, 30, n),
    "wide":    lambda rng, n: rng.uniform(0, 720, n),
    "bimodal": lambda rng, n: rng.choice([7.0, 730.0], n),
}
```

- [ ] **Step 2: Report per scenario**

Group rows by `callIndex` (one result set per call), score both triples, sort descending, and report:
fraction of calls whose **top-10 set** changes, mean rank displacement, and Kendall tau. Assert the `uniform` control shows zero set changes — a non-zero control means the implementation is wrong, not that the fusion is sensitive.

Also record nDCG@10 for both triples against `freshstack-chunk256-2026-08-30/qrels.trec`, joining calls to queries by `callIndex` parity (`2i` similar, `2i+1` chunks). **This number is recorded, not used to choose** — with timestamps uncorrelated to relevance it favours whichever triple carries less decay by construction (spec, "Reported but explicitly not decisive").

- [ ] **Step 3: Apply the decision rule and record the outcome**

The rule is fixed by the spec: if the top-10 set is unchanged for **≥99% of calls** under **both** `wide` and `bimodal`, ship triple B and close the hold; otherwise the choice becomes a product decision about decay's intended share.

Write the result into `docs/centroid-weighting-proposal.md` — the scenario table, the decision the rule produced, and the fact that it covers the centroid-present branch only.

- [ ] **Step 4: Commit**
```bash
cd /home/ben/repositories/Iverson
git add docs/centroid-weighting-proposal.md
git commit -m "record the decay-weight sensitivity result and the triple it licenses"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's "Out of scope":

- **The optimal decay weight.** No corpus here judges recency; nothing available can answer it.
- The MMR / `Lambda` question.
- The 180-day half-life in `ComputeDecay`.
- Any change to the centroid weight itself, which remains held pending this result.

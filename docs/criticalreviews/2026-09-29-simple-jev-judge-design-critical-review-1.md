# Critical Design Review: 2026-09-29-simple-jev-judge-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-29-simple-jev-judge-design.md`
**Artifact HEAD at review:** 861a8ef9db3b6d1c1bd1a9892d84405439a3b65e
**Verified Assumptions section:** present

Probe scratch (not in the repo): `/tmp/claude-1000/-home-ben-repositories-Iverson/8ea909e2-a873-4454-bbef-4ce0a5c26326/scratchpad/cdr/`. It holds `tables.py` (the §3 table), `render.py`, `renderall.py` and `nfall.py` (hf_server's own `PromptCompiler.compile(render_only=True)` over the real pools, with `fastapi` stubbed because it is not installed here), `maxbranch.json`, and the demo request bodies. There were 11 demo requests in total, all sent sequentially with public SciFact/NFCorpus data and `User-Agent: curl/8.5.0`.

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | §1 The question | ok. [existence] Signature read: `IResultReranker.Rerank(float[] queryVector, IReadOnlyList<RerankCandidate>)` at `Iverson.Vector/IResultReranker.cs:13-16` is synchronous and carries no text. The claim that `noul` is `P(yes)` under `shared_*` is covered by A12. |
| S2 | §2 Rulings | ok. These are rulings, not claims. Each is honoured by a later section: the screen runs on NFCorpus (§5), `report.py` stays unchanged (§9), and one `ir_measures` command is recorded (§7). |
| S3 | §3 Inputs + baseline/ceiling table | ok. [presence] `md5sum` of the 4 files matches the table exactly (`e51bf248…`, `f7572e6a…`, `b167cf42…`, `afd2c240…`). [totality] `tables.py` over all 300 queries: identity nDCG@10 0.7452, R@50 0.9337, AP 0.7018, R@10 0.8770, P@10 0.0993. Oracle nDCG@10 0.934502 (so headroom = +0.18928), reversed 0.0011 / AP 0.0212 / R@10 0.0033 / P@10 0.0003. 107 of 300 queries have oracle > identity. |
| S4 | §4 Judge request, acceptance, ordering | ok. [compat] The shape is valid. The exact §4 body (50 × `noul`, SciFact q1) was pushed through both consumers: the demo returned HTTP 200 with 50 answers, and hf_server's `PromptCompiler.compile(render_only=True)` compiled it without error (`render.py`). The acceptance and ordering rules are rows R1 and R2. The request length is a separate problem, S7 → §2.1. |
| S5 | §5 Stage A (screen, demo) | ok. Arms 1–5 are the five demo ids. The largest NFCorpus pool (PLAIN-478, the longest branch at 133,986 rendered chars) returned 200 on all 5 demo models (F5). RWKV is excluded per A14. The transport rules are rows R3 and R4. |
| S6 | §6.1 Dry run | ok. The two-sided design is checked in R11 and R12. The stub does no tokenizing, so it cannot catch §2.1. That is recorded under §2.1, not as a separate defect. |
| S7 | §6.2 Pod | `--max-model-len 16384` → §2.1. The GPU sizing uses bf16 weights only → §3.1. The flags exist (A11). The policy pin matches the code (A13, R9). Revisions match HF (A23). |
| S8 | §6.3 Passes | → §2.1: all three pod passes (transfer 20 NFCorpus, main 300, repeat 50) exceed 16,384. The subsample and restricted-qrels mechanics are ok (R8, R10). |
| S9 | §7 Verdict | ok. [existence] Code read: the `report.py --pair` preconditions are real (`POOL_MIN_REORDERED_FRACTION = 0.25` at `report.py:700`; the set and sequence checks at `:732-766`). The permutation p and delta come from `print_compare_block` (`:582-627`). The threshold wording is dropped at R6. |
| S10 | §8 PASS/FAIL scope | ok. Non-load-bearing scope text. The choice-at-k=50 400 is a recorded probe that nothing downstream depends on. |
| S11 | §9 Components | ok. [existence] Every reused helper exists at the cited line on `main` (`load_run :75`, `load_corpus :117`, `load_queries :122`, `select_subsample :140`, `read_responses_ledger :214`, `accepted_entry :253`, `append_response :264`, `make_record :272`). `write_run` hard-codes `RUN_TAG` (`:55`, `:502`). There is no `*jev*` in `scripts/`. |
| S12 | §10 Verified assumptions | → §1 (A15 failed); span check → §3.1. |
| S13 | §11 Known issues | ok. [existence] These are accepted risks, and each one is consistent with the probes. The stale top-level README table is at `README.md:159-163` against code `hf_prompt_policies.py:41-52`. The demo's differing `usage` accounting (27B 78,248 vs 4B 139,974 input tokens for the same PLAIN-478 body) confirms the note that "the demo's serving stack differs". |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | Response acceptance (every `d<i>` present; `noul` ∈ [0,1]; `score` ∈ [0,3]) | Over-inclusion (accepts a response it should reject): ok. [existence] Code read: `restore_binary_noul` sets `noul = probabilities['yes']` (`hf_prompt_policies.py:182-186`), a softmax probability in [0,1]. `score` is the expected zero-based index, range 0..N−1 (API_REFERENCE:234), so 0..3 for the 4-level rubric. A missing `d<i>` or a non-finite value is rejected by the rule's own terms. Under-inclusion (rejects a valid response): ok. [compat] Real demo responses were pushed through the bounds. The saved SciFact q1 response (`resp-sf1-4b.json`) has 50/50 `d<i>` keys with `noul` in 0.0124–0.6091. The extremes printed from the other probes (0.0097 on the 27B, 0.9900 on Gemma-26B) are also inside the closed [0,1] interval. The stub's extreme values 1.0 (`1 − 0/50`) and 0.0 (`0/50`) are also inside it. |
| R2 | Ordering: stable descending sort, ties keep baseline order, synthetic score `51 − position` | Over-inclusion: ok. [totality] Writing all 300 pools in file order with scores 50..1 reproduces the baseline's own-score metrics on all 5 measures to 4 dp (`tables.py`). This holds even though the baseline has adjacent 6-dp score ties on 7 SciFact queries. Under-inclusion: ok. [totality] Over every query of both pools (300 + 323), file order is rank order: 0 queries have an ascending score step, and 0 queries have a rank column differing from 1..50. So "baseline order" is well-defined for every query. |
| R3 | Retry classes (429/5xx/timeout/rejected retry; 400/422 fall back at once; 403 aborts) | Over-inclusion: an over-length branch returns 422 (`hf_server.py:351` ValueError → `:1093-1094`), so every over-length query falls back at once → §2.1. Under-inclusion: ok. [existence] Code read: the route catches only `OverloadedError` (→ 429), `ValidationError` and `ValueError` (→ 422) (`hf_server.py:1089-1094`). A CUDA OOM (`RuntimeError`) therefore escapes as a 500 and is retried (relevant to §3.1). A 422 from `--enforce-model-id` (`:884-885`, `ValueError`) fails every query, and the fallback cap makes that loud. |
| R4 | Fallback cap (>16/323 invalidates an arm; >15/300 refuses the gate) | Over-inclusion (a run that should be refused passes the cap): ok. [existence] Arithmetic: the strict "more than" bounds admit at most 16/323 = 4.95% and 15/300 = 5.00%, so nothing above 5% passes. Under-inclusion (a run that should pass is refused): ok. [existence] Arithmetic: 16 fallbacks, the largest count at or below 5% of 323, still leaves the arm valid, and 15 of 300 still permits the gate. So no run at or below 5% is refused. For the cap's interaction with §2.1 (300/300 fall back), see §2.1. |
| R5 | Selection rule (highest valid-arm NFCorpus nDCG@10; exact ties → earlier arm) | Over-inclusion: ok, because only valid arms are eligible. Under-inclusion: ok. The dropped candidate "every arm invalid" is disproved by probe F5 (the largest pool is accepted on all 5 models). |
| R6 | Gate: p < 0.05 and delta ≥ +0.047 | Over-inclusion (false PASS): ok. [existence] Code read: the permutation test is two-sided (`alternative="two-sided"`, `report.py:538-546`), and the gate also requires delta ≥ +0.047. A negative or small positive delta cannot pass whatever its p. With a one-pair `--pair` family, Holm's p_adj equals the permutation p (`holm_adjust` with m = 1, `:471-486`), so the printed p is the gated p. Under-inclusion (false FAIL): ok. [existence] The +0.047 threshold lies inside the MDE range the spec cites, 0.032–0.054 (A19, reconfirmed in §1). A true effect at the threshold is detectable at about the power the spec pre-registered; that is a chosen trade-off, not a defect. The candidate "+0.047 vs the derived 0.0473" was dropped. It is not an ambiguity: the stated gate is +0.047, and stage 1 used the same round-down convention (`2026-09-20-teacher-ceiling-design.md:176`: 25% × 0.2216 → "+0.055"). |
| R7 | Ledger identity: resume keys on accepted entries per `query_id`; one ledger per pass | Over-merge (two distinct passes or queries conflated): ok. [presence] A real ledger line dumped from `teacher-ceiling-2026-09/main.responses.jsonl` has the keys `completion_tokens, content, elapsed_s, order, pass, prefix_len, prompt_tokens, query_id, reason, reasoning, status`. So the per-query key (`query_id`) and the pass stamp (`pass`) are persisted, and per-pass ledgers keep passes apart. `accepted_entry` returns the first `status=="accepted"` record for that `query_id` only (`teacher_rerank.py:253-261`). Under-merge (a resumable query not recognised): ok. [existence] Code read: `read_responses_ledger` keys records by `str(query_id)` (`:248-249`), which matches `make_record`'s `str(query_id)` (`:275`). A fallback query has no accepted entry, so it is retried on resume, which the spec intends. |
| R8 | Repeat pass scored against qrels restricted to the 50 ids | Under-inclusion (unrestricted qrels would silently count the 250 absent queries as zeros): ok. [totality] The spec's restriction is required: `repeat-2.chunks.trec` (50 queries) scores nDCG@10 0.1321 against the full qrels and 0.7927 against `qrels-sub50.trec`. `iter_calc` yields 300 rows against the full qrels, so the 250 absent queries score 0. Over-inclusion (the restricted qrels admit a query outside the 50): ok. [totality] `qrels-sub50.trec` (the precedent) holds exactly 50 distinct query ids across its 65 rows, and `iter_calc` over it with the full 300-query bge-base run yields exactly 50 values. A qrels file built the same way from `select_subsample`'s 50 ids contains only those ids by construction. |
| R9 | Policy pin from code `KNOWN_PROFILES` | Over-inclusion (a model pinned to a policy that is not its profile's): ok. [bidirectional] Spec → code: each of the spec's five model → policy pairs appears in `KNOWN_PROFILES` (`hf_prompt_policies.py:41-52`): 4B, 27B and Gemma-26B → `shared_examples_binary`; 35B-A3B and Gemma-12B → `shared_repeat_state`. Under-inclusion (a screen model with no pinned policy): ok. [bidirectional] Code → spec: all five `KNOWN_PROFILES` rows are covered by the spec's list, and the five screen arms map one-to-one onto them. The HF `config.json` fields read in-round match the profile tuples: Qwen 4B 32 layers / 4 kv / 256; 27B 64 / 4 / 256; 35B-A3B 40 / 2 / 256; Gemma-12B 48 / 8 / 256; Gemma-26B 30 / 8 / 256. |
| R10 | `select_subsample(ids, 50, 20260929)` | ok. [existence] Code read: `random.Random(f"{subsample_seed}").sample(sorted(run_query_ids), n)` (`teacher_rerank.py:146`). It sorts before sampling, so the 50 ids depend only on the seed and the id set. |
| R11 | Two-sided dry run (identity must be refused by `--pair`; reversed must pass) | Over-inclusion (a broken client passes the dry run): ok. [totality] Over all 300 queries, the reversed ordering scores nDCG@10 0.0011 and the identity ordering 0.7452 (`tables.py`). A client that discards replies, or that gets a 422 from the stub on every query, falls back to baseline order. It then reads 0.7452 in reversed mode instead of the required 0.0011, so it fails. Under-inclusion (a correct client fails the dry run): ok. [existence] Code read: identity makes `check_pool` see 0 reordered, so `sys.exit` fires (`report.py:760-766`) only after the scores print (`main` prints scores before `run_pair_statistics`, `:915-928`). The required 0.7452 and the required non-zero exit are therefore both observable, and a correct identity run meets both. |
| R12 | Stub doc-text guard (422 unless the instructions contain the text of the doc at that position) | Over-inclusion (the stub accepts a defective prompt): ok. [existence] By the rule's own terms, a bare `[docid]` prompt does not contain the doc's text, so the substring test fails, and a misaligned position fails because the check is per position. Under-inclusion (the stub rejects a correct prompt): ok. [totality] Every pool doc resolves to a corpus `text` (0 missing in either pool, F2), and the §4 wording embeds that `text` verbatim after `Document:\n`. So a correctly built question always contains it. |
| R13 | hf_server branch-length limit (`len(ids) > max_tokens` → 422) × every request population | The population-closure matrix. Under-inclusion (requests the design needs admitted but that are rejected): SciFact main 300 → §2.1; SciFact repeat 50 (a subset) → §2.1; NFCorpus transfer 20 → §2.1; stub dry run: ok (no tokenizer, so the limit does not apply); demo screen (a different server with its own limit): ok. [totality] The largest NFCorpus body was accepted by all 5 demo models (F5), and every other NFCorpus body renders shorter (`maxbranch.json`). Over-inclusion (requests admitted that should not be, i.e. silently truncated): ok. [existence] Code read: nothing is truncated. The text path encodes the whole prompt and refuses on length (`hf_server.py:350-354`), and the vision path uses `truncation=False` and refuses on length (`hf_vision.py:158-162`). |

### Data-flow arrows

| # | Arrow → consuming operation | Disposition |
|---|---|---|
| F1 | pool `.trec` → `load_run` → position `i` for `d<i>` | ok. [totality] 300×50 and 323×50, in file order = rank order. |
| F2 | `corpus.jsonl` `text` → question `instructions` | ok. [totality] 0 pool doc ids missing in either corpus. The claim that text starts with the title holds for 3,633/3,633 NFCorpus docs, and for 5,165/5,183 SciFact docs exactly plus the remaining 18 after stripping trailing whitespace in the title. The dropped discrepancy is not load-bearing because the title text is present. |
| F3 | `queries.jsonl` → `state` | ok. [totality] Over every pool query id (300 SciFact, 323 NFCorpus), 0 are missing from its `queries.jsonl`. |
| F4 | §4 request → hf_server `PromptCompiler.compile` → branch token check (pod: transfer, main, repeat) | → §2.1. |
| F5 | §4 request → demo `/v1/classifier` (screen) | ok. [compat] Real bodies sent: SciFact q1 → 200 (4B). NFCorpus PLAIN-613 (median) → 200 (4B, Gemma-26B). PLAIN-478 (largest) → 200 on all five: 4B, 27B, Gemma-26B, Gemma-12B, 35B-A3B. |
| F6 | response → acceptance → ordering → run file (persisted) → `ir_measures`/`report.py` | ok. [compat] The synthetic 6-column file written the §4 way was scored by `ir_measures` with the expected values (R2). |
| F7 | sidecar (persisted) → `report.py load_build_composite` | ok. [presence] `bge-base.meta.json` holds `"composite": "7d3a15092f963723"`. `jev-<model>.chunks.trec` resolves to `jev-<model>.meta.json` (`report.py:167-181`). |
| F8 | ledger write → resume read (persisted) | ok. See R7. |
| F9 | demo ledger (persisted) → transfer check (Spearman ρ, mean abs judge-value difference) | dropped. The ledger's value field is unspecified, but `make_record`'s `content` slot can carry the raw `answers`. That is a plan-level choice, not a design break. |
| F10 | run files → `report.py --pair` gate | ok. See S9 and R11. |
| F11 | run files → recorded `ir_measures.calc_aggregate([nDCG@10,R@50,AP,R@10,P@10])` | ok. [compat] The same measure-object call ran in `tables.py` against both pools. |
| F12 | sidecar fallback ids → worst-case re-score | ok. [existence] The design specifies it: §6.3 item 2 puts "fallback count with query ids" in the `reranker` block (spec lines 197-200), and §7's worst-case bound needs exactly those ids. For precedent, `teacher-ceiling-2026-09/replay/repeat-1.meta.json` carries a `"fallbackQueryIds": []` key. The bound can be computed only when that field is written, which is a plan-time obligation on `jev_rerank.py`. |
| F13 | screen winner → pod `--model`/`--revision`/policy | ok. [existence] The HF API returns the spec's revisions: `851bf6e806ef…` (Qwen/Qwen3.5-4B), `1d4bf0f2ff60…`, `995ad96eacd9…`, `707f0a3b8a3c…`, `4d7ae4984b7d…`, all ungated. |
| F14 | model + prefix KV → pod GPU memory | → §3.1. |
| F15 | (dropped candidates) | Several candidates were dropped. **Transfer check "first 20" has no `--first` flag:** the pool file can be truncated, so this is not impossible. **The repeat-pass delta command is unspecified**, and `--pair` could refuse < 25% reordering, but the delta is reported and not gated, and `--baseline` with restricted qrels works (precedent `report-output-repeat.txt`). **A `/` in `jev-<model>` filenames** is trivial. **A22:** `main` is now 1 ahead of `origin/main` (the spec commit itself), which is not load-bearing for the scripts. **Throughput:** the demo 4B took 10.9–26 s per request, slower than the §5 estimate, but that is an estimate only. |

**Totals: 41 rows. 31 ok; 7 → findings (S7, S8, S12, R3, R13, F4, F14); 3 dropped (R6, F9, and F15, which bundles five dropped candidates in one row).** R6 now shows both failure directions as ok, but it stays counted as dropped because of its rounding candidate.

## 1. Verified-assumptions cross-check

- A1 still holds. The loaders and subsample are at `teacher_rerank.py:75-127,140-147`, and `load_corpus` returns `(title, text)`.
- A2 still holds. The imports at `:33-41` are stdlib only, and `main()` is guarded at `:649`.
- A3 still holds. See `:253-261` and `:272-284`.
- A4 still holds. `RUN_TAG = "teacher-ceiling"` is at `:55` and is used at `:502`.
- A5 still holds. See `report.py:700,731-766`; `--pair` is at `:851-859`.
- A6 still holds. See `report.py:184-202`, and the sidecar composite is `7d3a15092f963723`.
- A7 still holds. `GATE-embedding-migration.md:135` reads 0.7452 / 0.9337, and it reproduces.
- A8 still holds. `qrels-sub50.trec` exists (65 rows), and restriction matters (R8).
- A9 still holds. The rows are 4 columns: rel 1 ×11,758 and rel 2 ×576, with 323/323 queries covered.
- A10 holds modulo whitespace. 18 SciFact titles have trailing whitespace. See F2.
- A11 still holds. The flags are at `hf_server.py:1289-1330`, `--host` defaults to `127.0.0.1` (`:1329`), and API_REFERENCE:21 says no authentication.
- A12 still holds. See `hf_prompt_policies.py:107-120,182-186`, with the call at `hf_server.py:946`.
- A13 still holds. The code profiles are as the spec states (R9). The top-level README table (`README.md:159-163`) is stale, and `hf-server/README.md:123-129` agrees with the code.
- A14 still holds. `choices=["transformers", "laya"]`.
- **A15 failed.** A15 checked one document against the limit, but under hf_server's v1 builder every branch carries all 50 documents. `prompt_builder.py:177-183` lists every question's `instructions` in the shared prefix. `hf_server.py:272` puts that prefix in the system message of every branch. `prompt_builder.py:257-263` repeats the selected question's instructions twice. hf_server's own compiler, run over the real pools, renders SciFact branches of 69,731–150,319 chars. At the demo-measured 2.90–3.05 chars/token, no SciFact query fits 16,384. The cited "10,026 tokens" was a single-document request. → §2.1.
- A16 still holds. The 422 is at `:1002`/`:1094`, the 429 with `Retry-After: 1` is at `:1090`, and the demo's 400 was probed.
- A17 still holds. Scores 50..1 are never 0.
- A18 still holds. 0 pool docs are missing in either pool.
- A19 still holds. The MDEs at `GATE-embedding-migration.md` are 0.0323–0.0377, and `report-output-repeat.txt:22,44` shows 0.0525 and 0.0544.
- A20 still holds. The ranked-changes doc §0 has one row per `docs/plans/*GATE*.md` (`:28`).
- A21 still holds. There is no `*jev*` in `scripts/`.
- A22 still held when written. `main` is now 1 commit ahead of `origin/main`, the spec commit itself (F15, dropped).
- A23 still holds. The HF API `safetensors.total` values are 4,659,865,088 / 11,959,730,224 / 25,805,936,206 / 27,781,427,952 / 35,951,822,704.

**Span check. There are three uncovered dependencies:**
- **Pod GPU memory beyond weights.** The KV cache of a shared prefix of about 24k–52k tokens, copied per suffix batch, is covered by no assumption. A23 sizes weights only. This cannot be verified in-round, so it goes to §3.1.
- **The demo admits 50-question NFCorpus bodies.** A15's probe covered SciFact q1 only. This was verified in-round: the largest NFCorpus body returned 200 on all 5 demo models (F5).
- **hf_server can serve the five hybrid-attention models.** Its prefix path requires `reorder_cache`, which raises 422 otherwise (`hf_server.py:497-502`). This was verified in-round at read tier: `hf-server/README.md:123-129` reports hf_server evaluation scores for all five, and `eval/prompt_search.py:114` launches `hf-server/hf_server.py`.

## 2. Literal-wrongness findings

### 2.1 `--max-model-len 16384` rejects every gate request, because each branch carries all 50 documents

**Description.** §6.2 launches hf_server with `--max-model-len 16384`. hf_server's v1 prompt builder puts every question's `instructions` into the shared prefix, and here that means the full text of all 50 pool documents (`common/prompt_builder.py:177-183`, joined into each branch's system message at `hf_server.py:272`). It then repeats the selected question's instructions twice in the branch body (`prompt_builder.py:257-263`). Each complete branch must fit `--max-model-len`, or compilation raises `ValueError` (`hf_server.py:351-354`), which the route returns as HTTP 422 (`:1093-1094`). §5 classes 422 as not retryable, so the query falls back to baseline at once. The consequences:

- All 20 transfer queries fall back.
- All 300 main-pass queries fall back, which is far over the 15-query cap, so the gate is refused.
- All 50 repeat-pass queries fall back.

All of this happens after the pod is rented. The §6.1 stub does not tokenize, so the dry run cannot catch it. A15's evidence was a single-document request ("10,026 tokens"), which is the wrong unit.

**Evidence** (the claim is run-tier, and the probe goes through hf_server's own compiler):
- [totality] `renderall.py` runs `hf_server.PromptCompiler(..., prompt_policy='shared_examples_binary').compile(req, render_only=True)` on the exact §4 body for all 300 SciFact pools. The shortest branch per query ranges from 69,731 chars (min) through 87,722 (median) to 131,289 (max), and the longest branch overall is 150,319 chars (query 129). For the first 20 NFCorpus pools, the shortest branch is at least 83,493 chars.
- [presence] A dump of SciFact q1's rendered branch (`render.py`, both `shared_*` policies) shows an 81,594-char system message, and the substring test is True for both doc 0's and doc 49's text inside it. Every branch, whichever doc it asks about, carries all 50 documents.
- [compat] Tokens per char were measured by pushing two real bodies through the demo endpoint: one question holding doc 0, and one question holding docs 0–9 concatenated. hf_server's render of the two grows from 9,038 to 43,907 chars (+34,869), which confirms three copies of the instruction per branch. Over the same pair, `usage.input_tokens` rises by +12,033 on Qwen3.5-4B (2.90 chars/token) and by +11,436 on Gemma-4-26B-A4B (3.05 chars/token).
- [totality] Arithmetic over all 300 SciFact queries: for the shortest branch of all (69,731 chars) to fit 16,384 tokens, the tokenizer would need at least 69,731 / 16,384 = 4.26 chars/token. The measured 2.90–3.05 is far below that, so 300/300 queries exceed the limit.

**Proposed fix.**
1. In §6.2, replace `--max-model-len 16384` with `--max-model-len 65536`. The longest SciFact branch is 150,319 rendered chars, about 51.8k tokens at 2.90 chars/token or 49.3k at 3.05, before chat-template tokens. Keep `--max-batch-tokens` at its default of 32768. The largest branch suffix (the part after the shared prefix, which must fit that budget per `hf_server.py:517-518`) is 21,085 chars, about 7.3k tokens at 2.90 chars/token, so it stays well inside.
2. Rewrite A15 as: "Each hf_server branch carries the shared prefix listing all 50 instructions, plus the selected instruction twice (`prompt_builder.py:177-183,257-263`). The longest SciFact branch is 150,319 rendered chars (query 129), about 52k tokens; it fits 65,536, and every model's `max_position_embeddings` is 262,144."
3. Add a pod pre-flight to §6.3, before pass 1: send the query-129 request and require HTTP 200. This also serves §3.1.

Evidence:
- [existence] HF `config.json` (curl, all five repos): `max_position_embeddings` is 262144 for Qwen3.5-4B, Qwen3.8-27B, Qwen3.6-35B-A3B, gemma-4-12B-it and gemma-4-26B-A4B-it.
- [totality] 150,319 is the maximum of `maxbranch.json` over all 300 SciFact queries.
- UNVERIFIED: that the pod's HF tokenizers match the 2.90 and 3.05 chars/token measured through the demo's `featherless-ai/*-classifier` endpoints. The query-129 pre-flight discharges this.
- [totality] Largest suffix, verified in-round with `suffix.py` in the probe scratch. hf_server's compiler rendered every SciFact and NFCorpus pool (623 queries) under both `shared_*` policies. For each query, the script measured the JSON-escaped branch length beyond the common prefix. The maximum is 21,085 chars (SciFact 1282, `shared_examples_binary`), and the escaping over-counts, so this is an upper bound. Exceeding 32,768 tokens would need fewer than 0.643 chars/token, against the 2.90–3.05 measured on the demo. The margin holds under any plausible tokenizer.

## 3. Forced decisions

### 3.1 Pod GPU memory for the shared-prefix KV copies is unsized

**The choice.** The spec does not say how to size and verify pod memory once branches are 24k–52k tokens long, which they must be after §2.1.

**Why it is forced.** §6.2 sizes the card to bf16 **weights** only: an 80 GB card for Qwen3.8-27B at about 56 GB, and for Gemma-26B-A4B at about 52 GB. HFBackend does the following:

- It evaluates the shared prefix once.
- For every suffix batch, it runs `copy.deepcopy(cache)` and then `reorder_cache(torch.zeros(len(batch)))` to broadcast the seed to `min(--max-batch-size 32, --max-batch-tokens 32768 // suffix width)` rows (`hf_server.py:533-542`).
- Its loader docstring says `max_batch_tokens` limits "padded suffix tokens per batch, not shared-prefix prefill or total KV memory" (`:1136-1137`).
- `hf-server/README.md:97` says "Setting a larger token cap does not … guarantee sufficient memory."

A read-tier estimate of full-attention KV in bf16, per token:

| Model | Full-attention layers × KV size | Per token |
|---|---|---|
| Qwen3.8-27B | 16 × 2 × 4 × 256 × 2 B | 64 KiB, so about 3.3 GB per cache row at query 129's ~52k tokens |
| Gemma-26B-A4B | 5 × 2 × 2 × 512 × 2 B | 20 KiB, plus 25 sliding layers at a 1,024-token window |

These layer counts and head dimensions come from the HF configs.

If the broadcast materializes rows, which is not verified in-round, 25–32 rows of the 27B come to roughly 80–105 GB on top of the weights. An OOM is a `RuntimeError`, which the route does not catch (`:1089-1094`). It surfaces as a 500, is retried 4 times and falls back, and then the 15-fallback cap refuses the run after paid pod time.

For the 4B winner (about 9 GB of weights), all three options below coincide.

The same query-129 pre-flight also discharges §2.1's one residual UNVERIFIED claim: that the pod's HF tokenizers match the 2.90–3.05 chars/token measured through the demo. Options (a) and (b) include that pre-flight. Option (c) leaves the claim open, and a mismatch would show up only as 422 fallbacks.

**Options.**
- **(a)** Keep the §6.2 cards and add a pre-flight before pass 1. Send the query-129 request with `ENABLE_OPEN_JEV_ADVANCED_METRICS=1`. On a 500 or OOM, restart with a lower `--max-batch-size` until it returns 200, and record the value in the sidecar's `reranker` block. The trade-off is lower throughput, because fewer rows per batch means more forwards per request.
  Evidence: [existence] `--max-batch-size` exists with default 32 (`hf_server.py:1326`) and caps rows (`:533`). UNVERIFIED: that some batch size fits the 27B on 80 GB. The seed cache plus its deepcopy is about 6.6 GB at batch 1 by the estimate above.
- **(b)** Size the card for weights plus KV at the longest branch, for example an H200 (141 GB) for the 27B and Gemma-26B winners, and still run the (a) pre-flight. The trade-off is a higher hourly rate for higher throughput.
  Evidence: UNVERIFIED. The estimate is about 56 + 105 GB for the 27B at the default batch of 32, so even an H200 may need a lower `--max-batch-size`.
- **(c)** Accept the risk as specified, with no pre-flight. The trade-off is no setup cost, against the risk of a wasted rental that ends in a refused gate.
  Evidence: [existence] An OOM is a 500, not a 422, so it is retried and then counted as a fallback (R3). The 15-fallback cap refuses the run (§5).

**Why no option dominates.** Option (b) is (a) on a bigger card. It trades hourly rate for throughput, and the ratio between the two rental prices and the throughput lost at a smaller batch size is unmeasured, so neither (a) nor (b) is cheaper in general. Option (c) is the only one with no up-front cost. Any hybrid of (a) and (b) is (b).

## 5. Recommendation

🛑 **Surface forced decisions to user.** §3.1 needs a choice of pod memory strategy, and §2.1's `--max-model-len` fix must also be applied: as specified, every gate request is rejected with 422.

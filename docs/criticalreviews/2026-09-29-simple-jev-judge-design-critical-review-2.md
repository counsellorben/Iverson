# Critical Design Review: 2026-09-29-simple-jev-judge-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-29-simple-jev-judge-design.md`
**Artifact HEAD at review:** 3c5ae9174da8a57da33518ce33d3fb15d6c377bc
**Verified Assumptions section:** present

Probe scratch (not in the repo): `/tmp/claude-1000/-home-ben-repositories-Iverson/8ea909e2-a873-4454-bbef-4ce0a5c26326/scratchpad/cdr2/`. Round 1's `scratchpad/cdr/` no longer exists, so nothing below cites it as present. Where a round-1 result was needed, it was re-probed. The probe files and their md5s:

| File | md5 | What it does |
|---|---|---|
| `baseline.py` / `baseline.out` | `94f5ae84…` / `9288b9c4…` | Uses `ir_measures` 0.4.3 to score the identity, reversed and relevant-first-oracle orderings of both pools, written the §4 way (`51 − position`), and checks file order against score order. |
| `sig.py` / `sig.out` | `9e5bc92d…` / `42589144…` | Runs the code's own `resolve_prompt_policy` over the five HF `config.json` files fetched at the pinned revisions. |
| `render.py` / `render.out` | `b71de3ce…` / `52bdce74…` | Runs hf_server's `PromptCompiler.compile(render_only=True)` over every SciFact and NFCorpus pool, with `fastapi` stubbed. It reports the maximum branch, the suffix and the union chars. |
| `sim.py` / `sim.out` | `1b181c78…` / `03df15fe…` | Replays `HFBackend._score`'s batch schedule (`hf_server.py:516-534`) over every SciFact query, as described below. |
| `sub.py` / `sub.out` | `616db280…` / `dabe2384…` | Checks `select_subsample`'s output against the SciFact pool and its reproducibility across input order and `PYTHONHASHSEED`, and scores the qrels-restriction precedent. |
| `nfdist.py` / `nf-maxbranch.json` | `abab6421…` / `cee8b10c…` | Gives the distribution of maximum-branch sizes over the 323 NFCorpus queries. |
| `body-129-gemma-4-26B-A4B-classifier.json` → `resp-129-gemma26.json` | `7d96e8c7…` → `9c34ac99…` | Demo probe 1. |
| `body-PLAIN-478-gemma26.json` → `resp-PLAIN-478-gemma26.json` | `c4279d18…` → `9f06e7d9…` | Demo probe 2. |

`sim.py` works on a per-branch prefix and suffix measured in rendered chars and converted to tokens at an assumed chars/token ratio (2.67, 3.0 or 4.0). It sorts suffixes longest first and forms batches of `min(B, 32768 // width)` rows. The peak of `rows × (prefix + width)` is the number of full-attention KV row-tokens that are resident. Its limitations: it counts only full-attention KV, it ignores chat-template tokens, and its chars/token ratio is assumed, not tokenized.

The demo was sent two requests, one at a time, using public SciFact/NFCorpus data with `User-Agent: curl/8.5.0`.

## 0. Coverage enumeration

The enumeration below was built before round 1's review was read.

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | §1 The question | ok. [existence] Signature read: `IResultReranker.Rerank(float[] queryVector, IReadOnlyList<RerankCandidate>)` (`Iverson.Vector/IResultReranker.cs:3-15`). `RerankCandidate` has `Id`, `BaseScore`, `Centroid`, `Decay` and `Popularity`, and no text field. |
| S2 | §2 Rulings | ok. [existence] Spec read, each ruling traced to the section that honours it: the screen runs on the demo with NFCorpus (§5, spec lines 113–131); the gate self-hosts `hf_server` on the SciFact bge-base pool (§6.2–§6.3, lines 170–228); pointwise `noul`, with `score` in the screen only (§5 arm 6, line 124); R@10/P@10 from one recorded `ir_measures` command (§7, lines 245–247); `report.py` reused unchanged (§9, line 277). |
| S3 | §3 Inputs and the baseline/ceiling table | ok. [totality] `md5sum` of the four files matches the table (`e51bf248…`, `f7572e6a…`, `b167cf42…`, `afd2c240…`). `baseline.out` covers all 300 SciFact queries: identity nDCG@10 0.7452, R@50 0.9337, AP 0.7018, R@10 0.8770, P@10 0.0993; oracle nDCG@10 0.9345, R@10 0.9337; reversed 0.0011 / 0.0212 / 0.0033 / 0.0003. 107 queries have oracle > identity. Every figure in the §3 table reproduces. |
| S4 | §4 Judge request, acceptance, ordering | ok. [compat] The §4 body was pushed through both consumers. On the demo, PLAIN-478 with 50 × `noul` on Gemma-4-26B-A4B returned 200 with 50 answers (`resp-PLAIN-478-gemma26.json`). hf_server's own compiler compiled all 623 pool bodies under both `shared_*` policies without error (`render.out`). Acceptance, ordering and ties are covered in R1 and R2. |
| S5 | §5 Stage A (screen) | ok. [compat] Probe 2 pushed the largest NFCorpus body (PLAIN-478, 50 × `noul`) through the demo endpoint §5 names: HTTP 200, 50 answers (`resp-PLAIN-478-gemma26.json`, md5 `9f06e7d9…`). This re-confirms A24. [totality] `nf-maxbranch.json` shows every other NFCorpus body is smaller (F5). The transport, cap and selection rules are dispositioned per direction in R3–R5. |
| S6 | §6.1 Dry run | ok. [totality] `baseline.out` over all 300 SciFact queries gives the identity ordering 0.7452 / R@50 0.9337 and the reversed ordering 0.0011 / 0.9337, the exact §6.1 targets. `check_pool`'s file-order comparison (`report.py:721-766`) makes identity exit non-zero and reversed exit 0 (R11). [totality] 0 duplicate doc texts and 0 duplicate query texts across the 300 SciFact pools, so the stub's doc-text guard can locate the pool and discriminate positions (R12). |
| S7 | §6.2 Pod | ok. [bidirectional] Policy pin: `sig.out` shows all five pinned configs resolve to the policies §6.2 lists and all five `KNOWN_PROFILES` rows are matched (R9). [existence] Repos and revisions resolve on the HF API, all ungated (F13). The KV table arithmetic reproduces from the `config.json` values (M3). [totality] `sim.out` over all 300 SciFact queries shows the row count is capped at 18–26 by `--max-batch-tokens`, so the "+ 32 rows" column is an upper bound (F14). [totality] `render.out`: `--max-model-len 65536` admits every pod request (R13, M4). |
| S8 | §6.3 Passes | → §2.1 (the sidecar `reranker` block). The section's other parts have their own rows: the pre-flight in R15 and R16, the repeat pass in R8 and R10, and the transfer check in F15. |
| S9 | §7 Verdict | ok. [existence] Code read: `--pair` enforces the set and ≥ 25% sequence checks (`report.py:700`, `:732-766`). The permutation test is two-sided with a seeded sign flip (`:538-546`), and a one-pair family gives Holm p_adj = p. The R@50 precondition follows from pool invariance. |
| S10 | §8 PASS/FAIL scope | ok. [presence] Scope text, not load-bearing. The recorded listwise `choice` 400 ("maximum context length is 32768") matches the error body persisted by this round's demo probe 1 (`resp-129-gemma26.json`: the same 32,768 limit on Gemma-26B). |
| S11 | §9 Components | → §2.1. The CLI is the only writer of the sidecar, but it has no input for five of the `reranker`-block fields that §6.3 requires. [existence] The reused helpers exist at the cited lines: `load_run :75`, `load_corpus :117`, `load_queries :122`, `select_subsample :140`, `read_responses_ledger :214`, `accepted_entry :253`, `append_response :264`, `make_record :272`, `RUN_TAG :55`. There is no `*jev*` in `scripts/`. |
| S12 | §10 Verified assumptions | → §2.1. The span check found one uncovered dependency (the sidecar `reranker` block's field sources), which became §2.1. The 25 listed items are each reconfirmed in §1 with their evidence. |
| S13 | §11 Known issues | ok. [presence] These are accepted risks, and the persisted demo responses agree with them. `resp-PLAIN-478-gemma26.json` carries `usage` `{input_tokens: 136410, output_tokens: 50}`, where hf_server always reports 0 output tokens (`hf_server.py:608`, `:943`). `resp-129-gemma26.json` carries a vLLM-style `BadRequestError` with "maximum context length is 32768 tokens". Both show the demo is not `hf_server`, as §11 states. |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | Response acceptance (every `d<i>` present; `noul` ∈ [0,1]; `score` ∈ [0,3]) | **over:** ok. [existence] Code read: binary `noul` = `probabilities['yes']` (`hf_prompt_policies.py:182-186`), and `probabilities` survives public filtering (`PUBLIC_FIELDS`, `response_scoring.py:66-74`). `score` is the expected level index over digit labels 0..3 (`prompt_builder.py:207-213`, `response_scoring.py:310-325`). **under:** ok. [presence] Key set and value dump of a real demo response (`resp-PLAIN-478-gemma26.json`, md5 `9f06e7d9…`): 50/50 `d<i>` keys, each with keys `{type, noul}`, and `noul` range 0.010006–0.989986, inside [0,1]. So the acceptance bounds admit a valid response. |
| R2 | Ordering: stable descending sort, ties keep baseline order, synthetic `51 − position` | **over:** ok. [totality] `baseline.out`: the SciFact identity written the §4 way reproduces the raw run's scores on all 5 measures, although 7 queries carry tied raw scores. **under:** ok. [totality] File order equals score order and the rank column in both pools (0 not score-sorted, 0 rank≠file). So "baseline order" is defined for every query. |
| R3 | Retry classes (429/5xx/timeout/rejected retry; 400/422 fall back at once; 403 aborts) | **over:** ok. [existence] Code read: hf_server maps only `OverloadedError` to 429, and `ValidationError`/`ValueError` to 422 (`hf_server.py:1089-1094`). A CUDA OOM (`RuntimeError`) escapes as a 500 and is retried, as §6.3's pre-flight assumes. **under:** ok. [compat] Probe 1 pushed a real over-limit body (SciFact query 129) through the demo: HTTP 400, `type` `BadRequestError`, `code` 400 (`resp-129-gemma26.json`). The spec classes 400 as not retryable, so it falls back at once and is logged, rather than being retried 4 times. |
| R4 | Fallback cap (> 16/323 makes an arm invalid; > 15/300 refuses the gate) | **over (a run above 5% passes):** ok. [existence] Arithmetic: the strict "more than" bounds admit at most 16/323 = 4.95% and 15/300 = 5.00%, so nothing above 5% passes. **under (a run at or below 5% is refused):** ok. [existence] Arithmetic: 16 of 323 and 15 of 300 are the largest counts at or below 5%, and both are admitted. |
| R5 | Selection (highest valid-arm NFCorpus nDCG@10; exact ties → earlier arm) | **over:** ok. [existence] Rule text: only valid arms are eligible. **under:** ok. [compat] A24 was re-probed this round (§1), and the largest NFCorpus body is accepted. |
| R6 | Gate: p < 0.05 and delta ≥ +0.047 | **over (false PASS):** ok. [existence] The permutation test is two-sided, and the delta bound blocks a negative or small effect (`report.py:538-546`). **under (false FAIL):** ok. [existence] The threshold lies within the cited MDE range 0.032–0.054 (A19, reconfirmed in §1). |
| R7 | Ledger identity: resume keys on the accepted entry per `query_id`; one ledger per pass | **over-merge:** ok. [presence] The first line of a real ledger (`teacher-ceiling-2026-09/main.responses.jsonl`) has the keys `completion_tokens, content, elapsed_s, order, pass, prefix_len, prompt_tokens, query_id, reason, reasoning, status`, with `query_id` `1012`, `status` `rejected`, and `pass` stamped. Records are keyed per file and per `query_id` (`teacher_rerank.py:246-249`), and each pass writes its own file. **under-merge:** ok. [existence] Code read: `str(query_id)` on both the write side (`:275`) and the read side (`:248`). |
| R8 | Repeat pass scored against qrels restricted to its 50 ids | **over (the restricted qrels admit a query outside the 50):** ok. [totality] `sub.out`: the precedent `qrels-sub50.trec` holds exactly 50 distinct query ids (65 rows), and `iter_calc` of the full 300-query bge-base run against it yields exactly 50 values. A file built the same way from `select_subsample`'s 50 ids contains only those ids. **under (unrestricted qrels count the 250 absent queries as zeros):** ok. [totality] `sub.out`: `repeat-2.chunks.trec` (50 queries) scores nDCG@10 0.1321 against the full qrels and 0.7927 against `qrels-sub50.trec`. So the restriction the spec requires is necessary and sufficient. |
| R9 | Policy pin from the code's `KNOWN_PROFILES` | **over (a model pinned to the wrong policy):** ok. [bidirectional] `sig.out` runs the code's own `resolve_prompt_policy` over each pinned-revision `config.json`: Qwen3.5-4B → `shared_examples_binary`; Qwen3.8-27B → `shared_examples_binary`; Qwen3.6-35B-A3B → `shared_repeat_state`; gemma-4-12B-it → `shared_repeat_state`; gemma-4-26B-A4B-it → `shared_examples_binary`. All five resolve in mode `architecture-size`, exactly as §6.2 states. **under (a screen model with no profile):** ok. [bidirectional] All five `KNOWN_PROFILES` rows are matched, one per arm, and none resolves to `unknown-baseline`. |
| R10 | `select_subsample(ids, 50, 20260929)` | **over (selects an id outside the pool, or one id twice):** ok. [totality] `sub.out`: over the 300 SciFact pool ids, it returns 50 ids, 50 distinct, 0 outside the pool. **under (the selection is not reproducible, so the repeat pass cannot be reconstructed):** ok. [totality] `sub.out`: the result is identical whether the ids are passed in pool order or reversed, and identical under `PYTHONHASHSEED=1` and `=2` (the outputs are byte-identical, md5 `dabe2384…`). This is because it sorts before sampling (`teacher_rerank.py:140-146`). |
| R11 | Two-sided dry run (identity → `--pair` exits non-zero at 0.7452; reversed → exits 0 at 0.0011) | **over (a broken client passes):** ok. [totality] `baseline.out` gives reversed 0.0011 and identity 0.7452 over all 300 queries. A client that discards replies reads 0.7452 in reversed mode and fails. **under (a correct client fails):** ok. [existence] Code read: `check_pool` compares FILE-order sequences (`report.py:721-729`). The identity output is in `load_run` file order, so 0 of 300 queries are reordered and `sys.exit` runs (`:760-766`). `main` prints the scores before `run_pair_statistics` (`:915-928`). |
| R12 | Stub guard: 422 unless the instructions contain the text of the doc at that position | **over (a defective prompt accepted):** ok. [totality] 0 of the 300 SciFact pools contain two docs with identical `text`, so a swapped position always fails the substring test. **under (a correct prompt rejected, or the stub unable to find the pool):** ok. [totality] The stub must map `state` back to a query to find its pool. 0 of the 300 SciFact pool queries share a query text, so the reverse lookup is unambiguous. Every pool doc resolves (A18). |
| R13 | hf_server branch limit `--max-model-len 65536` × pod request populations (pre-flight, transfer 20, main 300, repeat 50) | **under (valid requests rejected):** ok. [totality] `render.out` gives a SciFact maximum branch of 150,320 chars (query 129; the extra char is the probe's own separator) and an NFCorpus maximum of 133,986 (PLAIN-478). Every pod request is at or below query 129. A 422 would need fewer than 150,320 / 65,536 = 2.29 chars/token. The densest reading any observation supports is 2.67 (M8). **over (silent truncation):** ok. [existence] Code read: the whole prompt is encoded and refused on length (`hf_server.py:350-354`). Nothing is truncated. |
| R14 | Suffix batch-token limit (`max(lengths) > max_batch_tokens` → 422, `hf_server.py:517-518`) × the same populations | **under:** ok. [totality] `render.out` gives a maximum per-branch suffix beyond the common prefix of 21,014 chars (SciFact) and 20,940 (NFCorpus). Exceeding the default 32,768 would need fewer than 0.65 chars/token. **over (an over-wide suffix admitted):** ok. [existence] Code read: `hf_server.py:517-518` raises `ValueError` (→ 422) whenever `max(lengths) > max_batch_tokens`. There is no truncation path. |
| R15 | Pre-flight branches (500/OOM → halve `--max-batch-size` and resend; 422 → stop) | **over (a correct setup stopped):** dropped. [existence] Code read: a 422 can also come from non-length `ValueError`s: the `reorder_cache` guard at `:510-513` and label stability at `:365-368`. The spec's diagnosis text ("denser tokenizer") would then be wrong, but the action (stop) is right, so the asked-for behavior is unaffected. **under (a failing setup not caught):** dropped. [existence] Code read: the halving loop has no floor, and an OOM in the unchunked prefix prefill is not bounded by `--max-batch-size` (`hf_server.py:446-448`, `hf-server/README.md:212`). That would need a single 48k-token prefill to exhaust an H200 after the weights, which is speculative against the §6.2 sizes, so it fails literal-wrongness. |
| R16 | Pre-flight representativeness (does query 129 exercise the worst memory case?) | **over (the pre-flight demands more than the passes need):** ok. [totality] `sim.out` over all 300 SciFact queries: query 129's peak is never above the population maximum. It is the maximum in 8 of 9 (B, density) settings, and 3.8% below it in the ninth. So a batch size that fails on query 129 could not have fitted the worst pass query anyway. **under (a pass query needs more than the pre-flight proved):** dropped. [totality] `sim.out` over all 300 SciFact queries: at `B` = 16 and 8, query 129 has the highest peak at every density tried (2.67, 3.0, 4.0). At `B` = 32 it is the highest at 2.67 and 4.0. At 3.0, 15 queries exceed it, by at most 3.8% (query 589, 839k vs 808k row-tokens). This does not change the outcome. At `B` = 32 and 64 KiB/token, the 27B's resident KV is about 53–59 GB, or about 112–118 GB with weights and seed, on a 141 GB H200. The worst other query adds about 2 GB. An OOM would also be loud (a 500, then the fallback cap), not silent. |
| R17 | Client timeout of 300 s on the pod | **over (a request that would have succeeded is timed out and falls back):** dropped. [existence] Latency on the pod was not measured, and the demo's 27B took 13.6 s per request (§5). Firing needs a single pod request of more than 300 s, which is speculative. A spurious fallback is also counted by the ≤ 15 cap, which refuses loudly. **under (a hung request is never abandoned):** ok. [existence] Rule text: a timeout is retryable up to 4 attempts in total, then the query falls back and is counted (spec §5, lines 136–145). hf_server cancels on client disconnect (`hf_server.py:1083-1087`). |

### Data-flow arrows

| # | Arrow → consuming operation | Disposition |
|---|---|---|
| F1 | Pool `.trec` → `load_run` → position `i` for `d<i>` (screen, stub, pod) | ok. [totality] 300 × 50 and 323 × 50, file order = rank order (`baseline.out`). |
| F2 | `corpus.jsonl` `text` → question `instructions` | ok. [totality] All 623 bodies compiled from `corpus[d][1]` with 0 lookup failures (`render.out`). A10 covers the title prefix. |
| F3 | `queries.jsonl` → `state`, and the stub's reverse lookup `state` → pool | ok. [totality] Every pool query resolves, with 0 duplicate query texts in either pool (R12). |
| F4 | §4 request → hf_server `PromptCompiler.compile` → length and suffix checks (pre-flight, transfer, main, repeat) | ok. [totality] `render.out` compiled every pod-population body (all 300 SciFact queries and all 323 NFCorpus queries, including the 20 transfer queries) through hf_server's own compiler. The maximum branch is 150,320 chars, which fits 65,536 above 2.29 chars/token. The maximum suffix is 21,014 chars, which fits 32,768 above 0.65 chars/token (R13, R14). |
| F5 | §4 request → demo `/v1/classifier` (screen, NFCorpus only) | ok. [compat] Probe 2: PLAIN-478 → 200, 50 answers, `usage.input_tokens` 136,410, the exact figure A24 cites. [totality] `nf-maxbranch.json`: PLAIN-478 (133,986 chars) is an outlier. The next-largest body is 125,742 (−6.2%), and 18 bodies are at or above 90% of the maximum. A body that failed would fall back and count toward the ≤ 16 cap, which is loud. Probe 1 shows that SciFact query 129 is **rejected** by the demo (400, "maximum context length is 32768 tokens"), but the demo never receives SciFact in this design (§5 uses NFCorpus only), so this is ok. |
| F6 | Response → acceptance → ordering → run file (persisted) → `ir_measures`/`report.py` | ok. [compat] The §4-style run written by `baseline.py` was scored by `ir_measures` and reproduces the raw run (R2). |
| F7 | Sidecar (persisted) `composite` → `report.py load_build_composite` | ok. [presence] `scifact…/runs/bge-base.meta.json` and `nfcorpus…/runs/bge-base.meta.json` both hold `"composite": "7d3a15092f963723"`. `report.py:184-202` reads only `composite`. |
| F8 | `jev_rerank.py` `write_sidecar` ← its sources for the `reranker` block (§6.3 pre-flight and main pass; the same writer is used for the transfer, repeat and screen passes) | → §2.1. |
| F9 | Ledger write → resume read (persisted) | ok. [presence] A real ledger line (`teacher-ceiling-2026-09/main.responses.jsonl`, line 1) carries `query_id` and `status` (plus `pass`), the two fields the resume read keys on: `read_responses_ledger` groups by `str(record["query_id"])` (`teacher_rerank.py:246-249`) and `accepted_entry` filters `status == "accepted"` (`:253-261`). See R7 for both directions. |
| F10 | Run files → `report.py --pair` gate | ok. [totality] `baseline.out`: a run written the §4 way (`51 − position`, file order) over all 300 SciFact queries was scored by `ir_measures`, the same scorer `report.py` calls, and reproduces the raw run on all 5 measures. [existence] Code read: `--pair` runs `check_pool` (`report.py:732-766`) and then the two-sided permutation test (`:538-546`) (S9, R11). |
| F11 | Run files → the recorded `ir_measures.calc_aggregate([nDCG@10, R@50, AP, R@10, P@10], …)` | ok. [compat] The same measure-object call ran in `baseline.py` on both pools. |
| F12 | Fallback ids → worst-case re-score | ok. [existence] The ids are computed by the client itself, and §6.3 puts them in the sidecar. |
| F13 | Screen winner → pod `--model <HF repo>` / `--revision` / policy | ok. [existence] The HF API resolves `Qwen/Qwen3.5-4B` `851bf6e806ef…`, `Qwen/Qwen3.8-27B` `1d4bf0f2ff60…`, `Qwen/Qwen3.6-35B-A3B` `995ad96eacd9…`, `google/gemma-4-12B-it` `707f0a3b8a3c…` and `google/gemma-4-26B-A4B-it` `4d7ae4984b7d…`, all `gated: False`. The demo ids (`featherless-ai/*-classifier`) do not resolve on HF, but §6.2 names the HF repos by revision, so the mapping is explicit. Policy: R9. |
| F14 | Model plus copied prefix KV → pod GPU memory | ok. [totality] `sim.out`: at `B` = 32, query 129 never forms more than 18–26 rows, because `--max-batch-tokens` 32768 caps rows at `32768 // width` (`hf_server.py:533`). So the §6.2 "Weights + 32 rows" column is an upper bound, which errs in the safe direction for card choice. |
| F15 | Demo ledger → transfer check (Spearman ρ, mean absolute difference) | ok. [existence] Spec read: the transfer check is "reported, not blocking" (§6.3 item 1, line 215). Round 1 dropped its ledger-field candidate (its F9), and it is not re-raised. |

### Round-2 mandatory rows (fix neighborhoods, intersected fix texts, amendments)

| # | Row | Disposition |
|---|---|---|
| M1 | (a) Header "25 assumptions … (A24–A25 added by CDR-1's span check)" | ok. [totality] `awk` over the spec's §10 table lists exactly the ids A1 … A25 (25 rows, none missing, none extra). |
| M2 | (a) §6.2 GPU bullet: "broadcasts it to each suffix batch of up to `--max-batch-size` rows" and "no flag bounds that memory (`:1136-1137`)" | dropped. [existence] Code read: the actual row count is `min(--max-batch-size, --max-batch-tokens // width)` (`hf_server.py:533`), so both flags bound the copied rows, which is the lever the spec's own pre-flight turns. As an upper bound the sentence is true, and no decision flows from the imprecision (F14). |
| M3 | (a) §6.2 table arithmetic, and "expect its pre-flight to settle at `--max-batch-size` 16 or lower" | **Arithmetic:** ok. [existence] The `config.json` values in `sig.out` give full-attention layers × KV heads × head dim of 8×4×256, 8×1×512, 5×2×512, 16×4×256 and 10×2×256, which is 32/16/20/64/20 KiB per token in bf16. At 52k tokens that is 1.7/0.85/1.06/3.4/1.06 GB per row. Weights + 32 rows = 63/51/86/165/106 GB. Every cell reproduces. **Expectation:** dropped. [totality] `sim.out` suggests the 27B probably passes at 32 (R16), but the text is labelled an estimate that "the §6.3 pre-flight decides", and the H200 choice stands either way. |
| M4 | (a) §6.2 server line `--max-model-len 65536` | ok. [totality] `render.out` over all 623 pool bodies: the maximum branch is 150,320 chars (query 129), which fits 65,536 tokens at any density above 2.29 chars/token. The densest supported reading is 2.67 (M8). Both directions are in R13. |
| M5 | (a) §6.3 pre-flight restatements: "150,319 rendered chars, ≈52k tokens"; "largest NFCorpus branch is ≈130K chars" | ok. [totality] `render.out` gives 150,320 (the extra char is the probe's own separator) and 133,986. "≈130K" understates by 3%, which is not load-bearing because query 129 is the larger. The branch logic is in R15 and R16. |
| M6 | (a) §6.3 item 2: the fix added "`--max-batch-size` and `--max-model-len` as run" to the sidecar, and item 0 added "the pre-flight's `usage.input_tokens`" | → §2.1. |
| M7 | (a) Restatement sweep for the round-1 fix values (`16384`, `16,384`, `80 GB`, `max-model-len`, `max-batch-size`, `2.90`) | ok. [totality] `grep` over the spec: no stale `16384` or `16,384` survives. `80 GB` appears only in the new table's card column (the 4B and 12B rows). `2.90–3.05` appears at line 211 and in A15, and both are consistent with M8's reading. |
| M8 | (b) Round 1's §2.1 evidence measured "2.90–3.05 chars/token" through the demo, assuming the demo renders three copies per question as hf_server does. This intersects this round's demo probes. | dropped. [compat] Probe 1 shows Gemma-26B's demo context is 32,768 tokens per prompt. Probe 2 shows the PLAIN-478 body (hf_server branch 133,986 chars) is accepted there. At 2.90–3.05 chars/token that branch would be 43.9k–46.2k tokens, which the demo would have to reject. So the demo does not render hf_server-shaped full-pool branches, and the Δ-probe's density is not a confirmed tokenizer density. Every consistent reading still fits `--max-model-len 65536`. Probe 2's union reading (364,199 union chars in `render.out` / 136,410 tokens) gives 2.67 chars/token, so query 129 is about 56.3k tokens. The pass/fail pair implies more than 4.09 chars/token if the demo's branches are full-pool, so query 129 is below 36.8k. The 422 branch of the pre-flight backstops either case. No literal wrongness. |
| M9 | (b) Round 1's §3.1 fix text ("25–32 rows" of the 27B) × this round's batch replay | ok. [totality] `sim.out` replays the batch schedule over all 300 SciFact queries at B = 32. Query 129's batches are `[4, 10, 15, 18, 3]` rows at 2.67 chars/token, `[4, 12, 18, 16]` at 3.0 and `[6, 18, 26]` at 4.0, so its largest batch is 18–26 rows. Round 1's "25–32 rows" overlaps that only at 25–26, and the §6.2 "+ 32 rows" column is an upper bound (F14). No decision changes: the H200 choice and the pre-flight stand. |
| M10 | (c) Amendment anchoring | ok. [existence] Round 1's anchor is SHA `861a8ef9…`. `git rev-parse 861a8ef9:<spec>` = `384531ec…` and `git hash-object <spec>` = `d1813026…`, so they differ. The forward window `git log 861a8ef9..HEAD -- <spec>` holds one commit, `3c5ae917`, "applied 4 fixes from …critical-review-1…", which is in-band. The reverse window is empty (`861a8ef9` is an ancestor of HEAD). The working tree is clean. So every hunk of `git diff 861a8ef9 -- <spec>` is a fix hunk, reviewed in M1–M9 and §1 (A15, A23–A25). There are no out-of-band amendments. |

**Totals: 55 rows. 44 ok; 5 → findings (S8, S11, S12, F8, M6, all → §2.1); 6 dropped (R15, R16, R17, M2, M3, M8).** R16, R17 and M3 each also carry an ok direction or part, but each is counted once, as dropped.

## 1. Verified-assumptions cross-check

- A1 still holds. The helpers are at `teacher_rerank.py:75-127,140-148`, and `load_corpus` returns `(title, text)` (`:117-119`).
- A2 still holds. The imports at `:33-41` are stdlib only, and the `__main__` guard is at `:649`.
- A3 still holds. See `accepted_entry` `:253-261` and `make_record` `:272-284`.
- A4 still holds. See `RUN_TAG` `:55`, and `write_run` `:490-505` writes it at `:502`.
- A5 still holds. See `report.py:700` and `:732-766`, and `--pair` at `:852`.
- A6 still holds. See `load_build_composite` `:184-202`, and both `bge-base.meta.json` files carry `7d3a15092f963723`.
- A7 still holds. `GATE-embedding-migration.md:135` reads 0.7452 / 0.9337 / 0.7018, and `baseline.out` reproduces it.
- A8 still holds. `teacher-ceiling-2026-09/replay/qrels-sub50.trec` exists.
- A9 still holds. The qrels have 4 columns, with rel 1 ×11,758 and rel 2 ×576, and 323 distinct query ids.
- A10 still holds, modulo the round-1 whitespace note. Probe consistency: all 623 bodies were built from `text` alone.
- A11 still holds. See `hf_server.py:1289-1330`, `--host` `127.0.0.1` at `:1329`, and `API_REFERENCE.md:21` ("implements no authentication").
- A12 still holds. See `hf_prompt_policies.py:107-120,182-186`, and the call at `hf_server.py:946`.
- A13 still holds, and is strengthened: `sig.out` runs the code's matcher over the pinned configs (R9).
- A14 still holds. See `hf_server.py:1300`.
- A15 still holds. See `prompt_builder.py:177-183` (the spec cites `:173-181`, which is the same block including its comment lines) and `:257-264`. `render.out` has a maximum of 150,320 at query 129. All five configs report `max_position_embeddings` 262144 (`sig.out`). M8 qualifies the "demo-measured 2.90–3.05 chars/token" figure, but the fit holds under every density the evidence supports.
- A16 still holds. See `:1002`, and `:1090` with `Retry-After: 1`. The demo's 400 is re-observed in probe 1.
- A17 still holds. The non-zero count is at `report.py:275-280`, and scores 50..1 are never 0.
- A18 still holds. 0 pool docs are missing (F2).
- A19 still holds. The embedding-migration MDEs are 0.0323–0.0377, and `report-output-repeat.txt` shows 0.0525 (`:22`) and 0.0544 (`:44`).
- A20 still holds. The ranked-changes doc §0 has one row per `docs/plans/*GATE*.md` (`:28`).
- A21 still holds. There is no `*jev*` in `scripts/`.
- A22 still held when written. `main` is now 3 ahead of `origin/main`, all docs-only commits (`861a8ef9`, `883a3cf7`, `3c5ae917`). That is not load-bearing for the scripts, and round 1 already dropped the same drift (its F15), so it is not re-raised.
- A23 still holds. The layer, head and head-dim figures match `sig.out` (M3). The param counts and the `:536-542` and `:1136-1137` cites were re-read.
- A24 still holds. Re-probed this round: PLAIN-478 on Gemma-4-26B-A4B → 200, 50 answers, `input_tokens` 136,410 (`resp-PLAIN-478-gemma26.json`, md5 `9f06e7d9…`).
- A25 still holds. See the guard at `hf_server.py:510-513`, and the score table at `hf-server/README.md:123-129` for all five under their `shared_*` policies.

**Span check. There is one uncovered dependency:**
- **Every field of the sidecar's `reranker` block has a source available to its writer.** No assumption covers this. It was verified in-round and found false (code read plus a negative grep), so it becomes §2.1.

These were considered and are not uncovered:
- **Token density on the pod** is covered by A15 and the pre-flight's 422 branch (M8).
- **Query 129 as the memory worst case** is covered by A23 ("the query-129 pre-flight decides") and R16.
- **The demo admits NFCorpus bodies other than PLAIN-478** is covered by A24 and the fallback cap, with the distribution in F5.

## 2. Literal-wrongness findings

### 2.1 The `reranker` block that §6.3 requires has no source for five of its fields in the §9 CLI, the sidecar's only writer

**Description.** §6.3 requires the main-pass sidecar's `reranker` block to carry:

- the simple-jev commit;
- the HF repo and full revision;
- the policy;
- the question type;
- the wording SHA-256;
- the endpoint;
- "`--max-batch-size` and `--max-model-len` as run";
- the fallback count and query ids.

Item 0 adds "the final `--max-batch-size` and the pre-flight's `usage.input_tokens`". §9 makes `jev_rerank.py` the component that "writes … the sidecar", and fixes its interface as `--pool --corpus --queries --base-url --model --question-type --responses --out --composite [--subsample N --subsample-seed S]`. Tracing each field back to a source:

| Field | Source | Status |
|---|---|---|
| HF repo | `--model` | ok |
| Question type | `--question-type` | ok |
| Endpoint | `--base-url` | ok |
| Wording SHA-256 | computed | ok |
| Fallback ids | computed | ok |
| simple-jev commit | none | hf_server reports no version or commit on any route |
| `--max-batch-size`, `--max-model-len` as run | none | the server's response metadata omits both |
| Pre-flight `usage.input_tokens` | a different, earlier request | not visible to the pass that writes the sidecar |
| Full revision and policy | response `metadata` | only when the server was launched with `ENABLE_OPEN_JEV_ADVANCED_METRICS=1`, which §6.2's launch line does not set |

As specified, the client cannot write the provenance the spec requires. That includes the two fields round 1's fix added. So the gate doc's record of *what configuration was measured* would be missing, or would be filled by hand into a file the client regenerates on a resume.

**Evidence.**
- [negative] `grep -n "max_model_len\|max_batch_size\|__version__\|git\|commit"` over `hf-server/hf_server.py`, `hf_vision.py` and `hf_media.py` hits only loader, constructor and docstring sites (`hf_server.py:443-453`, `:533`, `:1119-1120`, `:1136`, `:1192`, `:1236`). None of them is in a response.
- [existence] Code read of the routes: `/health` returns `{status, model}` (`:1049-1052`). `/v1/models` returns `{id, object, created, owned_by, x_max_choice_options}` (`:1054-1061`). The classifier response metadata is `self.metadata` = `{backend, prompt_policy, prompt_policy_selection, image_input, max_image_*, default_image_*, model_revision, rope_factor}` (`:1264-1275`), merged only `if self.advanced_metrics` (`:949-954`). That flag is read from the environment at service construction (`:864-869`).
- [existence] The spec's §6.2 launch line has no `ENABLE_OPEN_JEV_ADVANCED_METRICS`. The env var is named only in the pre-flight and policy-confirmation sentences.

**Population closure.** The rule is that every required `reranker`-block field has a source. It was checked against every field (the table above) and every pass that runs the same writer:

| Pass | Disposition |
|---|---|
| Screen arms (demo) | ok. [existence] Spec read: the demo has no pod launch configuration to record. The endpoint, model and question type come from `--base-url`, `--model` and `--question-type` in the §9 interface (spec line 273). The wording SHA-256 and fallback ids are computed by the client. |
| Transfer check | → §2.1, the same gap on the pod. |
| Main pass | → §2.1. |
| Repeat pass | → §2.1. |

[bidirectional] Direction 1, fields → sources, is the table above. Direction 2, flags → consumers: every §9 flag has a consumer (`--pool`/`--corpus`/`--queries` → the loaders; `--base-url`/`--model`/`--question-type` → the request; `--responses` → the ledger; `--out` → the run writer; `--composite` → the sidecar; `--subsample`/`--subsample-seed` → `select_subsample`). So the gap is only in direction 1. [existence] Codebase family: `teacher_rerank.py` hit the same problem and solved it with record-only flags (see below).

**Proposed fix.** In §9, extend the `jev_rerank.py` CLI with record-only provenance flags. Each is written verbatim into the sidecar's `reranker` block, is never sent to the server, and records `null` when omitted:

- `--server-commit`
- `--revision`
- `--policy`
- `--max-batch-size`
- `--max-model-len`
- `--preflight-input-tokens`

This follows `teacher_rerank.py`'s `--vllm-version`/`--quantisation`/`--instance-type` precedent. Also add `ENABLE_OPEN_JEV_ADVANCED_METRICS=1` to the §6.2 launch line, so that `metadata.model_revision` and `metadata.prompt_policy` are present in every pod response and can be cross-checked against `--revision`/`--policy`.

Evidence:
- [existence] Precedent: `teacher_rerank.py:576-579` defines `--vllm-version`, `--quantisation` and `--instance-type`, each "recorded verbatim in the sidecar's reranker block; omit to record null". `write_sidecar` writes them at `:543`, `:551` and nearby, and documents the rule at `:533-535`.
- [existence] Advanced metadata carries `model_revision` and `prompt_policy` (`hf_server.py:1266`, `:1273`), merged at `:950-954`. Enabling it does not change scores (`response_scoring.py` `build_answers` docstring: "Enabling diagnostics does not change the scores"). The binary `noul` answer is rebuilt as `{type, noul}` after assembly (`hf_prompt_policies.py:182-186`), so the acceptance rule R1 is unaffected.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1: `--max-model-len 16384` rejected every gate request.** Resolved. §6.2 now launches with `--max-model-len 65536`, and A15 was rewritten around the full-pool branch. Every pod request fits at any density above 2.29 chars/token (R13). The query-129 pre-flight was added (§6.3 item 0).
- **Round 1 §3.1: pod GPU memory for the copied prefix KV was unsized.** Resolved with option (b). §6.2 now has the weights-plus-KV table, with H200s for the 26B-A4B, 27B and 35B-A3B, and the blocking pre-flight with a `--max-batch-size` fallback. The table arithmetic reproduces (M3).
- **Round 1 §1: A15 failed.** Resolved. A15 is restated as "every branch fits 65536" and reconfirmed this round.
- **Round 1 span check: two uncovered dependencies** (demo acceptance of 50-question NFCorpus bodies; hf_server serving hybrid-attention models). Resolved. They were added as A24 (re-probed this round) and A25.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.** §2.1 must be applied before planning: add the record-only provenance flags to §9's CLI, and enable advanced metrics in §6.2's launch line. No forced decisions remain.

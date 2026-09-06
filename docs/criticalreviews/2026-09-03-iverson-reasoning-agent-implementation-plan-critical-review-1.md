# Critical Implementation Review: 2026-09-03-iverson-reasoning-agent-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-09-03-iverson-reasoning-agent-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA `3580842`); cited file:line references re-checked under §1. (`392a4da` adds the spec's CDR round-1 file; `6ff185d` adds this plan. No source file the plan cites changed.)

Reviewed read-only at `main@6ff185d`. No venv was created and no package installed; `anthropic` and `pydantic` are not on this machine, so every claim about them is checked against the bundled `claude-api` skill files, and the plan's test fixtures were traced by hand against the plan's code. `grpcio 1.81.1`, `protobuf 6.33.6`, and the generated `iverson_client` protos were exercised with `python3` from `Iverson.Clients/Python`.

## 0. Coverage enumeration

### Task 1 — scaffold and configuration
- Step 1 commands (`venv --without-pip`, `pip --python … install`, `.pth`, import smoke) — ok: `ensurepip` absent and `pip 26.1.2` present re-confirmed; `iverson_client`'s only third-party imports are `grpc` and `google.protobuf` (`auth.py` uses `urllib`), so the five packages installed are sufficient for the smoke import; the generated `_pb2.py` demand protobuf ≥ 6.33.5 and `_pb2_grpc.py` grpcio ≥ 1.81.1, both satisfied by unconstrained resolution today (see §1 span check).
- `pyproject.toml` — ok: `[tool.pytest.ini_options] testpaths` makes `Iverson.Agents/Python` the rootdir; no `[build-system]` is needed since nothing installs the package.
- `config.py` / `__init__.py` / `test_config.py` code — ok: frozen dataclass; `FrozenInstanceError` on attribute set; defaults match spec §5 verbatim.
- Step 7 `git add` of two directories — ok: `.gitignore:80-81` ignores `__pycache__/` and `*.pyc`; `.pytest_cache` lives at the rootdir, not under either added directory.

### Task 2 — schema
- `test_schema.py` fixtures vs `schema.py` — `test_validate_filters_matches_case_insensitively_and_emits_canonical_spelling` → §2.1. Remaining tests traced: `resolve_type` case-insensitive/raise ok; `render_schema` emits only `Source`, `PublishedAt`, `WordCount` and no substring `Title`/`Body` ok; drops-unknown/non-metadata/uncoercible → `[ValidFilter("WordCount", 12.0)]` ok; `SchemaCache` `MagicMock.__enter__` configuration honoured and `first is second` ok (verified `with MagicMock() as x` returns the configured value).
- `schema.py` code — ok: `mpb.CLR_*` module-level constants, `mpb.ClrType.Name`, `SchemaType.fields.add(...)` all exist (exercised); `datetime.utcnow` still exists on 3.14 (DeprecationWarning only, not an error under the plan's `pytest -q`).
- Rule: filter-name matching (both directions) — over-inclusion: none (exact lower-case equality against `SchemaField.name`); under-inclusion: snake_case input never matches a PascalCase schema name → this is exactly the mechanism §2.1 trips on. The code implements the spec's stated rule (§4.1 "matched case-insensitively against `SchemaField.name`"); the plan's own test contradicts it.
- Rule: CLR coercion — ok: numeric→`float`, bool→bool/"true", textual→`str`, else drop; `CLR_BYTES` metadata would be dropped with a log line, which is the spec's "failure → dropped".

### Task 3 — retrieval
- `test_retrieval.py` fixtures vs `retrieval.py` — ok, all 8 traced: `chunks_request` field order/values; `locate` grouping (A: max(0.3,0.8)=0.8, chunks sorted `a2,a1`; ranked `B,A`; `top_k=8`); merge + empty-query report; InvalidArgument retry (side-effect list `[err, [A]]` consumed once each; `RepeatedCompositeContainer == []` is `True` — exercised); Unavailable/unfiltered-InvalidArgument mapping; assemble top-up (A gets `a-top1,a-top2`, B untouched, one `search_chunks` call with `Id`/`"A"` PK clause, metadata keyed `Source`/`PublishedAt`); drop-when-GetMany-omits; `render_context` budget arithmetic (full ≈325 tok; 260 drops `y` (lowest score 0.2) first; 60 drops `z` then `x`, both summaries shown, both `[doc n]` headers kept).
- `grpc.RpcError()` instantiation with instance-attribute `code`/`details` lambdas raised from `MagicMock.side_effect` — ok (exercised).
- `@iverson_entity` bare, `title: str = None` (non-`FieldMeta` default), `object.__new__(Doc)` instances — ok (`annotations.py:222-226`, `:307-311`; exercised: `key_field='id'`, `metadata_fields=['source','published_at']`, `chunk_fields=[('body',512,64,False)]`, `summary_fields=['summary']`).
- `_search` retry recursion — ok: after `del retry.filter[:]` a second InvalidArgument has empty `filter` and raises `RetrievalError` (no infinite recursion); spec §6 row 1's "query is skipped" is unreachable once filters are gone, since the residual InvalidArgument is the chunk-field/query case that §6 row 2 says fails the session.
- `assemble` parameter sourcing (`key_field`, `metadata_fields`, `summary_fields`, `_to_pascal_case`) — ok against `annotations.py:358-371`, `core.py:96-99`.
- `render_context` / citation passage set — → §2.2 (dropped passages are removed from a local copy only).
- Dynamic: `get_many` denied → `found=false` → entity absent → parent dropped with a log line — ok, spec §3.4.

### Task 4 — planner
- Tests vs code — ok: cap at 3, kwargs (`model`, `output_format is Plan`, `messages[0]["content"]` contains schema and question), empty-queries fallback; pydantic model equality is field-wise.
- `messages.parse(..., output_format=Plan).parsed_output` — ok per `tool-use.md:516-527`; `system=` on `.parse` is not in the cited lines (see §1 #23).
- `Filter.value: str | float | bool` → `anyOf` — ok, `tool-use-concepts.md:491`.

### Task 5 — session
- `test_session.py` fixtures vs `session.py`, side-effect consumption traced per test:
  - plain answer: `search_chunks` ×3 (locate, top-up A, top-up B), `get_many` ×1; page contains `[doc 1] key=A`, `Source=legal`, question — ok.
  - planner filters: `source`→`Source` kept, `Title` dropped (not metadata); first request's clauses `[("Source","legal")]` — ok.
  - search_more (m=1): exactly two `search_chunks` calls (no top-ups), `get_many` ×2; fresh = `[C]`; tool result `[doc 3] key=C…` with no `key=A`; final message's last entry is the `tool_result` user message — ok.
  - budget exhausted: calls 1–3 run `expand_document` (`get_many` + PK top-up each, 6 `search_chunks` / 4 `get_many` total, within the ×10 lists), call 4 gets `is_error` + `BUDGET_EXHAUSTED`; `call_args_list[4]` is the fifth create — ok.
  - invalid citation: re-request once, then `"Still  and [doc 1]."` → `.replace("  "," ")` → `"Still and [doc 1]."` (first alternative) with flag — ok.
  - refusal: `_create` raises before content is read — ok, spec §6.
  - empty retrieval: `assemble` skipped (`if parents`), page says `No documents were found for: q`, no citations — ok.
- `from tests.test_schema import policy_doc_type` — ok: `tests/__init__.py` makes `tests` a package; pytest's prepend mode and `python -m` both put `Iverson.Agents/Python` at `sys.path[0]`, ahead of the `.pth` entry whose `Iverson.Clients/Python/tests/__init__.py` would otherwise shadow it (see §1 span check).
- `TOOLS` schemas — ok: `additionalProperties: false`, `required` on every object, `type: ["string","number","boolean"]` union; no unsupported constraints (`tool-use-concepts.md:486-500`).
- Manual loop shape (`tool_result` blocks batched in one user message; `response.content` appended whole) — ok, `tool-use.md:168-230`.
- `_user_key` — ok: non-JWT tokens key on themselves via the broad `except`.
- Identity: every data call goes through `coordinator(cls).with_acting_user(token)`; schema via per-user client — ok, `core.py:624-636`, `:748-785`, `:875-891`.
- Rule: `_invalid_citations` (both directions) — ok: `1 ≤ n ≤ shown`; `[doc 0]` and `[doc k+1]` invalid; `search_more` grows `shown` before the final check.
- Rule: `search_more` dedup key = document key — ok, spec §4.5 "by key".
- Citation `passages` sourcing — → §2.2.
- Spec §4.4 `cache_control` on the system prompt — dropped: omitted by the plan, but it changes cost, not any stated output.
- `NoAccessibleDocuments` imported, never raised — dropped: dead symbol; the read-denied path is delivered through the "No documents were found for:" page (spec §6), and the schema-omitted path through `TypeNotAccessible`.

### Task 6 — evaluation and CLI
- `test_evaluate.py` vs `evaluate.py` — ok: recall 0.5 (unanswerable excluded), grounding 2/2, honesty 1/1, per-item lists; `judge` called only when citations exist.
- `judge_grounding` — ok, same `parse` shape as Task 4.
- `__main__.py` — ok: `IversonClientCredentials(client_id, client_secret, token_endpoint, scope=None)` matches `auth.py:17-21`; `IversonClient(host, port, use_tls=, credentials=, acting_user_token=)` matches `core.py:824-832`; `IVERSON_GRPC_URL=http://localhost:8080` (bench-env.sh; same default as `Program.cs:26`) parses to `localhost`/`8080`/no TLS; `anthropic` imported at module top so `--help` needs the venv, which Step 5 uses.
- Step 5 count 27 — ok (2+6+8+2+7+2).
- Grounding judge's passage list (`c.passages`) — → §2.2 (same root cause).

### Task 7 — fanout sweep
- Step 1 commands — ok: snapshot names strip to `benchmark_documents{,_chunks}_tenant_bypass`; compose exposes Qdrant `6333` with the same `dev-only-…` key (`Iverson.Server/docker-compose.yml:111-114`); `stack.py query` tier documented at `stack.py:1-20`.
- Step 2 `sed` — ok: run on a scratch copy for 5/4/6/8, each pass leaves exactly `DocumentBudget = 5;` and `ChunkBudgetMultiplier = <mult>;` (single-digit backreference, so `\15` is group 1 + literal `5`).
- Guard arithmetic — ok: 19,967/5,183 = 3.85 chunks/doc; ×4→5.19, ×5→6.49, ×6→7.79, ×8→10.38 all ≥ 5; ×2/×3 refused (`ChunkBudgetGuard.cs:36-45`).
- Step 3 restore — ok: relative path from the LoadTest directory.
- Step 4 `report.py` invocation — ok as a command: flags exist (`report.py:594-612`), measures are `nDCG@10, R@50, AP` (`:628`), sidecar resolves `agent-k5-fN.chunks.trec` → `agent-k5-fN.meta.json` (`:156-168`), baseline is excluded from the comparison set but still scored per-run (`:540-548`).
- Step 5 verdict rule vs step 4's output — → §2.3.
- Step 6 conditional commit — ok: `git commit -- <paths>` with an unchanged tracked path and a force-added untracked path commits the changed ones.
- Prose: "all at `DocumentBudget = 5` so the harness issues the agent's exact request" — ok, spec §7.1.

### Cross-task interface contracts
- T1 `AgentConfig` → T2 (`schema_ttl`), T5, T6, T7 — ok.
- T2 `ValidFilter` → T3 `chunks_request`, `locate`; `SchemaCache`/`resolve_type`/`render_schema`/`validate_filters` → T5 — ok, signatures match at every call site.
- T3 `RankedParent`, `DocumentContext`, `locate`, `assemble`, `render_context`, `estimate_tokens`, `_render_one` → T5 (three `assemble` call sites: initial, `search_more`, `expand_document` — each sources `entity_cls`, `type_name`, `chunk_property`, `m`, `title_field` from `self`; `expand_document` passes an empty-chunk `RankedParent`) — ok.
- T4 `Plan`/`RetrievalQuery`/`Filter`/`plan` → T5 — ok.
- T5 `AgentAnswer`/`Citation`/`AgentSession` → T6 — ok, `Citation` positional order `(doc_number, key, title, passages)` matches the test helper.
- T2 test module → T5 test module (`policy_doc_type`) — ok (import path verified above).
- T7 ⧫ persistence: `benchmark-query` writes `<label>.{chunks,similar}.trec` + `<label>.meta.json`; `report.py` reads both by name — ok. `.meta.json` records the server `/build` composite, unaffected by the LoadTest edit — ok (`BenchmarkQueryScenario.cs:160-171`).
- T7 → T1 conditional `fanout` edit — ok, both files named.
- Ordering — ok: no task imports a later task's symbol.

## 1. Verified-plan-assumptions cross-check

1. still holds — `ls -d Iverson.Agents` → no such file.
2. still holds — `.gitignore:47` `**/docs/criticalreviews/`, `:49` `**/docs/plans/`; no `venv`/`Agents` entry.
3. still holds — `RESTORE.md` restore loop, key, and counts 5,183 / 19,967 as cited.
4. still holds — `beir/queries.jsonl`, `keymap.json`, `keymap.json.stats.json` (`documents: 5183, chunks: 19967`), `qrels.trec`, `runs/` present.
5. still holds — `python-libs/` holds `ir_measures-0.4.3`, `pytrec_eval`, `numpy`, `scipy`.
6. still holds (trivially — the file now exists because the plan was written).
7. still holds for the environment facts — `import ensurepip` → ModuleNotFoundError; `pip 26.1.2`. The scratch venv / dry-run resolution was not re-executed this round (read-only).
8. still holds — `setuptools 78.1.1`, `import setuptools.backends` → ModuleNotFoundError; `conformance/driver.py:24` "the package is not pip-installed".
9. still holds (not re-executed; standard `site` behaviour for a venv's own `site-packages`).
10. still holds — `Program.cs:401-404`; `BenchmarkQueryScenario.cs:62,82,167,173,182`.
11. still holds — `report.py:594-612`; `grep -c -- --pair` → 0; the `list(ir_measures.read_trec_run(baseline_path))` fix has not landed (grep → nothing).
12. still holds — the five exports present.
13. still holds — `stack.py:1-20`.
14. still holds — `git log --oneline -12`.
15. still holds — `core.py:96-99`.
16. still holds — `search.py:38-53`.
17. still holds — `core.py:748,783,624,824-832,875-891`.
18. still holds — `annotations.py:358-371` (`summary_fields` is in fact always present; `meta.get` is harmless).
19. still holds — exercised over the generated module (field lists and `ClrType.Name(7) == "CLR_DATETIME"`).
20. still holds — exercised (`EQUALS`, `FILTER`, `AND` module-level; request/response field lists).
21. still holds — `grpc 1.81.1`, `StatusCode` members present.
22. still holds — `core.py:559-568`.
23. still holds for every shape the cited lines show (`tool-use.md:168-230` manual loop and `tool_result` batching; `:498-527` `messages.parse(model=, max_tokens=, messages=, output_format=).parsed_output`; `README.md:53` httpx2). `system=` on `.parse` is not in the cited lines; `system=` is shown on `create` (`README.md:114,204`) and `.parse` is documented as the create call plus `output_format` (`tool-use-concepts.md:484`). With no SDK on this machine it cannot be exercised, and Task 4's mocked tests would not catch a rejected kwarg — the first live planner call would.
24. still holds — `tests/test_entity_coordinator.py:7,54,214`.
25. still holds — `__init__.py` exports; `generated/` listing; `_to_pascal_case`, `_to_search_value` present.
26. still holds — checked per import in §0.
27. still holds in substance — the grep also shows reads at `:102-117`, all inside the guard's refusal message; no consumer outside the guard and the two request/collapse sites.
28. still holds — recomputed: ×4 → 5.19 ≥ 5; ×3 → 3.89 refused (`ChunkBudgetGuard.cs:36-45`, `:117`).
29. still holds — `git ls-files | grep -ci venv` → 0.

Span check (dependencies with no covering assumption, verified in-round):
- `Iverson.Clients/Python/tests/__init__.py` exists, so the `.pth` path carries a second regular package named `tests`; `from tests.test_schema import …` resolves to the agent's package because `python -m pytest` (cwd) and pytest's prepend mode both insert `Iverson.Agents/Python` at `sys.path[0]`, ahead of site-packages `.pth` entries — holds.
- The generated protos require protobuf ≥ 6.33.5 (`object_search_pb2.py:12-18`) and grpcio ≥ 1.81.1 (`object_search_pb2_grpc.py:8-14`); the plan's pins are `protobuf>=5.29.0`, `grpcio>=1.81.1` — satisfied today because unconstrained resolution selects the latest (6.33.6 is already published) — holds.
- `iverson_client`'s third-party imports are only `grpc` and `google.protobuf` — holds, so Task 1's smoke import needs nothing beyond the five packages installed.
- `@iverson_entity` bare form, non-`FieldMeta` defaults, and `object.__new__` instances — holds (`annotations.py:222-226,307-311`; exercised).
- `IversonClientCredentials` field names — holds (`auth.py:17-21`).
- Qdrant host port/key for Task 7 step 1 — holds (`Iverson.Server/docker-compose.yml:111-114`).
- `IVERSON_ACTING_USER_TOKEN` has no producer in `bench-env.sh` (the LoadTest mints its own, `Program.cs:35-41`); not a plan-execution dependency — no step runs the CLI live (Task 6 step 5 runs `--help` only) — noted, not a gap.

## 2. Literal-wrongness findings

1. **Task 2's filter-validation test fails against Task 2's code: `published_at` never matches `PublishedAt` under the plan's (spec-mandated) rule.**
   - Evidence: `validate_filters` builds `by_lower = {f.name.lower(): f …}` from the GetSchema names, giving the key `"publishedat"`; the test passes `("published_at", "2025-11-02")` and asserts `ValidFilter("PublishedAt", "2025-11-02")` is returned. Exercised over the generated proto: `'published_at' in by_lower → False` — the clause is dropped as "unknown field", and `test_validate_filters_matches_case_insensitively_and_emits_canonical_spelling` fails; Task 2 step 4's "6 passed" and Task 5 step 5's "25" / Task 6 step 5's "27" cannot be reached. The code is the spec's rule (§4.1: matched *case-insensitively* against `SchemaField.name`, whose spelling is PascalCase per inherited assumption 28); the test is what is wrong.
   - Proposed fix: change the test's first input to a case variant of the schema spelling — e.g. `("publishedat", "2025-11-02")` or `("PUBLISHEDAT", …)` — so the test exercises the rule the spec states. (Making the validator also accept snake_case would be a change to the spec's rule, not to the plan.)

2. **Citations (and the §7.2 grounding judge) carry passages that were never on the page.**
   - Evidence: `render_context` (Task 3) drops budget-exceeding passages from a local `kept` copy and never records what survived; `run` (Task 5) builds `Citation(…, [t for _, t in state.contexts[n - 1].passages])` from the unpruned list, and `evaluate` (Task 6) hands `[p for c in a.citations for p in c.passages]` to the judge. Spec §4.6 defines a citation's passages as "the passages that were on the page when it answered" and §7.2 defines grounding as judged "given only the answer and the cited passages". The pruning path is reachable under the default config: stage 1 keeps *every* chunk a parent surfaced (`RankedParent.chunks` is not capped at `m`), so up to `k × fanout` = 20 chunks per query × 3 queries can attach to one document; with 512-token chunks a page exceeds 24,000 tokens well before `search_more` is involved, and whichever passages `render_context` drops still appear in the returned citation and in the judge's evidence.
   - Proposed fix: make the rendered set authoritative — e.g. have `render_context` return `(text, kept)` and store `kept[i]` back onto each `DocumentContext` (or a `shown` field) before the first `_create`; build `Citation.passages` from that, and let `expand_document` extend the same list (its additions *are* on the page via the tool result). Add an assertion to `test_render_context_…` (or a new session test) that a citation excludes a budget-dropped passage.

3. **Task 7 step 5's plateau rule needs a comparison step 4 never produces.**
   - Evidence: step 4 runs `report.py` once with `--baseline agent-k5-f5.chunks.trec`, which yields paired statistics for ×4, ×6, ×8 *each against ×5* (`report.py:540-587`). Step 5 defines the plateau as "the smallest multiplier at which nDCG@10 stops improving with Holm p_adj < 0.05 against the **next-smaller arm**" over the arms 4, 5, 6, 8 — i.e. it needs 5-vs-4 (readable from 4-vs-5 with the sign flipped), 6-vs-5 (produced), and 8-vs-6, which no invocation produces. Whenever ×6 beats ×5 significantly, the verdict line cannot be written from the captured report.
   - Proposed fix: either add a second invocation to step 4 — `--run agent-k5-f8.chunks.trec --baseline agent-k5-f6.chunks.trec | tee -a report-agent-fanout-2026-09.txt` (each invocation is its own Holm family, `report.py:561-569`) — or restate the step 5 rule in terms of the ×5 baseline the single invocation actually compares against. Whichever is chosen, the verdict file should name which comparisons it read.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ Approve with literal-wrongness fixes — §1 has no failed assumptions; §2 has three findings (one makes the plan's own test suite unrunnable as written, two make a stated output/verdict differ from the spec's definition); §3 is empty.

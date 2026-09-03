# Iverson Reasoning Agent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-03-iverson-reasoning-agent-design.md` (commit SHA: `3580842`)

**Goal:** A Python agent that answers a question by retrieving the top-k documents from Iverson on each reasoning turn — passages located by `SearchChunks`, documents assembled by `GetMany` — reasoning over them with Claude under the end user's identity, citing them, and with a bounded tool-calling escape hatch; plus the evaluation of its retrieval layer on the existing benchmark harness.

**Architecture:** A new `Iverson.Agents/Python/iverson_agent` package (spec §1: no server or client-library change). Per request: plan (structured output) → locate (`SearchChunks` at `k × fanout`, group by `parent_key`, max chunk score) → assemble (`get_many` + PK-filtered `SearchChunks` top-up) → reason with two tools (`search_more`, `expand_document`) under hard call/token budgets → answer with enforced citations. Every Iverson call carries the end user's token via `with_acting_user`; `GetSchema` is fetched per user through a cached per-user `IversonClient`. The `GetSchema` PascalCase name is the one canonical spelling of a field wherever the model sees or emits one.

**Tech stack:** Python 3.14 (`/usr/bin/python3`, no `ensurepip`), `iverson_client` (path-imported, not pip-installed), `anthropic>=1.0` (`messages.parse` / tool use), `pydantic>=2`, `grpcio`, `protobuf`, `pytest`. Evaluation: `Iverson.LoadTest benchmark-query` + `scripts/report.py` (`ir_measures` under `~/repositories/iverson-benchmark-corpora/python-libs`).

---

## Global Constraints

Copied from the spec; every task holds to them.

- **Scores are for ordering only** (§4.2): a `ChunkSearchResponse.score` is fused and MMR-reordered; nothing thresholds it.
- **One canonical field spelling** (§4.1): the `GetSchema` name (PascalCase wire name, e.g. `PublishedAt`). Filters are validated case-insensitively against `SchemaField.name` and sent with the schema's spelling; `[doc n]` metadata and tool descriptions use it.
- **End-user identity on every Iverson data call** (§3.1, §3.4): the agent never retries as the service identity; a read-denied user gets "no accessible documents".
- **Model** `claude-opus-5` (§3.5), adaptive thinking by default (no `thinking` parameter). `refusal` is checked before content is read (§6).
- **Do not change the embedding model between evaluation arms** (§7.1).

## File Structure

Create:
- `Iverson.Agents/Python/pyproject.toml` — package metadata, dependencies, pytest config
- `Iverson.Agents/Python/iverson_agent/__init__.py` — package marker (exports `AgentConfig`, `AgentSession`, `AgentAnswer`)
- `Iverson.Agents/Python/iverson_agent/config.py` — `AgentConfig` (spec §5)
- `Iverson.Agents/Python/iverson_agent/schema.py` — `SchemaCache` (§3.3), `resolve_type`, `render_schema`, `validate_filters` (§4.1)
- `Iverson.Agents/Python/iverson_agent/retrieval.py` — `chunks_request`, `locate` (§4.2), `assemble` + `DocumentContext` (§4.3), `render_context`, gRPC error mapping (§6)
- `Iverson.Agents/Python/iverson_agent/planner.py` — `Plan` models, `PLANNER_SYSTEM`, `plan()` (§4.1)
- `Iverson.Agents/Python/iverson_agent/session.py` — `AgentSession`, `REASONER_SYSTEM`, `TOOLS`, tool loop, citation enforcement, `AgentAnswer` (§4.4–4.6)
- `Iverson.Agents/Python/iverson_agent/evaluate.py` — §7.2 metrics over a JSONL labelled set
- `Iverson.Agents/Python/iverson_agent/__main__.py` — `ask` / `evaluate` subcommands
- `Iverson.Agents/Python/tests/{test_config,test_schema,test_retrieval,test_planner,test_session,test_evaluate}.py`
- `docs/plans/2026-09-FANOUT-reasoning-agent.md` — the §7.1 sweep verdict (Task 7; `git add -f`)

Modify:
- `.gitignore` — add `Iverson.Agents/Python/.venv/`
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs:40-41` — **per-arm, never committed** (Task 7); restored with `git checkout` before the task ends
- `Iverson.Agents/Python/iverson_agent/config.py` — `fanout` default, only if Task 7's plateau is not 4

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` at spec-write time (§9, 28 items) and are NOT re-verified here. Trusted as ground truth:

1. `get_schema()` returns `SchemaType` with type/field descriptions and `is_metadata`/`is_key`/`is_chunk` — `core.py:885-891`; `object_mapping.proto`
2. `get_schema()` uses the constructor-time acting-user token; `with_acting_user()` exists only on `EntityCoordinator` — `core.py:860,889`, `:624-628`
3. `GetSchema` applies the acting user's row and field authorization — `ObjectMappingGrpcService.cs:79-89`
4. Coordinator search/retrieval methods send `x-acting-user-authorization` — `core.py:630-636`, `748-785`
5. `search_chunks()` accepts a raw `SearchChunksRequest`, returns `list[ChunkSearchResponse]` — `core.py:783-785`
6. `ChunksBuilder.where()` allows one clause; the server allows many — `vector_search.py:74-82` vs `ObjectSearchGrpcService.cs:869-913`
7. Server filter rules: `EQUALS`+`FILTER` only, on the PK or a metadata column; masked column → `InvalidArgument` — `ObjectSearchGrpcService.cs:878-912`
8. PK clause matched case-insensitively, read as `string_val` — `ObjectSearchGrpcService.cs:885-887`; `search.py:38-53`
9. Read-denied → empty stream (`SearchChunks`), `found=false` (`GetMany`) — `ObjectSearchGrpcService.cs:337-339`; `ObjectRetrievalGrpcService.cs:96-102`
10. Empty query → `InvalidArgument`; embedding failure → `Unavailable` — `ObjectSearchGrpcService.cs:381-391`
11. Server over-fetch factor 4 — `ObjectSearchGrpcService.cs:747,408`
12. Chunk `score` is fused and MMR-reordered — `object_search.proto`; `ObjectSearchGrpcService.cs:404-470`
13. `GetMany` returns every column the user may read — `EntityRepository.cs:12-21`
14. Summary/keywords populated server-side from embedding+chunk fields — `EnrichmentConsumer.cs:82-300`
15. Python has no `[IversonDocument]` decorator — grep; `SchemaRegistrar._build_request`
16. `iverson_chunk(contextual=)`, `iverson_metadata`, `iverson_description`, `iverson_summary`, `iverson_keywords`, `@iverson_entity(description=)` reach the wire — `annotations.py:133-179,222`; `core.py:269-293,329`
17. No existing agent/RAG sample in the Python client — `sample/`, `conformance/driver.py:679-686`
18. `MaxPassageAggregator` = max chunk score per document, descending — `Iverson.LoadTest/Benchmark/{MaxPassageAggregator,DocumentRanking}.cs`
19. The harness runs chunks at `DocumentBudget × ChunkBudgetMultiplier` then collapses — `BenchmarkQueryScenario.cs:300-317`
20. `report.py --baseline` R@50/AP defect and its scheduled fix — `Iverson.Server/Iverson.LoadTest/scripts/report.py:552-557`; reranker phase-1 plan `:224-236`
21. Corpora with `keymap.json` and `qrels.trec` exist — `~/repositories/iverson-benchmark-corpora/…`
22. "Do not change the encoder and measure in the same run" — reranker design §2
23. Anthropic SDK shapes: manual tool loop, `tool_result` batching, `messages.parse(output_format=Model)`; forced `tool_choice` avoided — `claude-api` skill `python/claude-api/tool-use.md`; `shared/tool-use-concepts.md` §Tool choice
24. `SearchChunksRequest.filter_logic` exists — `object_search.proto:123`
25. Structured outputs accept `anyOf` — `shared/tool-use-concepts.md:491`
26. Omitting `thinking` on `claude-opus-5` runs adaptive thinking — `python/claude-api/README.md:253`
27. `DocumentBudget`/`ChunkBudgetMultiplier` are `private const` with no runtime binding — `BenchmarkQueryScenario.cs:40-41,94`
28. `GetSchema` field names are PascalCase wire names; hydrated entities expose snake_case attributes — `core.py:96-98,273,555-566`; `ObjectMappingGrpcService.cs:208`

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (2026-09-03, `main@392a4da`):

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Agents/` does not exist; Task 1 creates it | `ls -d Iverson.Agents` → no such file |
| 2 | File path | Root `.gitignore` has no `.venv` entry and does not ignore `Iverson.Agents`; it does ignore `**/docs/plans/` (`:49`) and `**/docs/criticalreviews/` (`:47`) → plan and verdict need `git add -f` | `grep -n "venv\|Agents\|docs/plans" .gitignore` |
| 3 | File path | `~/repositories/iverson-benchmark-corpora/scifact-512-qdrant-snapshots/RESTORE.md` documents the restore (`curl … /snapshots/upload?priority=snapshot` per `*.snapshot`, api-key `dev-only-…`) and the expected counts 5,183 / 19,967 | `RESTORE.md:1-22` |
| 4 | File path | `scifact-run-2026-08-26/` holds `beir/queries.jsonl`, `keymap.json`, `keymap.json.stats.json` (`documents: 5183, chunks: 19967`), `qrels.trec`, `runs/` | `ls`; `keymap.json.stats.json` |
| 5 | File path | `python-libs/` holds `ir_measures 0.4.3`, `pytrec_eval`, `numpy`, `scipy` for `report.py` | `ls python-libs` |
| 6 | File path | `docs/plans/2026-09-03-iverson-reasoning-agent-implementation-plan.md` did not exist | `ls` → no such file |
| 7 | Command | `python3` is 3.14.4 with **no `ensurepip`**, so `python3 -m venv .venv` fails; `python3 -m venv --without-pip .venv` succeeds and system `pip 26.1.2` installs into it via `python3 -m pip --python .venv/bin/python install …` (`--python` must precede the subcommand) | `import ensurepip` → ModuleNotFoundError; scratch venv created; dry-run resolved `anthropic-1.3.0 pydantic-2.13.5 pytest-9.1.1 httpx2-2.12.0 …` |
| 8 | Command | The client is **not** pip-installable here: its `build-backend = "setuptools.backends.legacy:build"` is absent from installed setuptools 78.1.1, and system pip is PEP 668-managed; the repo's own convention is path import, not install | `import setuptools.backends` → ModuleNotFoundError; `pip install -e .` → externally-managed-environment; `conformance/driver.py:24` "the package is not pip-installed" |
| 9 | Command | A `.pth` file in `.venv/lib/python3.14/site-packages/` is honoured by the venv interpreter | `site.getsitepackages()` on the scratch venv lists that directory first |
| 10 | Command | `dotnet run -c Release -- benchmark-query --corpus-path <dir> --key-map-path <file> --output-dir <dir> --config-label <name>`; reads `<corpus>/beir/queries.jsonl` and `<keymap>.stats.json`; writes `<label>.{chunks,similar}.trec` + `<label>.meta.json` | `Program.cs:401-404`; `BenchmarkQueryScenario.cs:62,82,167,173,182` |
| 11 | Command | `report.py` flags at `main` are `--run` (repeatable), `--qrels`, `--stats-path`, `--baseline`; **no `--pair`** (reranker plan Task 2 has not landed) | `report.py:594-612`; `grep -c -- --pair` → 0 |
| 12 | Command | `/home/ben/iverson-benchmark-data/bench-env.sh` exports `IVERSON_CLIENT_ID`, `IVERSON_CLIENT_SECRET`, `IVERSON_CLIENT_SCOPE`, `IVERSON_GRPC_URL`, `IVERSON_TOKEN_ENDPOINT` — what `benchmark-query` needs (same env names the agent CLI reuses) | `grep export bench-env.sh` |
| 13 | Command | `python3 Iverson.Server/Iverson.LoadTest/scripts/stack.py query` brings up `qdrant, ollama, postgres, redis, authentik-server, iverson-api` — the tier `benchmark-query` needs | `stack.py:1-20` docstring |
| 14 | Command | Commit convention: lowercase imperative, no prefix | `git log --oneline -12` |
| 15 | Signature | `iverson_client.core._to_pascal_case(snake) -> str` (`"".join(part.capitalize() …)`) | `core.py:96-99` |
| 16 | Signature | `iverson_client.search._to_search_value(value) -> SearchValue` (bool→bool_val, str→string_val, int/float→number_val, else `str(value)`) | `search.py:38-53` |
| 17 | Signature | `EntityCoordinator.get_many(ids: List[str], trace_id: str = "") -> List[T]`, `.search_chunks(request)`, `.with_acting_user(token)`; `IversonClient(host="localhost", port=5000, use_tls=False, *, credentials=None, acting_user_token=None)`, `.get_schema(trace_id="")`, `.coordinator(cls)`, `__enter__/__exit__` | `core.py:748,783,624,824-832,875-891` |
| 18 | Signature | `cls._iverson_meta` keys: `type_name`, `key_field`, `chunk_fields` (list of `(field_name, max_tokens, overlap, contextual)`), `metadata_fields`, `summary_fields` (may be absent → `meta.get(…, [])`) | `annotations.py:334,358-371`; `core.py:231,235` |
| 19 | Signature | `object_mapping_pb2`: `SchemaType{name,description,fields,relations}`, `SchemaField{name,description,clr_type,is_array,is_key,is_nullable,is_metadata,is_search_key,search_key_order,is_embedding,is_chunk,enrichment}`; `ClrType.Name(7) == "CLR_DATETIME"`; names `CLR_STRING CLR_GUID CLR_INT32 CLR_INT64 CLR_DOUBLE CLR_FLOAT CLR_BOOL CLR_DATETIME CLR_BYTES` | `python3 -c` over the generated module |
| 20 | Signature | `object_search_pb2`: `SearchChunksRequest{type_name,property,query,top_k,trace_id,filter,filter_logic}`, `ChunkSearchResponse{parent_key,chunk_text,score,trace_id}`, module-level `EQUALS`, `FILTER`, `AND` | same |
| 21 | Signature | `grpc.RpcError` raised by stubs (`_InactiveRpcError`) exposes `.code() -> grpc.StatusCode` and `.details()`; codes `INVALID_ARGUMENT`, `UNAVAILABLE`, `FAILED_PRECONDITION`, `NOT_FOUND`, `PERMISSION_DENIED` | `python3 -c` |
| 22 | Signature | Hydrated values are `str \| float \| bool \| list \| None` — a datetime column arrives as `string_value` | `core.py:559-568` |
| 23 | Code validity | anthropic SDK 1.x: `client.messages.parse(model=, max_tokens=, system=, messages=, output_format=Model).parsed_output`; `client.messages.create(..., tools=…)` → `.content` blocks (`.type` `"text"`/`"tool_use"`, `.text`, `.id`, `.name`, `.input`), `.stop_reason`; `tool_result` blocks `{type, tool_use_id, content, is_error?}` returned together in ONE user message | `claude-api` skill `python/claude-api/tool-use.md:168-230,498-527`; `README.md:53` (1.x on `httpx2`); SDK source `src/anthropic/resources/messages/messages.py` `Messages.parse(*, max_tokens, messages, model, metadata, output_config, output_format, service_tier, stop_sequences, system, thinking, tool_choice, tools, …) -> ParsedMessage`, and `src/anthropic/types/parsed_message.py` `ParsedMessage.parsed_output` (fetched from `anthropics/anthropic-sdk-python@main`, 2026-09-03) |
| 24 | Code validity | Test convention: plain `pytest` functions/classes with `unittest.mock.MagicMock` stubs; no fixture library | `Iverson.Clients/Python/tests/test_entity_coordinator.py:7,54,214` |
| 25 | Code validity | Every import the plan's code uses resolves: `from iverson_client import IversonClient, IversonClientCredentials, EntityCoordinator`; `iverson_client.generated.{object_search_pb2, object_mapping_pb2}`; `iverson_client.core._to_pascal_case`; `iverson_client.search._to_search_value` | `iverson_client/__init__.py`; `generated/` listing |
| 26 | Ordering | T2, T3, T4 depend on T1 only; T5 on T2–T4; T6 on T5; T7 on T1 (final config edit). No task imports a symbol a later task creates | by construction — see each task's Interfaces |
| 27 | Consumer impact | The two constants are read at `BenchmarkQueryScenario.cs:94` (guard), `:255` (similar `TopK`), `:292` (similar collapse), `:300` (chunks `TopK`), `:317` (chunks collapse) — and nowhere else (not the `.meta.json` writer, which records the server `/build` composite). Setting both per arm changes exactly the request size and the collapse limit | `grep -n DocumentBudget\|ChunkBudgetMultiplier BenchmarkQueryScenario.cs` |
| 28 | Consumer impact | `ChunkBudgetGuard` on SciFact (3.85 chunks/doc) at `DocumentBudget = 5` passes for multiplier ≥ 4 (`5×4/3.85 = 5.19 ≥ 5`) and refuses `×2`, `×3` (throws `InvalidOperationException`, `:117`) | `ChunkBudgetGuard.cs:36-45`; `keymap.json.stats.json` |
| 29 | Consumer impact | Adding `Iverson.Agents/Python/.venv/` to `.gitignore` shadows nothing tracked | `git ls-files \| grep -ci venv` → 0 |
| 30 | Code validity | The `.pth` path carries a second regular package named `tests` (`Iverson.Clients/Python/tests/__init__.py`); the agent's `tests` package wins because `python -m pytest` (cwd) and pytest's prepend import mode put `Iverson.Agents/Python` at `sys.path[0]`, ahead of site-packages `.pth` entries — so `from tests.test_schema import …` in `test_session.py` resolves to the agent's module | CIR round 1 §1 span check |
| 31 | Code validity | The generated protos require protobuf ≥ 6.33.5 (`object_search_pb2.py:12-18`) and grpcio ≥ 1.81.1 (`object_search_pb2_grpc.py:8-14`); the plan's `protobuf>=5.29.0` pin is satisfied because unconstrained resolution selects the latest (6.33.6 published) | CIR round 1 §1 span check |
| 32 | Code validity | `iverson_client`'s third-party imports are only `grpc` and `google.protobuf`, so Task 1's smoke import needs nothing beyond the five packages installed | CIR round 1 §1 span check |
| 33 | Code validity | Bare `@iverson_entity`, non-`FieldMeta` defaults, and `object.__new__(Cls)` instances (as the tests build them) work | `annotations.py:222-226,307-311`; exercised in CIR round 1 |
| 34 | Signature | `IversonClientCredentials(client_id, client_secret, token_endpoint, scope=None)` field names as `__main__.py` uses them | `auth.py:17-21` |
| 35 | Command | Qdrant is at `localhost:6333` with api-key `dev-only-not-for-production-qdrant-key-0123456789` under compose, as Task 7 step 1 assumes | `Iverson.Server/docker-compose.yml:111-114` |
| 36 | Command | `IVERSON_ACTING_USER_TOKEN` has no producer in `bench-env.sh` (`Iverson.LoadTest` mints its own, `Program.cs:35-41`); no plan step runs the agent CLI live (Task 6 step 5 runs `--help` only), so a live `ask` needs a token minted per `docs/user-management-and-security.md` | CIR round 1 §1 span check |

## Tasks

### Task 1: Project scaffold and configuration

**Files:**
- Create: `Iverson.Agents/Python/pyproject.toml`, `Iverson.Agents/Python/iverson_agent/__init__.py`, `Iverson.Agents/Python/iverson_agent/config.py`, `Iverson.Agents/Python/tests/__init__.py`, `Iverson.Agents/Python/tests/test_config.py`
- Modify: `.gitignore` (append one line)

**Interfaces:**
- Produces: `AgentConfig` (consumed by every later task); the `.venv` every later task's test command uses.

- [ ] **Step 1: Create the environment.** From `Iverson.Agents/Python/` (create the directory first). The venv has no pip of its own (assumption 7); the client is imported by path, not installed (assumption 8):
```bash
mkdir -p /home/ben/repositories/Iverson/Iverson.Agents/Python && cd /home/ben/repositories/Iverson/Iverson.Agents/Python
python3 -m venv --without-pip .venv
python3 -m pip --python .venv/bin/python install "anthropic>=1.0,<2" "pydantic>=2,<3" "grpcio>=1.81.1" "protobuf>=5.29.0" "pytest>=8"
echo /home/ben/repositories/Iverson/Iverson.Clients/Python > .venv/lib/python3.14/site-packages/iverson_client.pth
.venv/bin/python -c "import iverson_client, anthropic, pydantic; print('ok')"
```

- [ ] **Step 2: Write `pyproject.toml`.**
```toml
[project]
name = "iverson-agent"
version = "0.1.0"
description = "A reasoning agent that gathers top-k documents from Iverson per reasoning turn"
requires-python = ">=3.11"
# iverson_client is imported by path (see the .pth line in the implementation plan, Task 1 step 1),
# matching how the repo consumes it everywhere else — it is deliberately not listed here.
dependencies = [
    "anthropic>=1.0,<2",
    "pydantic>=2,<3",
    "grpcio>=1.81.1",
    "protobuf>=5.29.0",
]

[project.optional-dependencies]
dev = ["pytest>=8"]

[tool.pytest.ini_options]
testpaths = ["tests"]
```

- [ ] **Step 3: Write `iverson_agent/config.py`** (spec §5 verbatim):
```python
"""Agent configuration — spec §5. Defaults are starting points; §7 is how to choose them."""
from __future__ import annotations

from dataclasses import dataclass
from datetime import timedelta


@dataclass(frozen=True)
class AgentConfig:
    model: str = "claude-opus-5"
    k: int = 5                 # documents per turn
    fanout: int = 4            # stage-1 chunk budget = k * fanout
    m: int = 3                 # passages per document after stage 2
    max_tool_calls: int = 3    # escape-hatch budget per session
    context_tokens: int = 24_000
    schema_ttl: timedelta = timedelta(minutes=10)
```

- [ ] **Step 4: Write `iverson_agent/__init__.py`** exporting `AgentConfig` now (Task 5 adds `AgentSession`, `AgentAnswer`):
```python
"""Reasoning agent over Iverson — see docs/specs/2026-09-03-iverson-reasoning-agent-design.md."""
from iverson_agent.config import AgentConfig

__all__ = ["AgentConfig"]
```

- [ ] **Step 5: Write `tests/__init__.py`** (empty) and `tests/test_config.py`:
```python
from datetime import timedelta

from iverson_agent.config import AgentConfig


def test_defaults_match_spec_section_5():
    cfg = AgentConfig()
    assert (cfg.model, cfg.k, cfg.fanout, cfg.m, cfg.max_tool_calls, cfg.context_tokens) == (
        "claude-opus-5", 5, 4, 3, 3, 24_000)
    assert cfg.schema_ttl == timedelta(minutes=10)


def test_config_is_frozen():
    import dataclasses
    with __import__("pytest").raises(dataclasses.FrozenInstanceError):
        AgentConfig().k = 9
```

- [ ] **Step 6: Run the tests.** `cd Iverson.Agents/Python && .venv/bin/python -m pytest -q` → 2 passed.

- [ ] **Step 7: Ignore the venv and commit.**
```bash
cd /home/ben/repositories/Iverson
printf '\n# Reasoning-agent virtualenv (created by the implementation plan, Task 1)\nIverson.Agents/Python/.venv/\n' >> .gitignore
git add .gitignore Iverson.Agents/Python/pyproject.toml Iverson.Agents/Python/iverson_agent Iverson.Agents/Python/tests
git commit -m "scaffold the iverson reasoning agent package with its configuration"
```

### Task 2: Schema access — per-user cache, rendering, filter validation

**Files:**
- Create: `Iverson.Agents/Python/iverson_agent/schema.py`, `Iverson.Agents/Python/tests/test_schema.py`

**Interfaces:**
- Consumes: `AgentConfig.schema_ttl` (T1).
- Produces: `SchemaCache`, `resolve_type`, `render_schema`, `validate_filters`, `ValidFilter`, `TypeNotAccessible` (T5).

- [ ] **Step 1: Write the failing tests** — `tests/test_schema.py`:
```python
from datetime import timedelta
from unittest.mock import MagicMock

import pytest

from iverson_client.generated import object_mapping_pb2 as mpb
from iverson_agent.schema import (
    SchemaCache, TypeNotAccessible, ValidFilter, render_schema, resolve_type, validate_filters,
)


def policy_doc_type() -> mpb.SchemaType:
    t = mpb.SchemaType(name="PolicyDoc", description="A policy document.")
    t.fields.add(name="Id", clr_type=mpb.CLR_GUID, is_key=True)
    t.fields.add(name="Title", clr_type=mpb.CLR_STRING, description="Document title")
    t.fields.add(name="Source", clr_type=mpb.CLR_STRING, is_metadata=True, description="hr|legal|it")
    t.fields.add(name="PublishedAt", clr_type=mpb.CLR_DATETIME, is_metadata=True, description="Published")
    t.fields.add(name="WordCount", clr_type=mpb.CLR_INT32, is_metadata=True)
    t.fields.add(name="Body", clr_type=mpb.CLR_STRING, is_chunk=True)
    return t


def test_resolve_type_is_case_insensitive_and_raises_when_absent():
    assert resolve_type([policy_doc_type()], "policydoc").name == "PolicyDoc"
    with pytest.raises(TypeNotAccessible):
        resolve_type([policy_doc_type()], "Other")


def test_render_schema_lists_only_metadata_fields_by_schema_name():
    text = render_schema(policy_doc_type())
    assert "PolicyDoc" in text and "A policy document." in text
    assert "Source" in text and "PublishedAt" in text and "WordCount" in text
    assert "Title" not in text and "Body" not in text


def test_validate_filters_matches_case_insensitively_and_emits_canonical_spelling():
    valid = validate_filters([("PUBLISHEDAT", "2025-11-02"), ("SOURCE", "legal")],
                             policy_doc_type(), trace_id="t")
    assert valid == [ValidFilter("PublishedAt", "2025-11-02"), ValidFilter("Source", "legal")]


def test_validate_filters_drops_unknown_non_metadata_and_uncoercible():
    valid = validate_filters(
        [("Nope", "x"), ("Title", "x"), ("WordCount", "many"), ("WordCount", "12")],
        policy_doc_type(), trace_id="t")
    assert valid == [ValidFilter("WordCount", 12.0)]


def test_schema_cache_uses_factory_once_per_user_within_ttl():
    client = MagicMock()
    client.__enter__.return_value = client
    client.get_schema.return_value = [policy_doc_type()]
    factory = MagicMock(return_value=client)
    cache = SchemaCache(ttl=timedelta(minutes=10), client_factory=factory)

    first = cache.get("user-a", "token-a")
    second = cache.get("user-a", "token-a")
    assert first is second
    factory.assert_called_once_with("token-a")


def test_schema_cache_is_per_user():
    client = MagicMock()
    client.__enter__.return_value = client
    client.get_schema.return_value = [policy_doc_type()]
    factory = MagicMock(return_value=client)
    cache = SchemaCache(ttl=timedelta(minutes=10), client_factory=factory)
    cache.get("user-a", "token-a")
    cache.get("user-b", "token-b")
    assert factory.call_count == 2
```

- [ ] **Step 2: Run them** — `.venv/bin/python -m pytest -q tests/test_schema.py` → ImportError (module absent).

- [ ] **Step 3: Write `iverson_agent/schema.py`.**
```python
"""Schema discovery under the end user's identity (spec §3.3) and planner-filter validation (§4.1).

Field names are the GetSchema names — the PascalCase wire spelling. That is the one canonical
spelling everywhere the model sees or emits a field name.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from typing import Callable, Iterable

from iverson_client import IversonClient
from iverson_client.generated import object_mapping_pb2 as mpb

log = logging.getLogger(__name__)


class TypeNotAccessible(Exception):
    """The requested type is not in the schema the end user may see (§3.4 row 1)."""


@dataclass(frozen=True)
class ValidFilter:
    field: str                  # canonical GetSchema spelling
    value: str | float | bool


@dataclass
class SchemaCache:
    """Per-user schema, fetched through a per-user IversonClient on cache miss (§3.3)."""
    ttl: timedelta
    client_factory: Callable[[str], IversonClient]   # end_user_token -> client bound to that user
    _entries: dict[str, tuple[datetime, list[mpb.SchemaType]]] = field(default_factory=dict)

    def get(self, user_key: str, end_user_token: str) -> list[mpb.SchemaType]:
        now = datetime.utcnow()
        hit = self._entries.get(user_key)
        if hit and now - hit[0] < self.ttl:
            return hit[1]
        with self.client_factory(end_user_token) as per_user:
            types = per_user.get_schema()
        self._entries[user_key] = (now, types)
        return types


def resolve_type(types: Iterable[mpb.SchemaType], type_name: str) -> mpb.SchemaType:
    for t in types:
        if t.name.lower() == type_name.lower():
            return t
    raise TypeNotAccessible(type_name)


def metadata_fields(schema_type: mpb.SchemaType) -> list[mpb.SchemaField]:
    return [f for f in schema_type.fields if f.is_metadata]


def render_schema(schema_type: mpb.SchemaType) -> str:
    """The compact rendering the planner reads: type, description, filterable fields only."""
    lines = [f"Type: {schema_type.name}", f"Description: {schema_type.description}",
             "Filterable fields (equality only):"]
    for f in metadata_fields(schema_type):
        lines.append(f"  - {f.name} ({mpb.ClrType.Name(f.clr_type)}): {f.description}")
    return "\n".join(lines)


_NUMERIC = {mpb.CLR_INT32, mpb.CLR_INT64, mpb.CLR_DOUBLE, mpb.CLR_FLOAT}
_TEXTUAL = {mpb.CLR_STRING, mpb.CLR_GUID, mpb.CLR_DATETIME}


def validate_filters(filters: Iterable[tuple[str, object]], schema_type: mpb.SchemaType,
                     trace_id: str) -> list[ValidFilter]:
    """Local validation before anything reaches the wire (§4.1). Every drop is logged."""
    by_lower = {f.name.lower(): f for f in schema_type.fields}
    valid: list[ValidFilter] = []
    for name, value in filters:
        f = by_lower.get(str(name).lower())
        if f is None:
            log.info("[plan] trace=%s dropped filter %r: unknown field", trace_id, name)
            continue
        if not f.is_metadata:
            log.info("[plan] trace=%s dropped filter %r: not a metadata field", trace_id, f.name)
            continue
        try:
            if f.clr_type in _NUMERIC:
                coerced: str | float | bool = float(value)
            elif f.clr_type == mpb.CLR_BOOL:
                coerced = value if isinstance(value, bool) else str(value).lower() == "true"
            elif f.clr_type in _TEXTUAL:
                coerced = str(value)
            else:
                raise ValueError(f"unsupported CLR type {mpb.ClrType.Name(f.clr_type)}")
        except (TypeError, ValueError) as exc:
            log.info("[plan] trace=%s dropped filter %r=%r: %s", trace_id, f.name, value, exc)
            continue
        valid.append(ValidFilter(f.name, coerced))
    return valid
```

- [ ] **Step 4: Run the tests** — `.venv/bin/python -m pytest -q tests/test_schema.py` → 6 passed.

- [ ] **Step 5: Commit.**
```bash
git add Iverson.Agents/Python/iverson_agent/schema.py Iverson.Agents/Python/tests/test_schema.py
git commit -m "add per-user schema cache, schema rendering, and planner filter validation"
```

### Task 3: Retrieval — locate, assemble, render, error mapping

**Files:**
- Create: `Iverson.Agents/Python/iverson_agent/retrieval.py`, `Iverson.Agents/Python/tests/test_retrieval.py`

**Interfaces:**
- Consumes: `ValidFilter` (T2).
- Produces: `RankedParent`, `DocumentContext`, `locate`, `assemble`, `render_context`, `estimate_tokens`, `RetrievalError`, `RetrievalUnavailable`, `NoAccessibleDocuments` (T5).

- [ ] **Step 1: Write the failing tests** — `tests/test_retrieval.py`:
```python
from unittest.mock import MagicMock

import grpc
import pytest

from iverson_client import iverson_entity, iverson_key, iverson_metadata, iverson_chunk, iverson_summary
from iverson_client.generated import object_search_pb2 as pb
from iverson_agent.schema import ValidFilter
from iverson_agent.retrieval import (
    DocumentContext, RetrievalError, RetrievalUnavailable, assemble, chunks_request, locate,
    render_context,
)


@iverson_entity
class Doc:
    id: str = iverson_key()
    title: str = None
    source: str = iverson_metadata()
    published_at: str = iverson_metadata()
    body: str = iverson_chunk()
    summary: str = iverson_summary()


def chunk(parent, text, score):
    return pb.ChunkSearchResponse(parent_key=parent, chunk_text=text, score=score)


def rpc_error(code):
    err = grpc.RpcError()
    err.code = lambda: code
    err.details = lambda: str(code)
    return err


def test_chunks_request_carries_every_filter_with_canonical_names():
    req = chunks_request("Doc", "Body", "q", 20,
                         [ValidFilter("Source", "legal"), ValidFilter("PublishedAt", "2025-11-02")], "t")
    assert (req.type_name, req.property, req.query, req.top_k, req.trace_id) == ("Doc", "Body", "q", 20, "t")
    assert [(c.property, c.operator, c.clause_type) for c in req.filter] == [
        ("Source", pb.EQUALS, pb.FILTER), ("PublishedAt", pb.EQUALS, pb.FILTER)]
    assert req.filter[0].value.string_val == "legal"
    assert req.filter_logic == pb.AND


def test_locate_groups_by_parent_ranks_by_max_and_takes_k():
    coord = MagicMock()
    coord.search_chunks.return_value = [
        chunk("A", "a1", 0.3), chunk("B", "b1", 0.9), chunk("A", "a2", 0.8), chunk("C", "c1", 0.5)]
    parents, empty = locate(coord, "Doc", "Body", [("q", [])], k=2, fanout=4, trace_id="t")
    assert [p.key for p in parents] == ["B", "A"]
    assert parents[1].best_score == pytest.approx(0.8)
    assert [t for _, t in parents[1].chunks] == ["a2", "a1"]
    assert coord.search_chunks.call_args.args[0].top_k == 8
    assert empty == []


def test_locate_merges_queries_and_reports_empty_ones():
    coord = MagicMock()
    coord.search_chunks.side_effect = [[chunk("A", "a1", 0.4)], []]
    parents, empty = locate(coord, "Doc", "Body", [("q1", []), ("q2", [])], k=5, fanout=4, trace_id="t")
    assert [p.key for p in parents] == ["A"] and empty == ["q2"]


def test_locate_retries_once_without_filters_on_invalid_argument():
    coord = MagicMock()
    coord.search_chunks.side_effect = [rpc_error(grpc.StatusCode.INVALID_ARGUMENT), [chunk("A", "a", 0.1)]]
    parents, _ = locate(coord, "Doc", "Body", [("q", [ValidFilter("Source", "x")])], k=5, fanout=4, trace_id="t")
    assert [p.key for p in parents] == ["A"]
    assert coord.search_chunks.call_args_list[1].args[0].filter == []


def test_locate_maps_unavailable_and_unfiltered_invalid_argument():
    coord = MagicMock()
    coord.search_chunks.side_effect = rpc_error(grpc.StatusCode.UNAVAILABLE)
    with pytest.raises(RetrievalUnavailable):
        locate(coord, "Doc", "Body", [("q", [])], k=5, fanout=4, trace_id="t")
    coord.search_chunks.side_effect = rpc_error(grpc.StatusCode.INVALID_ARGUMENT)
    with pytest.raises(RetrievalError):
        locate(coord, "Doc", "Body", [("q", [])], k=5, fanout=4, trace_id="t")


def entity(key, title="T", summary="S"):
    e = object.__new__(Doc)
    e.id, e.title, e.source, e.published_at, e.body, e.summary = key, title, "legal", "2025-11-02", "B", summary
    return e


def test_assemble_hydrates_tops_up_thin_parents_and_keys_metadata_by_schema_name():
    coord = MagicMock()
    coord.get_many.return_value = [entity("A"), entity("B")]
    coord.search_chunks.return_value = [chunk("A", "a-top1", 0.7), chunk("A", "a-top2", 0.6)]
    from iverson_agent.retrieval import RankedParent
    parents = [RankedParent("A", 0.9, [(0.9, "a1")]), RankedParent("B", 0.5, [(0.5, "b1"), (0.4, "b2"), (0.3, "b3")])]
    ctx = assemble(coord, Doc, "Doc", "Body", parents, "question", m=3, trace_id="t", title_field="title")
    assert [c.key for c in ctx] == ["A", "B"]
    assert [t for _, t in ctx[0].passages] == ["a1", "a-top1", "a-top2"]     # topped up
    assert [t for _, t in ctx[1].passages] == ["b1", "b2", "b3"]             # already >= m: no call
    coord.search_chunks.assert_called_once()
    req = coord.search_chunks.call_args.args[0]
    assert req.query == "question" and req.top_k == 3
    assert (req.filter[0].property, req.filter[0].value.string_val) == ("Id", "A")
    assert ctx[0].metadata == {"Source": "legal", "PublishedAt": "2025-11-02"}
    assert ctx[0].summary == "S" and ctx[0].title == "T"


def test_assemble_drops_parents_get_many_did_not_return():
    coord = MagicMock()
    coord.get_many.return_value = [entity("A")]
    coord.search_chunks.return_value = []
    from iverson_agent.retrieval import RankedParent
    ctx = assemble(coord, Doc, "Doc", "Body", [RankedParent("A", 0.9, [(0.9, "a")] * 3), RankedParent("Z", 0.1, [])],
                   "q", m=3, trace_id="t", title_field="title")
    assert [c.key for c in ctx] == ["A"]


def test_render_context_numbers_documents_and_drops_lowest_passages_first():
    ctx = [DocumentContext("A", "TA", {"Source": "legal"}, "sum-A", [(0.9, "x" * 400), (0.2, "y" * 400)], 0.9),
           DocumentContext("B", "TB", {}, "sum-B", [(0.5, "z" * 400)], 0.5)]
    full = render_context(ctx, budget_tokens=10_000)
    assert "[doc 1]" in full and "[doc 2]" in full and "Source=legal" in full and "y" * 400 in full
    tight = render_context(ctx, budget_tokens=260)
    assert "y" * 400 not in tight and "x" * 400 in tight          # lowest score dropped first
    assert [t for _, t in ctx[0].passages] == ["x" * 400]         # pruned in place: citations see only the page
    tiny = render_context(ctx, budget_tokens=60)
    assert "sum-A" in tiny and "[doc 2]" in tiny                  # summary fallback; documents never dropped
```

- [ ] **Step 2: Run them** — ImportError.

- [ ] **Step 3: Write `iverson_agent/retrieval.py`.**
```python
"""Stage 1 (locate), stage 2 (assemble), context rendering, and the §6 error boundary."""
from __future__ import annotations

import logging
from dataclasses import dataclass, field

import grpc

from iverson_client.core import _to_pascal_case
from iverson_client.generated import object_search_pb2 as pb
from iverson_client.search import _to_search_value
from iverson_agent.schema import ValidFilter

log = logging.getLogger(__name__)


class RetrievalError(Exception):
    """Fail the session (§6): misconfiguration, masked chunk field, or no collection."""


class RetrievalUnavailable(RetrievalError):
    """Embedding backend down (§6): fail the session; never answer without retrieval."""


class NoAccessibleDocuments(RetrievalError):
    """Every stage came back empty for this user (§3.4, §8)."""


@dataclass
class RankedParent:
    key: str
    best_score: float
    chunks: list[tuple[float, str]] = field(default_factory=list)   # (score, text), best first


@dataclass
class DocumentContext:
    key: str
    title: str | None
    metadata: dict[str, object]           # keyed by GetSchema name (PascalCase)
    summary: str | None
    passages: list[tuple[float, str]]     # (score, text), best first
    best_score: float


def chunks_request(type_name: str, chunk_property: str, query_text: str, top_k: int,
                   filters: list[ValidFilter], trace_id: str) -> pb.SearchChunksRequest:
    # Built from the generated protos: ChunksBuilder accepts one clause, the server any number.
    return pb.SearchChunksRequest(
        type_name=type_name, property=chunk_property, query=query_text, top_k=top_k,
        filter=[pb.SearchClause(property=f.field, operator=pb.EQUALS,
                                value=_to_search_value(f.value), clause_type=pb.FILTER)
                for f in filters],
        filter_logic=pb.AND, trace_id=trace_id)


def _search(coordinator, request: pb.SearchChunksRequest) -> list[pb.ChunkSearchResponse]:
    try:
        return coordinator.search_chunks(request)
    except grpc.RpcError as err:
        code = err.code()
        if code == grpc.StatusCode.UNAVAILABLE:
            raise RetrievalUnavailable(err.details()) from err
        if code == grpc.StatusCode.INVALID_ARGUMENT and request.filter:
            log.warning("[locate] trace=%s InvalidArgument with filters; retrying unfiltered: %s",
                        request.trace_id, err.details())
            retry = pb.SearchChunksRequest()
            retry.CopyFrom(request)
            del retry.filter[:]
            return _search(coordinator, retry)
        raise RetrievalError(f"{code.name}: {err.details()}") from err


def locate(coordinator, type_name: str, chunk_property: str,
           queries: list[tuple[str, list[ValidFilter]]], k: int, fanout: int,
           trace_id: str) -> tuple[list[RankedParent], list[str]]:
    """Stage 1 (§4.2): k×fanout chunks per query, grouped by parent, ranked by best chunk."""
    by_parent: dict[str, RankedParent] = {}
    empty_queries: list[str] = []
    for query_text, filters in queries:
        hits = _search(coordinator, chunks_request(type_name, chunk_property, query_text,
                                                   k * fanout, filters, trace_id))
        if not hits:
            empty_queries.append(query_text)
        for h in hits:
            p = by_parent.setdefault(h.parent_key, RankedParent(h.parent_key, h.score))
            p.best_score = max(p.best_score, h.score)      # max, not sum (§4.2)
            p.chunks.append((h.score, h.chunk_text))
    for p in by_parent.values():
        p.chunks.sort(key=lambda c: c[0], reverse=True)
    ranked = sorted(by_parent.values(), key=lambda p: p.best_score, reverse=True)[:k]
    return ranked, empty_queries


def assemble(coordinator, entity_cls: type, type_name: str, chunk_property: str,
             parents: list[RankedParent], question: str, m: int, trace_id: str,
             title_field: str | None) -> list[DocumentContext]:
    """Stage 2 (§4.3): one get_many, then a PK-filtered top-up for parents thinner than m."""
    meta = entity_cls._iverson_meta
    key_attr = meta["key_field"]
    key_prop = _to_pascal_case(key_attr)                       # canonical spelling of the PK
    metadata_attrs = {_to_pascal_case(a): a for a in meta["metadata_fields"]}
    summary_attr = next(iter(meta.get("summary_fields", [])), None)

    entities = {str(getattr(e, key_attr)): e
                for e in coordinator.get_many([p.key for p in parents], trace_id)}
    contexts: list[DocumentContext] = []
    for p in parents:
        e = entities.get(p.key)
        if e is None:
            log.info("[assemble] trace=%s parent %s not returned by GetMany; dropped", trace_id, p.key)
            continue
        passages = list(p.chunks)
        if len(passages) < m:
            more = _search(coordinator, pb.SearchChunksRequest(
                type_name=type_name, property=chunk_property, query=question, top_k=m,
                filter=[pb.SearchClause(property=key_prop, operator=pb.EQUALS,
                                        value=_to_search_value(p.key), clause_type=pb.FILTER)],
                trace_id=trace_id))
            if not more:
                log.info("[assemble] trace=%s no chunks for parent %s under PK filter; using stage-1 fragments",
                         trace_id, p.key)
            seen = {t for _, t in passages}
            passages += [(h.score, h.chunk_text) for h in more if h.chunk_text not in seen]
        contexts.append(DocumentContext(
            key=p.key,
            title=getattr(e, title_field, None) if title_field else None,
            metadata={name: getattr(e, attr, None) for name, attr in metadata_attrs.items()},
            summary=getattr(e, summary_attr, None) if summary_attr else None,
            passages=passages,
            best_score=p.best_score))
    return contexts


def estimate_tokens(text: str) -> int:
    """Budget estimate (§4.3): ~4 characters per token. Exact counting would cost a network call."""
    return len(text) // 4


def _render_one(n: int, c: DocumentContext, passages: list[tuple[float, str]]) -> str:
    meta = " ".join(f"{k}={v}" for k, v in c.metadata.items())
    head = f'[doc {n}] key={c.key} title="{c.title or ""}" {meta}'.rstrip()
    if passages:
        body = "\n".join(f"  passage: {t}" for _, t in passages)
    else:
        body = f"  summary: {c.summary or '(no summary available)'}"
    return f"{head}\n{body}"


def render_context(contexts: list[DocumentContext], budget_tokens: int) -> str:
    """Numbered, citable blocks under a token ceiling: drop lowest-scored passages across all
    documents first; a document with no passages left shows its summary. Documents are never dropped.

    Prunes each DocumentContext.passages IN PLACE to the rendered set, so the citations (§4.6),
    the grounding judge (§7.2), and expand_document's "shown" set are exactly what was on the page."""

    def render() -> str:
        return "\n\n".join(_render_one(i + 1, c, c.passages) for i, c in enumerate(contexts))

    text = render()
    while estimate_tokens(text) > budget_tokens:
        candidates = [(c.passages[-1][0], i) for i, c in enumerate(contexts) if c.passages]
        if not candidates:
            break
        _, i = min(candidates)
        contexts[i].passages.pop()
        text = render()
    return text
```

- [ ] **Step 4: Run the tests** — `.venv/bin/python -m pytest -q tests/test_retrieval.py` → 8 passed.

- [ ] **Step 5: Commit.**
```bash
git add Iverson.Agents/Python/iverson_agent/retrieval.py Iverson.Agents/Python/tests/test_retrieval.py
git commit -m "add passage-located, document-assembled retrieval over SearchChunks and GetMany"
```

### Task 4: Planner

**Files:**
- Create: `Iverson.Agents/Python/iverson_agent/planner.py`, `Iverson.Agents/Python/tests/test_planner.py`

**Interfaces:**
- Produces: `Plan`, `RetrievalQuery`, `Filter`, `plan()` (T5).

- [ ] **Step 1: Write the failing tests** — `tests/test_planner.py`:
```python
from types import SimpleNamespace
from unittest.mock import MagicMock

from iverson_agent.planner import Filter, Plan, RetrievalQuery, plan


def test_plan_calls_parse_with_schema_and_question_and_caps_at_three_queries():
    client = MagicMock()
    client.messages.parse.return_value = SimpleNamespace(parsed_output=Plan(queries=[
        RetrievalQuery(query_text=f"q{i}", filters=[Filter(field="Source", value="legal")]) for i in range(5)]))
    result = plan(client, "claude-opus-5", "Type: PolicyDoc", "What is the leave policy?")
    assert [q.query_text for q in result.queries] == ["q0", "q1", "q2"]
    kwargs = client.messages.parse.call_args.kwargs
    assert kwargs["model"] == "claude-opus-5" and kwargs["output_format"] is Plan
    assert "Type: PolicyDoc" in kwargs["messages"][0]["content"]
    assert "What is the leave policy?" in kwargs["messages"][0]["content"]


def test_plan_falls_back_to_the_question_when_the_model_returns_no_queries():
    client = MagicMock()
    client.messages.parse.return_value = SimpleNamespace(parsed_output=Plan(queries=[]))
    result = plan(client, "claude-opus-5", "Type: PolicyDoc", "leave policy")
    assert result.queries == [RetrievalQuery(query_text="leave policy", filters=[])]
```

- [ ] **Step 2: Run them** — ImportError.

- [ ] **Step 3: Write `iverson_agent/planner.py`.**
```python
"""Stage 0: one structured-output call turning the question into 1–3 retrieval queries (§4.1)."""
from __future__ import annotations

from pydantic import BaseModel

MAX_QUERIES = 3

PLANNER_SYSTEM = """You turn a user's question into retrieval queries against a document store.

Rules:
- Produce between one and three queries. The first is a rewrite of the whole question in the
  wording a matching passage would use. Add a second or third only when the question spans
  clearly distinct topics.
- A filter is an equality on one of the listed filterable fields, using the field name exactly
  as listed. Use a filter only when the question states the value; when unsure, use no filter.
- Never invent field names or values."""


class Filter(BaseModel):
    field: str
    value: str | float | bool


class RetrievalQuery(BaseModel):
    query_text: str
    filters: list[Filter]


class Plan(BaseModel):
    queries: list[RetrievalQuery]


def plan(client, model: str, schema_text: str, question: str) -> Plan:
    response = client.messages.parse(
        model=model, max_tokens=2048, system=PLANNER_SYSTEM,
        messages=[{"role": "user", "content": f"{schema_text}\n\nQuestion: {question}"}],
        output_format=Plan)
    result: Plan = response.parsed_output
    if not result.queries:
        return Plan(queries=[RetrievalQuery(query_text=question, filters=[])])
    return Plan(queries=result.queries[:MAX_QUERIES])
```

- [ ] **Step 4: Run the tests** — 2 passed.

- [ ] **Step 5: Commit.**
```bash
git add Iverson.Agents/Python/iverson_agent/planner.py Iverson.Agents/Python/tests/test_planner.py
git commit -m "add the structured-output retrieval planner"
```

### Task 5: Session — reasoning loop, tools, budgets, citations

**Files:**
- Create: `Iverson.Agents/Python/iverson_agent/session.py`, `Iverson.Agents/Python/tests/test_session.py`
- Modify: `Iverson.Agents/Python/iverson_agent/__init__.py` (export `AgentSession`, `AgentAnswer`)

**Interfaces:**
- Consumes: `AgentConfig` (T1); `SchemaCache`, `resolve_type`, `render_schema`, `validate_filters` (T2); `locate`, `assemble`, `render_context`, `estimate_tokens`, errors (T3); `plan` (T4).
- Produces: `AgentSession.run(question, end_user_token, trace_id) -> AgentAnswer`, `ModelRefused` (T6).

- [ ] **Step 1: Write the failing tests** — `tests/test_session.py`:
```python
import json
from datetime import timedelta
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

from iverson_client import iverson_entity, iverson_key, iverson_metadata, iverson_chunk
from iverson_client.generated import object_search_pb2 as pb
from iverson_agent.config import AgentConfig
from iverson_agent.planner import Plan, RetrievalQuery, Filter
from iverson_agent.session import AgentSession, ModelRefused
from tests.test_schema import policy_doc_type


@iverson_entity
class PolicyDoc:
    id: str = iverson_key()
    title: str = None
    source: str = iverson_metadata()
    published_at: str = iverson_metadata()
    body: str = iverson_chunk()


def text(t):
    return SimpleNamespace(type="text", text=t)


def tool_use(name, **inp):
    return SimpleNamespace(type="tool_use", id=f"tu-{name}", name=name, input=inp)


def message(*blocks, stop_reason="end_turn"):
    return SimpleNamespace(content=list(blocks), stop_reason=stop_reason)


def chunk(parent, t, s):
    return pb.ChunkSearchResponse(parent_key=parent, chunk_text=t, score=s)


def doc(key):
    e = object.__new__(PolicyDoc)
    e.id, e.title, e.source, e.published_at, e.body = key, f"T{key}", "legal", "2025-11-02", "B"
    return e


def make_session(responses, plan_queries=None, chunks=None, entities=None, cfg=AgentConfig()):
    anthropic = MagicMock()
    anthropic.messages.parse.return_value = SimpleNamespace(parsed_output=Plan(
        queries=plan_queries or [RetrievalQuery(query_text="q", filters=[])]))
    anthropic.messages.create.side_effect = responses
    coord = MagicMock()
    coord.search_chunks.side_effect = chunks or ([[chunk("A", "a1", 0.9), chunk("B", "b1", 0.5)]] * 10)
    coord.get_many.side_effect = entities or ([[doc("A"), doc("B")]] * 10)
    coord.with_acting_user.return_value = coord
    iverson = MagicMock()
    iverson.coordinator.return_value = coord
    schema_client = MagicMock()
    schema_client.__enter__.return_value = schema_client
    schema_client.get_schema.return_value = [policy_doc_type()]
    session = AgentSession(anthropic, iverson, PolicyDoc, cfg,
                           schema_client_factory=lambda token: schema_client, title_field="title")
    return session, anthropic, coord


def test_plain_answer_with_citations():
    session, anthropic, coord = make_session([message(text("Leave is 20 days [doc 1]."))])
    answer = session.run("How much leave?", "tok", trace_id="t")
    assert answer.text == "Leave is 20 days [doc 1]."
    assert [c.key for c in answer.citations] == ["A"]
    assert answer.tool_calls == 0 and not answer.flags
    coord.with_acting_user.assert_called_with("tok")
    ctx = anthropic.messages.create.call_args.kwargs["messages"][0]["content"]
    assert "[doc 1] key=A" in ctx and "Source=legal" in ctx and "How much leave?" in ctx


def test_planner_filters_are_validated_and_sent_canonically():
    session, _, coord = make_session(
        [message(text("x [doc 1]"))],
        plan_queries=[RetrievalQuery(query_text="q", filters=[Filter(field="source", value="legal"),
                                                              Filter(field="Title", value="no")])])
    session.run("q?", "tok", trace_id="t")
    req = coord.search_chunks.call_args_list[0].args[0]
    assert [(c.property, c.value.string_val) for c in req.filter] == [("Source", "legal")]


def test_search_more_appends_only_new_documents_and_continues():
    # m=1: every parent already has >= m passages, so no stage-2 top-up call consumes the
    # search_chunks side-effect list — its two entries are exactly the two locate() calls.
    session, anthropic, coord = make_session(
        [message(tool_use("search_more", query_text="q2", filters=[]), stop_reason="tool_use"),
         message(text("Done [doc 3]."))],
        chunks=[[chunk("A", "a1", 0.9), chunk("B", "b1", 0.5)], [chunk("A", "a1", 0.9), chunk("C", "c1", 0.7)]],
        entities=[[doc("A"), doc("B")], [doc("A"), doc("C")]],
        cfg=AgentConfig(m=1))
    answer = session.run("q?", "tok", trace_id="t")
    result_msg = anthropic.messages.create.call_args.kwargs["messages"][-1]
    tool_result = result_msg["content"][0]
    assert tool_result["type"] == "tool_result" and tool_result["tool_use_id"] == "tu-search_more"
    assert "[doc 3] key=C" in tool_result["content"] and "key=A" not in tool_result["content"]
    assert answer.tool_calls == 1 and [c.key for c in answer.citations] == ["C"]


def test_tool_budget_exhausted_returns_error_result_and_forces_answer():
    responses = [message(tool_use("expand_document", doc_number=1, query_text="more"), stop_reason="tool_use")] * 4 \
        + [message(text("Final [doc 1]."))]
    session, anthropic, _ = make_session(responses, cfg=AgentConfig(max_tool_calls=3))
    answer = session.run("q?", "tok", trace_id="t")
    fourth = anthropic.messages.create.call_args_list[4].kwargs["messages"][-1]["content"][0]
    assert fourth.get("is_error") is True and "budget" in fourth["content"].lower()
    assert answer.tool_calls == 4 and answer.text == "Final [doc 1]."


def test_invalid_citation_is_rerequested_then_stripped_and_flagged():
    session, _, _ = make_session([message(text("See [doc 9].")), message(text("Still [doc 9] and [doc 1]."))])
    answer = session.run("q?", "tok", trace_id="t")
    assert answer.text == "Still and [doc 1]." or answer.text == "Still  and [doc 1]."
    assert [c.key for c in answer.citations] == ["A"]
    assert any("doc 9" in f for f in answer.flags)


def test_refusal_raises():
    session, _, _ = make_session([message(stop_reason="refusal")])
    with pytest.raises(ModelRefused):
        session.run("q?", "tok", trace_id="t")


def test_empty_retrieval_tells_the_model_explicitly():
    session, anthropic, _ = make_session([message(text("I cannot answer from the documents."))],
                                         chunks=[[]] * 10, entities=[[]] * 10)
    answer = session.run("q?", "tok", trace_id="t")
    ctx = anthropic.messages.create.call_args.kwargs["messages"][0]["content"]
    assert "No documents were found for: q" in ctx
    assert answer.citations == []
```

- [ ] **Step 2: Run them** — ImportError.

- [ ] **Step 3: Write `iverson_agent/session.py`.**
```python
"""One request = one AgentSession.run (§4): plan → locate → assemble → reason, with a bounded
tool-calling escape hatch (§4.5) and enforced citations (§4.6)."""
from __future__ import annotations

import base64
import json
import logging
import re
from dataclasses import dataclass, field

from iverson_client import IversonClient
from iverson_agent.config import AgentConfig
from iverson_agent.planner import plan
from iverson_agent.retrieval import (
    DocumentContext, NoAccessibleDocuments, assemble, estimate_tokens, locate, render_context,
)
from iverson_agent.schema import SchemaCache, render_schema, resolve_type, validate_filters

log = logging.getLogger(__name__)

REASONER_SYSTEM = """You answer the user's question using only the numbered documents provided.

- Cite every claim with the document number in the form [doc n]. Cite only documents shown to you.
- If the documents do not contain the answer, say so plainly instead of guessing.
- You may call search_more when a specific, nameable gap in the documents blocks the answer, and
  expand_document when a shown document clearly contains more relevant text than the passages
  shown. Do not call tools speculatively. When told the retrieval budget is exhausted, answer
  from the documents shown."""

TOOLS = [
    {
        "name": "search_more",
        "description": ("Retrieve further documents for a new query. filters are equality "
                        "conditions on the filterable field names exactly as shown on the page "
                        "(for example PublishedAt). Returns only documents not already shown."),
        "input_schema": {
            "type": "object",
            "properties": {
                "query_text": {"type": "string"},
                "filters": {"type": "array", "items": {
                    "type": "object",
                    "properties": {"field": {"type": "string"},
                                   "value": {"type": ["string", "number", "boolean"]}},
                    "required": ["field", "value"], "additionalProperties": False}},
            },
            "required": ["query_text", "filters"], "additionalProperties": False,
        },
    },
    {
        "name": "expand_document",
        "description": "Fetch more passages from a document already shown, for a fresh query.",
        "input_schema": {
            "type": "object",
            "properties": {"doc_number": {"type": "integer"}, "query_text": {"type": "string"}},
            "required": ["doc_number", "query_text"], "additionalProperties": False,
        },
    },
]

BUDGET_EXHAUSTED = "Retrieval budget exhausted; answer from the documents shown."
_CITATION = re.compile(r"\[doc (\d+)\]")


class ModelRefused(Exception):
    """The model returned stop_reason == "refusal" (§6)."""


@dataclass(frozen=True)
class Citation:
    doc_number: int
    key: str
    title: str | None
    passages: list[str]


@dataclass
class AgentAnswer:
    text: str
    citations: list[Citation]
    tool_calls: int
    context_tokens: int
    flags: list[str] = field(default_factory=list)


def _user_key(token: str) -> str:
    """The JWT subject when the token parses as one; otherwise the token itself."""
    try:
        payload = token.split(".")[1]
        claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
        return str(claims.get("sub") or token)
    except Exception:  # noqa: BLE001 — any malformed token keys on itself
        return token


class AgentSession:
    def __init__(self, anthropic_client, iverson_client: IversonClient, entity_cls: type,
                 config: AgentConfig, schema_client_factory, title_field: str | None = "title",
                 chunk_property: str | None = None) -> None:
        meta = entity_cls._iverson_meta
        chunk_fields = [f for f, *_ in meta["chunk_fields"]]
        if chunk_property is None:
            if len(chunk_fields) != 1:
                raise ValueError(f"{meta['type_name']} declares {len(chunk_fields)} chunk fields; "
                                 "pass chunk_property explicitly")
            chunk_property = chunk_fields[0]
        from iverson_client.core import _to_pascal_case
        self._anthropic = anthropic_client
        self._iverson = iverson_client
        self._cls = entity_cls
        self._type_name: str = meta["type_name"]
        self._chunk_property = _to_pascal_case(chunk_property)
        self._title_field = title_field
        self._cfg = config
        self._schemas = SchemaCache(ttl=config.schema_ttl, client_factory=schema_client_factory)

    # ── one request ─────────────────────────────────────────────────────────

    def run(self, question: str, end_user_token: str, trace_id: str = "") -> AgentAnswer:
        cfg = self._cfg
        docs = self._iverson.coordinator(self._cls).with_acting_user(end_user_token)
        schema_type = resolve_type(self._schemas.get(_user_key(end_user_token), end_user_token),
                                   self._type_name)

        # 4.1 plan
        planned = plan(self._anthropic, cfg.model, render_schema(schema_type), question)
        queries = [(q.query_text, validate_filters([(f.field, f.value) for f in q.filters],
                                                   schema_type, trace_id))
                   for q in planned.queries]

        # 4.2 + 4.3
        contexts, empty = self._retrieve(docs, queries, question, trace_id)
        if not contexts and len(empty) == len(queries):
            log.info("[session] trace=%s nothing retrieved for any query", trace_id)
        state = _State(contexts=contexts, empty_queries=empty)

        # 4.4 reason (+ 4.5 tools)
        messages = [{"role": "user", "content": self._page(state, question)}]
        state.context_tokens = estimate_tokens(messages[0]["content"])
        response = self._create(messages)
        while response.stop_reason == "tool_use":
            messages.append({"role": "assistant", "content": response.content})
            results = []
            for block in (b for b in response.content if b.type == "tool_use"):
                state.tool_calls += 1
                if state.tool_calls > cfg.max_tool_calls or state.context_tokens > cfg.context_tokens:
                    results.append({"type": "tool_result", "tool_use_id": block.id,
                                    "is_error": True, "content": BUDGET_EXHAUSTED})
                    continue
                content = self._run_tool(block.name, block.input, state, docs, schema_type, question, trace_id)
                state.context_tokens += estimate_tokens(content)
                results.append({"type": "tool_result", "tool_use_id": block.id, "content": content})
            messages.append({"role": "user", "content": results})
            response = self._create(messages)

        # 4.6 output with enforced citations
        text = _text_of(response)
        invalid = _invalid_citations(text, len(state.contexts))
        flags: list[str] = []
        if invalid:
            messages.append({"role": "assistant", "content": response.content})
            messages.append({"role": "user", "content":
                             f"Your answer cites {', '.join(f'[doc {n}]' for n in invalid)}, which "
                             "was not among the documents shown. Answer again citing only shown documents."})
            response = self._create(messages)
            text = _text_of(response)
            invalid = _invalid_citations(text, len(state.contexts))
            for n in invalid:
                text = text.replace(f"[doc {n}]", "").replace("  ", " ")
                flags.append(f"stripped invalid citation [doc {n}]")
        cited = sorted({int(n) for n in _CITATION.findall(text)})
        citations = [Citation(n, state.contexts[n - 1].key, state.contexts[n - 1].title,
                              [t for _, t in state.contexts[n - 1].passages]) for n in cited]
        return AgentAnswer(text=text, citations=citations, tool_calls=state.tool_calls,
                           context_tokens=state.context_tokens, flags=flags)

    # ── helpers ─────────────────────────────────────────────────────────────

    def _create(self, messages):
        response = self._anthropic.messages.create(
            model=self._cfg.model, max_tokens=16000, system=REASONER_SYSTEM,
            tools=TOOLS, messages=messages)
        if response.stop_reason == "refusal":
            raise ModelRefused()
        return response

    def _retrieve(self, docs, queries, question, trace_id):
        parents, empty = locate(docs, self._type_name, self._chunk_property, queries,
                                self._cfg.k, self._cfg.fanout, trace_id)
        contexts = assemble(docs, self._cls, self._type_name, self._chunk_property, parents,
                            question, self._cfg.m, trace_id, self._title_field) if parents else []
        return contexts, empty

    def _page(self, state: "_State", question: str) -> str:
        parts = []
        if state.contexts:
            parts.append(render_context(state.contexts, self._cfg.context_tokens))
        for q in state.empty_queries:
            parts.append(f"No documents were found for: {q}")
        if not state.contexts and not state.empty_queries:
            parts.append("No documents were found.")
        parts.append(f"Question: {question}")
        return "\n\n".join(parts)

    def _run_tool(self, name, inp, state, docs, schema_type, question, trace_id) -> str:
        if name == "search_more":
            filters = validate_filters([(f["field"], f["value"]) for f in inp.get("filters", [])],
                                       schema_type, trace_id)
            new_contexts, empty = self._retrieve(docs, [(inp["query_text"], filters)],
                                                 inp["query_text"], trace_id)
            known = {c.key for c in state.contexts}
            fresh = [c for c in new_contexts if c.key not in known]
            if not fresh:
                return f"No new documents were found for: {inp['query_text']}"
            start = len(state.contexts)
            state.contexts.extend(fresh)
            return "\n\n".join(_render_block(start + i + 1, c) for i, c in enumerate(fresh))
        if name == "expand_document":
            n = int(inp["doc_number"])
            if not 1 <= n <= len(state.contexts):
                return f"[doc {n}] is not a document shown to you."
            target = state.contexts[n - 1]
            from iverson_agent.retrieval import RankedParent
            refreshed = assemble(docs, self._cls, self._type_name, self._chunk_property,
                                 [RankedParent(target.key, target.best_score, [])],
                                 inp["query_text"], self._cfg.m, trace_id, self._title_field)
            shown = {t for _, t in target.passages}
            new_passages = [(s, t) for c in refreshed for s, t in c.passages if t not in shown]
            if not new_passages:
                return f"[doc {n}] has no further passages for that query."
            target.passages.extend(new_passages)
            return "\n".join(f"[doc {n}] passage: {t}" for _, t in new_passages)
        return f"Unknown tool {name}."


@dataclass
class _State:
    contexts: list[DocumentContext]
    empty_queries: list[str]
    tool_calls: int = 0
    context_tokens: int = 0


def _render_block(n: int, c: DocumentContext) -> str:
    from iverson_agent.retrieval import _render_one
    return _render_one(n, c, c.passages)


def _text_of(response) -> str:
    return "".join(b.text for b in response.content if b.type == "text").strip()


def _invalid_citations(text: str, shown: int) -> list[int]:
    return sorted({int(n) for n in _CITATION.findall(text) if not 1 <= int(n) <= shown})
```

- [ ] **Step 4: Export from `__init__.py`.**
```python
"""Reasoning agent over Iverson — see docs/specs/2026-09-03-iverson-reasoning-agent-design.md."""
from iverson_agent.config import AgentConfig
from iverson_agent.session import AgentAnswer, AgentSession

__all__ = ["AgentConfig", "AgentAnswer", "AgentSession"]
```

- [ ] **Step 5: Run the whole suite** — `.venv/bin/python -m pytest -q` → all green (2 + 6 + 8 + 2 + 7 = 25). If `test_invalid_citation_is_rerequested_then_stripped_and_flagged` fails only on whitespace, fix the strip in `run` (collapse the double space), not the assertion.

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Agents/Python/iverson_agent/session.py Iverson.Agents/Python/iverson_agent/__init__.py Iverson.Agents/Python/tests/test_session.py
git commit -m "add the agent session: reasoning loop, bounded retrieval tools, and enforced citations"
```

### Task 6: Evaluation script and CLI

**Files:**
- Create: `Iverson.Agents/Python/iverson_agent/evaluate.py`, `Iverson.Agents/Python/iverson_agent/__main__.py`, `Iverson.Agents/Python/tests/test_evaluate.py`

**Interfaces:**
- Consumes: `AgentSession`, `AgentAnswer` (T5); `AgentConfig` (T1).

- [ ] **Step 1: Write the failing tests** — `tests/test_evaluate.py`:
```python
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

from iverson_agent.session import AgentAnswer, Citation
from iverson_agent.evaluate import EvalItem, evaluate, judge_grounding


def answer(text, keys, tool_calls=0, tokens=100):
    return AgentAnswer(text=text, citations=[Citation(i + 1, k, None, ["p"]) for i, k in enumerate(keys)],
                       tool_calls=tool_calls, context_tokens=tokens)


def test_metrics_over_items():
    session = MagicMock()
    session.run.side_effect = [answer("a [doc 1]", ["A"], 1, 200), answer("cannot answer", [], 0, 50)]
    judge = MagicMock(return_value=(2, 2))
    items = [EvalItem("q1", ["A", "B"], ["fact"]), EvalItem("q2", [], [])]
    report = evaluate(session, items, "tok", judge)
    assert report["citation_precision"] == 1.0
    assert report["citation_recall"] == pytest.approx(0.5)          # A of {A,B}; unanswerable items excluded
    assert report["grounding"] == pytest.approx(1.0)
    assert report["insufficiency_honesty"] == 1.0                   # "cannot" appears, no citations
    assert report["tool_calls"] == [1, 0] and report["context_tokens"] == [200, 50]


def test_judge_parses_structured_output():
    client = MagicMock()
    client.messages.parse.return_value = SimpleNamespace(
        parsed_output=SimpleNamespace(supported_claims=3, total_claims=4))
    assert judge_grounding(client, "claude-opus-5", "answer", ["p1"]) == (3, 4)
```

- [ ] **Step 2: Run them** — ImportError.

- [ ] **Step 3: Write `iverson_agent/evaluate.py`.**
```python
"""§7.2 agent-layer evaluation over a hand-labelled JSONL set:
{"question": ..., "expected_keys": [...], "expected_facts": [...]} per line."""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from pydantic import BaseModel

INSUFFICIENT_MARKERS = ("cannot", "can't", "not contain", "no information", "unable")


@dataclass(frozen=True)
class EvalItem:
    question: str
    expected_keys: list[str]
    expected_facts: list[str]


class Grounding(BaseModel):
    supported_claims: int
    total_claims: int


JUDGE_SYSTEM = """You check whether an answer is grounded in the passages it cites. Split the
answer into its factual claims. Count how many are directly supported by the passages."""


def judge_grounding(client, model: str, answer_text: str, passages: list[str]) -> tuple[int, int]:
    response = client.messages.parse(
        model=model, max_tokens=1024, system=JUDGE_SYSTEM,
        messages=[{"role": "user", "content":
                   "Passages:\n" + "\n---\n".join(passages) + f"\n\nAnswer:\n{answer_text}"}],
        output_format=Grounding)
    g = response.parsed_output
    return g.supported_claims, g.total_claims


def load_items(path: Path) -> list[EvalItem]:
    return [EvalItem(d["question"], list(d.get("expected_keys", [])), list(d.get("expected_facts", [])))
            for d in (json.loads(line) for line in path.read_text().splitlines() if line.strip())]


def evaluate(session, items: list[EvalItem], end_user_token: str,
             judge: Callable[[str, list[str]], tuple[int, int]]) -> dict:
    recall_hits: list[float] = []
    supported = total = 0
    honest = unanswerable = 0
    tool_calls: list[int] = []
    tokens: list[int] = []
    for i, item in enumerate(items):
        a = session.run(item.question, end_user_token, trace_id=f"eval-{i}")
        cited = {c.key for c in a.citations}
        # Citation precision is enforced by construction (§4.6): assert, don't measure.
        assert all(c.doc_number >= 1 for c in a.citations), "citation outside context"
        if item.expected_keys:
            recall_hits.append(len(cited & set(item.expected_keys)) / len(item.expected_keys))
        else:
            unanswerable += 1
            if not cited and any(m in a.text.lower() for m in INSUFFICIENT_MARKERS):
                honest += 1
        if a.citations:
            s, t = judge(a.text, [p for c in a.citations for p in c.passages])
            supported, total = supported + s, total + t
        tool_calls.append(a.tool_calls)
        tokens.append(a.context_tokens)
    return {
        "items": len(items),
        "citation_precision": 1.0,
        "citation_recall": sum(recall_hits) / len(recall_hits) if recall_hits else None,
        "grounding": supported / total if total else None,
        "insufficiency_honesty": honest / unanswerable if unanswerable else None,
        "tool_calls": tool_calls,
        "context_tokens": tokens,
    }
```

- [ ] **Step 4: Write `iverson_agent/__main__.py`.** Environment names match `bench-env.sh` / `Iverson.LoadTest` (`IVERSON_GRPC_URL`, `IVERSON_CLIENT_ID`, `IVERSON_CLIENT_SECRET`, `IVERSON_CLIENT_SCOPE`, `IVERSON_TOKEN_ENDPOINT`, `IVERSON_ACTING_USER_TOKEN`). The entity class is loaded from a `module:Class` string because the agent binds to one registered type.
```python
"""CLI: `python -m iverson_agent ask --entity pkg.models:PolicyDoc --question "..."`
       `python -m iverson_agent evaluate --entity ... --items labelled.jsonl`"""
from __future__ import annotations

import argparse
import importlib
import json
import os
import sys
from pathlib import Path
from urllib.parse import urlsplit

import anthropic

from iverson_client import IversonClient, IversonClientCredentials
from iverson_agent.config import AgentConfig
from iverson_agent.evaluate import evaluate, judge_grounding, load_items
from iverson_agent.session import AgentSession


def _credentials() -> IversonClientCredentials:
    return IversonClientCredentials(
        client_id=os.environ["IVERSON_CLIENT_ID"], client_secret=os.environ["IVERSON_CLIENT_SECRET"],
        token_endpoint=os.environ["IVERSON_TOKEN_ENDPOINT"], scope=os.environ.get("IVERSON_CLIENT_SCOPE"))


def _endpoint() -> tuple[str, int, bool]:
    url = urlsplit(os.environ.get("IVERSON_GRPC_URL", "http://localhost:8080"))
    return url.hostname or "localhost", url.port or (443 if url.scheme == "https" else 8080), url.scheme == "https"


def _entity(spec: str) -> type:
    module, _, name = spec.partition(":")
    return getattr(importlib.import_module(module), name)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="iverson_agent")
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("ask", "evaluate"):
        p = sub.add_parser(name)
        p.add_argument("--entity", required=True, help="module:Class of the @iverson_entity to retrieve over")
        p.add_argument("--title-field", default="title")
        if name == "ask":
            p.add_argument("--question", required=True)
            p.add_argument("--trace-id", default="")
        else:
            p.add_argument("--items", required=True, type=Path)
    args = ap.parse_args(argv)

    host, port, tls = _endpoint()
    creds = _credentials()
    token = os.environ["IVERSON_ACTING_USER_TOKEN"]
    cfg = AgentConfig()
    client = anthropic.Anthropic()
    with IversonClient(host, port, use_tls=tls, credentials=creds) as iverson:
        session = AgentSession(
            client, iverson, _entity(args.entity), cfg,
            schema_client_factory=lambda t: IversonClient(host, port, use_tls=tls, credentials=creds,
                                                          acting_user_token=t),
            title_field=args.title_field)
        if args.cmd == "ask":
            answer = session.run(args.question, token, trace_id=args.trace_id)
            print(answer.text)
            for c in answer.citations:
                print(f"  [doc {c.doc_number}] {c.key} {c.title or ''}")
            for f in answer.flags:
                print(f"  ! {f}", file=sys.stderr)
            return 0
        report = evaluate(session, load_items(args.items), token,
                          lambda text, passages: judge_grounding(client, cfg.model, text, passages))
        print(json.dumps(report, indent=2))
        return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 5: Run the suite** — `.venv/bin/python -m pytest -q` → 27 passed. Also `.venv/bin/python -m iverson_agent --help` prints both subcommands.

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Agents/Python/iverson_agent/evaluate.py Iverson.Agents/Python/iverson_agent/__main__.py Iverson.Agents/Python/tests/test_evaluate.py
git commit -m "add the agent-layer evaluation and the ask/evaluate command line"
```

### Task 7: §7.1 fanout sweep on SciFact and the verdict

**Files:**
- Modify (per arm, **never committed**): `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs:40-41`
- Create (outside this repo, untracked): `~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/runs/agent-k5-f{5,4,6,8}.{chunks,similar}.trec`, `agent-k5-f*.meta.json`, `report-agent-fanout-2026-09.txt`
- Create: `docs/plans/2026-09-FANOUT-reasoning-agent.md` (`git add -f`)
- Modify (conditional): `Iverson.Agents/Python/iverson_agent/config.py` — `fanout` default

This task is operational: it needs the compose query tier, the SciFact snapshot restored, and one
`benchmark-query` run per arm (each is a rebuild). `ChunkBudgetGuard` refuses `×2` and `×3` on
this corpus (assumption 28), so the arms are `×5` (baseline, the current default multiplier), `×4`
(the agent's default), `×6`, `×8` — all at `DocumentBudget = 5` so the harness issues the agent's
exact request (spec §7.1).

- [ ] **Step 1: Bring up the query tier and restore SciFact.**
```bash
cd /home/ben/repositories/Iverson
python3 Iverson.Server/Iverson.LoadTest/scripts/stack.py query
cd ~/repositories/iverson-benchmark-corpora/scifact-512-qdrant-snapshots
K=dev-only-not-for-production-qdrant-key-0123456789
for f in *.snapshot; do c="${f%%-6802952876034638*}"; curl -X POST -H "api-key: $K" "http://localhost:6333/collections/$c/snapshots/upload?priority=snapshot" -F "snapshot=@$f"; done
for c in benchmark_documents_tenant_bypass benchmark_documents_chunks_tenant_bypass; do curl -s -H "api-key: $K" http://localhost:6333/collections/$c | python3 -c "import json,sys; print('$c', json.load(sys.stdin)['result']['points_count'])"; done
```
Expect `5183` and `19967`. `export RUN=~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26` and `source /home/ben/iverson-benchmark-data/bench-env.sh`.

- [ ] **Step 2: Run the four arms.** Each arm edits the two constants, runs, and the file is only
restored at the end (step 3). From `Iverson.Server/Iverson.LoadTest`:
```bash
F=Scenarios/BenchmarkQueryScenario.cs
for mult in 5 4 6 8; do
  sed -i -E 's/(private const int +DocumentBudget += )[0-9]+;/\15;/; s/(private const int +ChunkBudgetMultiplier = )[0-9]+;/\1'"$mult"';/' $F
  grep -nE "DocumentBudget += |ChunkBudgetMultiplier = " $F     # must show 5 and $mult
  dotnet run -c Release -- benchmark-query --corpus-path $RUN --key-map-path $RUN/keymap.json --output-dir $RUN/runs --config-label agent-k5-f$mult
done
```
A `REFUSING: chunk budget cannot reach DocumentBudget distinct documents` exit on any arm means the
guard fired — record it in the verdict and do not score that arm.

- [ ] **Step 3: Restore the scenario file.** `git checkout -- Scenarios/BenchmarkQueryScenario.cs && git status --short` must show it clean. The edits are never committed.

- [ ] **Step 4: Score against the ×5 baseline.**
```bash
cd $RUN/runs
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 \
  /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts/report.py \
  --qrels $RUN/qrels.trec \
  --run agent-k5-f5.chunks.trec --run agent-k5-f4.chunks.trec --run agent-k5-f6.chunks.trec --run agent-k5-f8.chunks.trec \
  --baseline agent-k5-f5.chunks.trec | tee report-agent-fanout-2026-09.txt
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 \
  /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts/report.py \
  --qrels $RUN/qrels.trec \
  --run agent-k5-f8.chunks.trec --baseline agent-k5-f6.chunks.trec | tee -a report-agent-fanout-2026-09.txt
```
The first invocation compares `×4`, `×6`, `×8` each against `×5`; the second produces the 8-vs-6
comparison step 5 needs. Each invocation is its own Holm family.
Per spec §7.1: unless the reranker phase-1 plan's Task 1 (`list(...)` at `report.py:552`) has
landed — check `grep -n "list(ir_measures.read_trec_run(baseline_path))" …/report.py` — **only the
nDCG@10 rows of the paired section are trustworthy**; the per-run R@50/AP absolute scores are fine.
The ranking is 5 deep, so nDCG@10 is scored over a 5-deep list (ranks 6–10 contribute zero).

- [ ] **Step 5: Record the verdict** in `docs/plans/2026-09-FANOUT-reasoning-agent.md`: the four
arms' per-run nDCG@10 / R@50 / AP; for `×4`, `×6`, `×8` vs `×5` and for `×8` vs `×6` on nDCG@10 the
delta, paired t, permutation p, Holm p_adj, 95 % CI, d_z, queries changed; the three next-smaller
comparisons the plateau rule reads, named explicitly: 5-vs-4 (the `×5`-family's 4-vs-5 row with the
sign flipped), 6-vs-5 (same family), 8-vs-6 (the second invocation); whether the guard refused any arm; the
server build composite from the `.meta.json` sidecars (must be identical across arms — the harness
edit does not change it); and one line: **fanout plateau = N** (the smallest multiplier at which
nDCG@10 stops improving with Holm p_adj < 0.05 against the next-smaller arm; if no arm differs
significantly, the plateau is the smallest arm run, `4`).

- [ ] **Step 6: Apply the plateau and commit.** If the plateau is not 4, change `fanout: int = 4`
in `Iverson.Agents/Python/iverson_agent/config.py` and the `test_defaults_match_spec_section_5`
expectation in `tests/test_config.py` to the plateau, run `.venv/bin/python -m pytest -q`, and
include both files in the commit. The corpora directory is not a git repository; the run files and
the report capture stay there untracked.
```bash
cd /home/ben/repositories/Iverson
git add -f docs/plans/2026-09-FANOUT-reasoning-agent.md
git commit -m "record the reasoning-agent fanout sweep verdict" -- docs/plans/2026-09-FANOUT-reasoning-agent.md Iverson.Agents/Python/iverson_agent/config.py Iverson.Agents/Python/tests/test_config.py
```

## Tasks NOT in this plan

Inherited verbatim from the spec (§1):

Out of scope: ingestion pipelines, any change to the Iverson server or client libraries, and the choice of front end that supplies the question and the end user's token.

Also outside this plan, by the user's choice at plan time: the §7.2 labelled question set and its end-to-end run (the `evaluate` subcommand is delivered; the set is authored against a real corpus).

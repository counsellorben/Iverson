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
    DocumentContext, assemble, estimate_tokens, locate, render_context,
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
        "strict": True,
    },
    {
        "name": "expand_document",
        "description": "Fetch more passages from a document already shown, for a fresh query.",
        "input_schema": {
            "type": "object",
            "properties": {"doc_number": {"type": "integer"}, "query_text": {"type": "string"}},
            "required": ["doc_number", "query_text"], "additionalProperties": False,
        },
        "strict": True,
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
            budget_exhausted_this_turn = False
            for block in (b for b in response.content if b.type == "tool_use"):
                state.tool_calls += 1
                if state.tool_calls > cfg.max_tool_calls or state.context_tokens > cfg.context_tokens:
                    results.append({"type": "tool_result", "tool_use_id": block.id,
                                    "is_error": True, "content": BUDGET_EXHAUSTED})
                    budget_exhausted_this_turn = True
                    continue
                content = self._run_tool(block.name, block.input, state, docs, schema_type, question, trace_id)
                state.context_tokens += estimate_tokens(content)
                results.append({"type": "tool_result", "tool_use_id": block.id, "content": content})
            messages.append({"role": "user", "content": results})
            # Once any call this turn hit the budget guard, force the next response to be a final
            # answer (tool_choice="none") — the model is not trusted to stop on its own (§4.5).
            response = self._create(messages, force_answer=budget_exhausted_this_turn)

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
                text = _strip_citation(text, n).replace("  ", " ")
                flags.append(f"stripped invalid citation [doc {n}]")
        # Bounded by construction: only numbers that index an actual shown document ever reach
        # state.contexts, so an unstripped/leftover invalid citation can never raise IndexError.
        cited = sorted({int(n) for n in _CITATION.findall(text) if 1 <= int(n) <= len(state.contexts)})
        citations = [Citation(n, state.contexts[n - 1].key, state.contexts[n - 1].title,
                              [t for _, t in state.contexts[n - 1].passages]) for n in cited]
        return AgentAnswer(text=text, citations=citations, tool_calls=state.tool_calls,
                           context_tokens=state.context_tokens, flags=flags)

    # ── helpers ─────────────────────────────────────────────────────────────

    def _create(self, messages, force_answer: bool = False):
        kwargs = dict(model=self._cfg.model, max_tokens=16000, system=REASONER_SYSTEM,
                      tools=TOOLS, messages=messages)
        if force_answer:
            kwargs["tool_choice"] = {"type": "none"}
        response = self._anthropic.messages.create(**kwargs)
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
            target.passages.sort(key=lambda c: c[0], reverse=True)   # keep best-first (same invariant as
                                                                       # assemble's top-up merge, §4.3)
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


def _strip_citation(text: str, n: int) -> str:
    """Remove every spelling of citation n (leading zeros included, e.g. "[doc 09]") so the
    detector (which normalizes via int()) and the strip step agree on what "n" matched (Ruling 5)."""
    return re.sub(rf"\[doc 0*{n}\]", "", text)

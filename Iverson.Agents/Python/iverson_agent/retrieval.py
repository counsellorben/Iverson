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

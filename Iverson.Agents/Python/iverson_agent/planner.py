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

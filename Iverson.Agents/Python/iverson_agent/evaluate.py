"""§7.2 agent-layer evaluation over a hand-labelled JSONL set:
{"question": ..., "expected_keys": [...], "expected_facts": [...]} per line."""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from pydantic import BaseModel

INSUFFICIENT_MARKERS = ("cannot", "can't", "not contain", "no information", "unable")


class CitationOutsideContext(Exception):
    """A cited key was not among the documents on the page — §4.6 was violated (§7.2)."""


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
        # Citation precision is enforced by construction (§4.6), so §7.2 checks it rather than
        # measuring it — a real check, not an assert, because any violation is a bug.
        outside = sorted(cited - set(a.context_keys))
        if outside:
            raise CitationOutsideContext(
                f"item {i}: cited {outside} which are not among the documents shown")
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

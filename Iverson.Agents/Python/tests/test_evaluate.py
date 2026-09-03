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
    judge = MagicMock(side_effect=[(2, 2)])
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

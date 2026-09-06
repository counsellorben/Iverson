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
    calls = anthropic.messages.create.call_args_list
    fourth = calls[4].kwargs["messages"][-1]["content"][0]
    assert fourth.get("is_error") is True and "budget" in fourth["content"].lower()
    # Only the call that follows the budget-exhausted turn is forced to a final answer; the three
    # tool-executing turns before it keep the tools selectable.
    assert [c.kwargs.get("tool_choice") for c in calls[:4]] == [None] * 4
    assert calls[4].kwargs.get("tool_choice") == {"type": "none"}
    # tool_calls counts EXECUTED calls: the fourth block was refused by the budget guard.
    assert answer.tool_calls == 3 and answer.text == "Final [doc 1]."


def test_invalid_citation_is_rerequested_then_stripped_and_flagged():
    session, _, _ = make_session([message(text("See [doc 9].")), message(text("Still [doc 9] and [doc 1]."))])
    answer = session.run("q?", "tok", trace_id="t")
    assert answer.text == "Still and [doc 1]." or answer.text == "Still  and [doc 1]."
    assert [c.key for c in answer.citations] == ["A"]
    assert any("doc 9" in f for f in answer.flags)


def test_citation_rerequest_forces_a_final_answer():
    # Without tool_choice="none" the model may answer the re-request with a tool_use block: the
    # tool loop has already exited, so _text_of() would be empty and run() would return an empty
    # answer with no error (§4.6).
    session, anthropic, _ = make_session([message(text("See [doc 9].")), message(text("Fine [doc 1]."))])
    answer = session.run("q?", "tok", trace_id="t")
    assert answer.text == "Fine [doc 1]." and [c.key for c in answer.citations] == ["A"]
    calls = anthropic.messages.create.call_args_list
    assert calls[0].kwargs.get("tool_choice") is None
    assert calls[1].kwargs.get("tool_choice") == {"type": "none"}


def test_system_prompt_is_a_cacheable_block():
    session, anthropic, _ = make_session([message(text("Leave is 20 days [doc 1]."))])
    session.run("q?", "tok", trace_id="t")
    system = anthropic.messages.create.call_args.kwargs["system"]
    assert system[0]["type"] == "text" and system[0]["cache_control"] == {"type": "ephemeral"}


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


def test_non_canonical_citation_is_stripped_without_indexerror():
    # "[doc 09]" normalizes to 9 for detection (int("09") == 9), which is out of range for a
    # single-document context. Before the fix, the strip loop's literal `text.replace("[doc 9]", "")`
    # never matched the "09" spelling, so the un-stripped "[doc 09]" survived into the final
    # `cited` computation and indexed state.contexts[8] on a 1-long list, raising IndexError.
    session, _, _ = make_session([message(text("See [doc 09].")), message(text("Still [doc 09] and [doc 1]."))])
    answer = session.run("q?", "tok", trace_id="t")
    assert answer.text == "Still and [doc 1]." or answer.text == "Still  and [doc 1]."
    assert [c.key for c in answer.citations] == ["A"]
    assert any("doc 9" in f for f in answer.flags)


def test_search_more_filters_are_validated_and_sent_canonically():
    session, _, coord = make_session(
        [message(tool_use("search_more", query_text="q2",
                          filters=[{"field": "source", "value": "legal"},
                                   {"field": "Title", "value": "no"}]),
                stop_reason="tool_use"),
         message(text("Done [doc 3]."))],
        chunks=[[chunk("A", "a1", 0.9), chunk("B", "b1", 0.5)], [chunk("A", "a1", 0.9), chunk("C", "c1", 0.7)]],
        entities=[[doc("A"), doc("B")], [doc("A"), doc("C")]],
        cfg=AgentConfig(m=1))
    session.run("q?", "tok", trace_id="t")
    req = coord.search_chunks.call_args_list[1].args[0]
    assert [(c.property, c.value.string_val) for c in req.filter] == [("Source", "legal")]


def test_expand_document_keeps_passages_best_first_after_topup():
    # target.passages starts as [(0.9, "a1")]; expand_document's top-up returns a *higher*-scored
    # passage ("a-new", 0.99). A plain extend() would leave [(0.9, "a1"), (0.99, "a-new")] —
    # out of best-first order, the same invariant Task 3 fixed for assemble's own top-up.
    session, _, _ = make_session(
        [message(tool_use("expand_document", doc_number=1, query_text="more"), stop_reason="tool_use"),
         message(text("Done [doc 1]."))],
        chunks=[[chunk("A", "a1", 0.9), chunk("B", "b1", 0.5)], [chunk("A", "a-new", 0.99)]],
        entities=[[doc("A"), doc("B")], [doc("A")]],
        cfg=AgentConfig(m=1))
    answer = session.run("q?", "tok", trace_id="t")
    assert answer.citations[0].passages == ["a-new", "a1"]

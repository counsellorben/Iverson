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


def test_plan_passes_one_to_three_queries_through_unchanged():
    client = MagicMock()
    for n in (1, 2, 3):
        queries = [RetrievalQuery(query_text=f"q{i}", filters=[Filter(field="Source", value="legal")] if i else [])
                   for i in range(n)]
        client.messages.parse.return_value = SimpleNamespace(parsed_output=Plan(queries=queries))
        assert plan(client, "claude-opus-5", "Type: PolicyDoc", "leave policy").queries == queries

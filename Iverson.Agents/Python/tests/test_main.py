import json
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

import iverson_agent.__main__ as cli
from iverson_agent.session import AgentAnswer, Citation
from tests.test_session import PolicyDoc


def test_endpoint_scheme_selects_tls_and_default_port(monkeypatch):
    monkeypatch.delenv("IVERSON_GRPC_URL", raising=False)
    assert cli._endpoint() == ("localhost", 8080, False)
    monkeypatch.setenv("IVERSON_GRPC_URL", "https://iverson.example.com")
    assert cli._endpoint() == ("iverson.example.com", 443, True)
    monkeypatch.setenv("IVERSON_GRPC_URL", "http://api:9090")
    assert cli._endpoint() == ("api", 9090, False)
    monkeypatch.setenv("IVERSON_GRPC_URL", "grpc://api:9090")
    with pytest.raises(ValueError, match="http:// or https://"):
        cli._endpoint()


def test_entity_resolves_a_module_class_spec_and_explains_a_bad_one():
    assert cli._entity("tests.test_session:PolicyDoc") is PolicyDoc
    with pytest.raises(ValueError, match="module:Class"):
        cli._entity("tests.test_session")
    with pytest.raises(ValueError, match="could not be resolved"):
        cli._entity("tests.test_session:NoSuchClass")
    with pytest.raises(ValueError, match="could not be resolved"):
        cli._entity("no.such.module:X")
    with pytest.raises(ValueError, match="not an @iverson_entity"):
        cli._entity("tests.test_session:make_session")


def _env(monkeypatch):
    monkeypatch.setenv("IVERSON_CLIENT_ID", "id")
    monkeypatch.setenv("IVERSON_CLIENT_SECRET", "secret")
    monkeypatch.setenv("IVERSON_TOKEN_ENDPOINT", "https://idp/token")
    monkeypatch.setenv("IVERSON_ACTING_USER_TOKEN", "user-jwt")
    monkeypatch.setenv("IVERSON_GRPC_URL", "https://iverson.example.com:8443")
    monkeypatch.setattr(cli.anthropic, "Anthropic", MagicMock())
    client_cls = MagicMock()
    monkeypatch.setattr(cli, "IversonClient", client_cls)
    session_cls = MagicMock()
    monkeypatch.setattr(cli, "AgentSession", session_cls)
    return client_cls, session_cls


def test_ask_prints_the_answer_citations_and_flags(monkeypatch, capsys):
    client_cls, session_cls = _env(monkeypatch)
    session_cls.return_value.run.return_value = AgentAnswer(
        text="Leave is 20 days [doc 1].", citations=[Citation(1, "A", "Leave policy", ["p"])],
        tool_calls=0, context_tokens=10, flags=["stripped invalid citation [doc 9]"])
    rc = cli.main(["ask", "--entity", "tests.test_session:PolicyDoc", "--question", "How much leave?",
                   "--trace-id", "t1"])
    out = capsys.readouterr()
    assert rc == 0
    assert out.out == "Leave is 20 days [doc 1].\n  [doc 1] A Leave policy\n"
    assert out.err == "  ! stripped invalid citation [doc 9]\n"
    session_cls.return_value.run.assert_called_once_with("How much leave?", "user-jwt", trace_id="t1")
    # The gRPC endpoint comes from IVERSON_GRPC_URL (TLS on https), the entity from --entity.
    assert client_cls.call_args_list[0].args == ("iverson.example.com", 8443)
    assert client_cls.call_args_list[0].kwargs["use_tls"] is True
    assert session_cls.call_args.args[2] is PolicyDoc
    assert session_cls.call_args.kwargs["title_field"] == "title"
    # The per-user schema client carries the acting-user token it is asked for.
    session_cls.call_args.kwargs["schema_client_factory"]("other-jwt")
    assert client_cls.call_args.kwargs["acting_user_token"] == "other-jwt"


def test_evaluate_prints_the_report_as_json(monkeypatch, capsys, tmp_path):
    _, session_cls = _env(monkeypatch)
    items = tmp_path / "items.jsonl"
    items.write_text(json.dumps({"question": "q", "expected_keys": ["A"]}) + "\n")
    session_cls.return_value.run.return_value = AgentAnswer(
        text="x [doc 1]", citations=[Citation(1, "A", None, ["p"])], tool_calls=1, context_tokens=5,
        context_keys=["A"])
    monkeypatch.setattr(cli, "judge_grounding", lambda client, model, text, passages: (1, 1))
    rc = cli.main(["evaluate", "--entity", "tests.test_session:PolicyDoc", "--items", str(items)])
    report = json.loads(capsys.readouterr().out)
    assert rc == 0
    assert (report["items"], report["citation_recall"], report["grounding"], report["tool_calls"]) == (1, 1.0, 1.0, [1])


def test_missing_credentials_fail_before_any_connection(monkeypatch):
    client_cls, _ = _env(monkeypatch)
    monkeypatch.delenv("IVERSON_CLIENT_SECRET")
    with pytest.raises(KeyError, match="IVERSON_CLIENT_SECRET"):
        cli.main(["ask", "--entity", "tests.test_session:PolicyDoc", "--question", "q"])
    client_cls.assert_not_called()

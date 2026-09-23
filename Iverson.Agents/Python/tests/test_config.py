import dataclasses
from datetime import timedelta

import pytest

from iverson_agent.config import AgentConfig


def test_defaults_match_spec_section_5():
    cfg = AgentConfig()
    assert (cfg.model, cfg.k, cfg.fanout, cfg.m, cfg.max_tool_calls, cfg.context_tokens) == (
        "claude-opus-5", 5, 4, 3, 3, 24_000)
    assert cfg.schema_ttl == timedelta(minutes=10)


def test_config_is_frozen():
    with pytest.raises(dataclasses.FrozenInstanceError):
        AgentConfig().k = 9

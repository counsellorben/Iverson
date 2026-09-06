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

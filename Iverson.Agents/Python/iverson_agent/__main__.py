"""CLI: `python -m iverson_agent ask --entity pkg.models:PolicyDoc --question "..."`
       `python -m iverson_agent evaluate --entity ... --items labelled.jsonl`"""
from __future__ import annotations

import argparse
import importlib
import json
import os
import sys
from pathlib import Path
from urllib.parse import urlsplit

import anthropic

from iverson_client import IversonClient, IversonClientCredentials
from iverson_agent.config import AgentConfig
from iverson_agent.evaluate import evaluate, judge_grounding, load_items
from iverson_agent.session import AgentSession


def _credentials() -> IversonClientCredentials:
    return IversonClientCredentials(
        client_id=os.environ["IVERSON_CLIENT_ID"], client_secret=os.environ["IVERSON_CLIENT_SECRET"],
        token_endpoint=os.environ["IVERSON_TOKEN_ENDPOINT"], scope=os.environ.get("IVERSON_CLIENT_SCOPE"))


def _endpoint() -> tuple[str, int, bool]:
    """(host, port, use_tls) from IVERSON_GRPC_URL.

    The scheme decides TLS: `https://` connects with TLS (default port 443); `http://` — the
    default, `http://localhost:8080`, matching the compose stack's plaintext h2c listener —
    connects without it. Anything other than localhost should be `https://`."""
    url = urlsplit(os.environ.get("IVERSON_GRPC_URL", "http://localhost:8080"))
    if url.scheme not in ("http", "https"):
        raise ValueError(f"IVERSON_GRPC_URL must start with http:// or https://, got {url.geturl()!r}")
    return url.hostname or "localhost", url.port or (443 if url.scheme == "https" else 8080), url.scheme == "https"


def _entity(spec: str) -> type:
    """Resolve `module:Class` to the @iverson_entity class, failing with the spec in the message."""
    module, sep, name = spec.partition(":")
    if not sep or not module or not name:
        raise ValueError(f"--entity must be module:Class, got {spec!r}")
    try:
        cls = getattr(importlib.import_module(module), name)
    except (ImportError, AttributeError) as err:
        raise ValueError(f"--entity {spec!r} could not be resolved: {err}") from err
    if not hasattr(cls, "_iverson_meta"):
        raise ValueError(f"--entity {spec!r} is not an @iverson_entity class")
    return cls


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="iverson_agent")
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("ask", "evaluate"):
        p = sub.add_parser(name)
        p.add_argument("--entity", required=True, help="module:Class of the @iverson_entity to retrieve over")
        p.add_argument("--title-field", default="title")
        if name == "ask":
            p.add_argument("--question", required=True)
            p.add_argument("--trace-id", default="")
        else:
            p.add_argument("--items", required=True, type=Path)
    args = ap.parse_args(argv)

    host, port, tls = _endpoint()
    creds = _credentials()
    token = os.environ["IVERSON_ACTING_USER_TOKEN"]
    cfg = AgentConfig()
    client = anthropic.Anthropic()
    with IversonClient(host, port, use_tls=tls, credentials=creds) as iverson:
        session = AgentSession(
            client, iverson, _entity(args.entity), cfg,
            schema_client_factory=lambda t: IversonClient(host, port, use_tls=tls, credentials=creds,
                                                          acting_user_token=t),
            title_field=args.title_field)
        if args.cmd == "ask":
            answer = session.run(args.question, token, trace_id=args.trace_id)
            print(answer.text)
            for c in answer.citations:
                print(f"  [doc {c.doc_number}] {c.key} {c.title or ''}")
            for f in answer.flags:
                print(f"  ! {f}", file=sys.stderr)
            return 0
        report = evaluate(session, load_items(args.items), token,
                          lambda text, passages: judge_grounding(client, cfg.model, text, passages))
        print(json.dumps(report, indent=2))
        return 0


if __name__ == "__main__":
    sys.exit(main())

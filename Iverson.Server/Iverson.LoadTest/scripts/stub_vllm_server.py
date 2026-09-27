#!/usr/bin/env python3
"""Stub vLLM `/v1/chat/completions` server for the teacher-ceiling dry run (spec
docs/specs/2026-09-20-teacher-ceiling-design.md §11 step 2). Stands in for a rented GPU instance so
`teacher_rerank.py` can be exercised end to end -- prompt construction, permutation validation, the
run writer, and the scorer -- for zero cost, before Task 3 spends real money.

It answers every query with that query's doc ids **in input-run order** by default (the A0' run
file's own row order), NOT the shuffled order teacher_rerank.py presented in the prompt: it
ignores the shuffle and reconstructs fusion order. That makes the dry run's output order-identical
to the A0' run it started from, which is exactly what report.py's plain `--run`/`--qrels` scoring
(not `--pair`) must reproduce to the two exact numbers the gate checks (spec §11 step 2).

`--order reversed` answers instead with each query's ids in REVERSE input-run order. This is the
other half of the gate (fix round 1, controller ruling): a pipeline bug that discards the model's
reply and silently falls back to writing fusion order -- exactly the shape of the bug the reviewer
found, where `score_query` returned the input pool unchanged and the dry run byte-matched the A0'
baseline under `--order input-run` too -- would emit fusion order in BOTH modes and so score the
identity-order numbers again under `--order reversed`, instead of the reversed-order numbers. A
single-mode gate cannot tell "the teacher reordered the pool" apart from "the teacher's reply was
thrown away and fusion order leaked through"; two modes with two different, exactly-pinned
acceptance numbers can.

Query identification: the request body carries only the rendered prompt, not a query id, so this
server recovers the query id by extracting the query text embedded between teacher_rerank.py's
fixed "Query: " and "\\n\\nDocuments (doc id, title, abstract):\\n" markers (its
PROMPT_INSTRUCTIONS template, duplicated here rather than imported -- this directory's convention,
per teacher_rerank.py's own module docstring, is that per-script parsing helpers are local copies,
not shared imports) and looking that text up against queries.jsonl's 300 distinct query texts
(task brief's resolved ambiguity).

Stdlib only.

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/stub_vllm_server.py \\
        --run scifact-run-2026-08-26/runs/rerank-a0prime.chunks.trec \\
        --queries scifact-run-2026-08-26/beir/queries.jsonl \\
        --order input-run

Prints the bound port to stdout, then serves until killed.
"""
import argparse
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# Must match teacher_rerank.py's PROMPT_INSTRUCTIONS exactly -- these are the literal markers this
# server splits the rendered prompt on to recover the query text (see module docstring).
QUERY_PREFIX = "Query: "
DOCUMENTS_MARKER = "\n\nDocuments (doc id, title, abstract):\n"


# ── load_run -- own copy, this directory's convention (teacher_rerank.py does not import
#    popularity_rerank.py's either; see that module's docstring) ────────────────────────────────

def load_run(path, order):
    """Parses a 6-column TREC run: `queryId Q0 docId rank score tag`, whitespace-separated.
    Returns {queryId: [docId, ...]} in file (rank) order -- this IS "input-run order" (spec §11
    step 2): the A0' run file's rows are already fusion-ranked, so preserving file order here is
    exactly reconstructing fusion order, with no separate sort needed.

    `order` selects the mode this stub answers in (fix round 1, controller ruling): `"input-run"`
    keeps each query's ids in that file order; `"reversed"` reverses each query's list once here,
    at load time, so every subsequent lookup just serves the already-reversed list."""
    pool = {}
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            if not line.strip():
                continue
            fields = line.split()
            if len(fields) != 6:
                sys.exit(f"{path}:{lineno}: expected 6 whitespace-separated fields, got {len(fields)}: {line!r}")
            query_id, _iter, doc_id, _rank, _score, _tag = fields
            rows = pool.setdefault(query_id, [])
            if doc_id in rows:
                sys.exit(f"{path}:{lineno}: document {doc_id!r} appears twice in query {query_id!r}'s pool")
            rows.append(doc_id)
    if not pool:
        sys.exit(f"{path}: no run rows")
    if order == "reversed":
        pool = {query_id: list(reversed(ids)) for query_id, ids in pool.items()}
    return pool


def load_queries_by_text(path):
    """Returns {queryText: queryId} from queries.jsonl (keys: _id, text) -- the reverse of
    teacher_rerank.py's load_queries, since this server looks up by text (extracted from the
    prompt) to recover the id. Exits loud if two queries share text: an ambiguous reverse mapping
    would silently answer some query with another query's ranking, exactly the kind of quiet
    corruption this gate exists to catch."""
    by_text = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            text = record["text"]
            query_id = str(record["_id"])
            if text in by_text:
                sys.exit(
                    f"{path}: query text {text!r} is shared by {by_text[text]!r} and {query_id!r} -- "
                    "the reverse lookup this stub depends on requires distinct query texts"
                )
            by_text[text] = query_id
    if not by_text:
        sys.exit(f"{path}: no queries")
    return by_text


def extract_query_text(prompt):
    """Recovers the query text from a rendered prompt by slicing between the two fixed markers
    teacher_rerank.py's PROMPT_INSTRUCTIONS always emits around it. Raises ValueError (caught by
    the request handler, turned into a loud 500) if either marker is absent -- this must never
    silently fall back to guessing a query."""
    start = prompt.index(QUERY_PREFIX) + len(QUERY_PREFIX)
    end = prompt.index(DOCUMENTS_MARKER, start)
    return prompt[start:end]


def make_handler(pool, queries_by_text):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):
            pass  # keep the dry run's console quiet; failures still surface as HTTP 500s below

        def do_POST(self):
            if self.path != "/v1/chat/completions":
                self.send_error(404, f"unhandled path {self.path!r}")
                return
            try:
                length = int(self.headers["Content-Length"])
                body = json.loads(self.rfile.read(length))
                prompt = body["messages"][0]["content"]
                query_text = extract_query_text(prompt)
                query_id = queries_by_text[query_text]
                order = pool[query_id]
            except Exception as e:  # fail loud: a 500 surfaces as an HTTPError in teacher_rerank.py's
                # urllib caller, which is an uncaught exception that stops the run -- never a
                # degraded, silently-wrong response.
                self.send_error(500, f"{type(e).__name__}: {e}")
                return

            response = {
                "choices": [
                    {"message": {"content": json.dumps(order)}, "finish_reason": "stop"},
                ],
            }
            payload = json.dumps(response).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

    return Handler


def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run", required=True, help="the A0' TREC run whose input order this stub reconstructs")
    ap.add_argument("--queries", required=True, help="BEIR queries.jsonl (keys: _id, text)")
    ap.add_argument("--port", type=int, default=0, help="port to bind on 127.0.0.1; 0 (default) picks an ephemeral port")
    ap.add_argument(
        "--order", choices=["input-run", "reversed"], default="input-run",
        help="reply order per query: 'input-run' (default, fusion order) or 'reversed' (fix round 1 gate)",
    )
    return ap


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    pool = load_run(args.run, args.order)
    queries_by_text = load_queries_by_text(args.queries)

    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(pool, queries_by_text))
    print(server.server_address[1], flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()

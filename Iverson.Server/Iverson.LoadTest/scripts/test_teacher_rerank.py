"""pytest suite for teacher_rerank.py (spec docs/specs/2026-09-20-teacher-ceiling-design.md,
"Stage 1 of a three-stage line of work on per-tenant distilled rerankers"). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q

Everything except the model call is covered here, on the dev box, before any GPU is rented:
permutation validation (missing/duplicated/invented/wrong-length rejection, exact-permutation
acceptance, and P22's unquoted-JSON-number acceptance), JSON parsing, the refusal-to-write rule,
seeded-shuffle reproducibility (against a literal expected order, per P7), run-file formatting, the
resume rule (accepted skipped, all-rejected re-issued with a fresh budget, accepted wins when both
exist), and --subsample's reproducible selection (against a literal expected selection, per P21).
`build_prompt`/`call_teacher`/`write_sidecar` are the model-call path and the sidecar writer; the
brief's Step 1 does not enumerate them, so they are exercised only indirectly here (call_teacher is
monkeypatched out wherever score_query/main are driven).

No non-stdlib imports beyond pytest -- nothing needs PYTHONPATH."""
import http.server
import json
import os
import re
import sys
import threading
import urllib.request
from argparse import Namespace

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import teacher_rerank as tr  # noqa: E402


def write(path, text):
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    return str(path)


# --------------------------------------------------------------------------------------------
# validate_permutation -- missing / duplicated / invented / wrong-length rejection,
# exact-permutation acceptance, and unquoted-JSON-number acceptance (P22)
# --------------------------------------------------------------------------------------------

EXPECTED = ["10", "20", "30", "40", "50"]


def test_validate_permutation_accepts_an_exact_permutation():
    ok, result = tr.validate_permutation(["30", "10", "50", "20", "40"], EXPECTED)
    assert ok is True
    assert result == ["30", "10", "50", "20", "40"]


def test_validate_permutation_rejects_the_wrong_length():
    ok, reason = tr.validate_permutation(["10", "20", "30"], EXPECTED)
    assert ok is False
    assert "length" in reason


def test_validate_permutation_rejects_a_duplicated_id():
    """Five slots, but '10' appears twice and '50' is dropped -- same length as EXPECTED."""
    ok, reason = tr.validate_permutation(["10", "10", "20", "30", "40"], EXPECTED)
    assert ok is False
    assert "duplicat" in reason


def test_validate_permutation_rejects_a_response_missing_an_expected_id():
    """Same length, no duplicates, but '50' never appears and '99' (not in the pool) fills its
    slot instead -- from the pool's perspective, '50' is MISSING."""
    ok, reason = tr.validate_permutation(["10", "20", "30", "40", "99"], EXPECTED)
    assert ok is False
    assert "missing" in reason
    assert "50" in reason


def test_validate_permutation_rejects_a_response_with_an_invented_id():
    """Same scenario read from the candidate's side: '99' is an id the pool never offered."""
    ok, reason = tr.validate_permutation(["10", "20", "30", "40", "99"], EXPECTED)
    assert ok is False
    assert "invented" in reason
    assert "99" in reason


def test_validate_permutation_accepts_unquoted_json_numbers():
    """P22: a reply whose ids are unquoted JSON numbers (decoded as Python ints, not strings) must
    be ACCEPTED once normalised with str() -- this is the branch Task 2's stub cannot reach, since
    it always emits ids as strings. Without per-element str() normalisation, set(int) == set(str)
    is False for every query, which would reject every reply and produce no run file."""
    candidate_ids = [30, 10, 50, 20, 40]  # json.loads(...) of "[30, 10, 50, 20, 40]" -> Python ints
    ok, result = tr.validate_permutation(candidate_ids, EXPECTED)
    assert ok is True
    assert result == ["30", "10", "50", "20", "40"]
    assert all(isinstance(doc_id, str) for doc_id in result)


# --------------------------------------------------------------------------------------------
# parse_json_array -- rejects a non-array and a non-JSON body
# --------------------------------------------------------------------------------------------

def test_parse_json_array_rejects_non_json_text():
    ids, reason = tr.parse_json_array("this is not json at all")
    assert ids is None
    assert "JSON" in reason


def test_parse_json_array_rejects_a_json_object_that_is_not_an_array():
    ids, reason = tr.parse_json_array('{"ranking": ["10", "20"]}')
    assert ids is None
    assert "array" in reason


def test_parse_json_array_accepts_a_json_array():
    ids, reason = tr.parse_json_array('["10", "20", "30"]')
    assert reason is None
    assert ids == ["10", "20", "30"]


def test_parse_json_array_rejects_none_instead_of_raising_typeerror():
    """json.loads(None) raises TypeError, not JSONDecodeError. A reasoning model can legitimately
    return `content: null` on vLLM's OpenAI route (the answer landed in `reasoning_content`
    instead) -- this must come back as an ordinary (None, reason) rejection, matching the
    docstring's promise that this function "never raises", not propagate an uncaught traceback
    that would abort the whole run without ever recording the offending response."""
    ids, reason = tr.parse_json_array(None)
    assert ids is None
    assert "JSON" in reason


# --------------------------------------------------------------------------------------------
# shuffled_doc_order -- seeded-shuffle reproducibility (P7), against a LITERAL expected order
# --------------------------------------------------------------------------------------------

def test_shuffled_doc_order_matches_a_literal_expected_order():
    """The literal below was computed once via:
        random.Random(f"7:q1").shuffle(["101","102","103","104","105"])
    and independently reproduced across separate process invocations, including under
    PYTHONHASHSEED=random, proving random.Random's string seeding is NOT hash()-based (hash() of a
    str is randomized per process unless PYTHONHASHSEED is fixed). Asserting against this pasted
    literal -- rather than calling shuffled_doc_order a second time in this same process and
    comparing the two results -- is what makes this test able to catch an implementation that used
    `random.Random(hash(query_id))` instead: within one process hash() is stable, so a
    call-vs-call comparison would pass even on a `hash()`-based, cross-process-unreproducible
    implementation. Only a pre-computed literal exposes that bug."""
    doc_ids = ["101", "102", "103", "104", "105"]
    order = tr.shuffled_doc_order(doc_ids, shuffle_seed=7, query_id="q1")
    assert order == ["105", "103", "101", "104", "102"]


def test_shuffled_doc_order_differs_by_query_id_under_the_same_shuffle_seed():
    """Confirms the seed is genuinely per-query (f"{shuffle_seed}:{query_id}"), not global -- two
    different query ids under the same shuffle_seed must not collapse to the same order."""
    doc_ids = ["101", "102", "103", "104", "105"]
    order_q1 = tr.shuffled_doc_order(doc_ids, shuffle_seed=7, query_id="q1")
    order_q2 = tr.shuffled_doc_order(doc_ids, shuffle_seed=7, query_id="q2")
    assert order_q1 != order_q2


def test_shuffled_doc_order_leaves_the_input_list_untouched():
    doc_ids = ["101", "102", "103"]
    tr.shuffled_doc_order(doc_ids, shuffle_seed=1, query_id="q1")
    assert doc_ids == ["101", "102", "103"]


# --------------------------------------------------------------------------------------------
# select_subsample -- reproducible selection (P21), against a LITERAL expected selection
# --------------------------------------------------------------------------------------------

def test_select_subsample_matches_a_literal_expected_selection():
    """The literal below was computed once via:
        random.Random(f"99").sample(sorted(["5","3","1","4","2","9","7","6","8"]), 4)
    Sorting before sampling is load-bearing (P21): if the ids were collected into a `set` instead
    (unordered), the sample would differ across processes/Python versions even with the same seed,
    because random.sample's result depends on input order as well as the seed. A literal, not a
    second in-process call, is what would catch a regression to an unsorted collection."""
    run_query_ids = ["5", "3", "1", "4", "2", "9", "7", "6", "8"]
    selection = tr.select_subsample(run_query_ids, n=4, subsample_seed=99)
    assert selection == ["2", "9", "1", "5"]


def test_select_subsample_is_the_same_across_repeated_calls():
    run_query_ids = ["5", "3", "1", "4", "2", "9", "7", "6", "8"]
    first = tr.select_subsample(run_query_ids, n=4, subsample_seed=99)
    second = tr.select_subsample(run_query_ids, n=4, subsample_seed=99)
    assert first == second


def test_select_subsample_is_insensitive_to_input_order():
    """Sorting before sampling means the selection must not depend on the order run_query_ids
    arrived in -- a shuffled input must select the same ids."""
    sorted_ids = ["1", "2", "3", "4", "5", "6", "7", "8", "9"]
    shuffled_ids = ["8", "1", "9", "3", "5", "2", "7", "4", "6"]
    assert tr.select_subsample(sorted_ids, 4, 99) == tr.select_subsample(shuffled_ids, 4, 99)


# --------------------------------------------------------------------------------------------
# write_run -- 6 columns, score = 51 - position, ranks 1..50 map to scores 50..1
# --------------------------------------------------------------------------------------------

def test_write_run_produces_six_whitespace_columns_score_51_minus_position(tmp_path):
    doc_ids = [f"doc{i}" for i in range(50)]  # 50 docs -> ranks 1..50
    scored_pool = {"q1": doc_ids}
    out_path = str(tmp_path / "out.chunks.trec")
    tr.write_run(out_path, scored_pool, pool_query_order=["q1"])

    with open(out_path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    assert len(lines) == 50

    for position, line in enumerate(lines, start=1):
        fields = line.split()
        assert len(fields) == 6, f"expected 6 columns, got {len(fields)}: {line!r}"
        query_id, iteration, doc_id, rank, score, tag = fields
        assert query_id == "q1"
        assert iteration == "Q0"
        assert doc_id == f"doc{position - 1}"
        assert int(rank) == position
        assert score == f"{51 - position:.6f}"

    # rank 1 -> score 50.000000; rank 50 -> score 1.000000, never 0.000000
    assert lines[0].split()[4] == "50.000000"
    assert lines[-1].split()[4] == "1.000000"
    assert all(line.split()[4] != "0.000000" for line in lines)


def test_write_run_preserves_pool_query_order_not_dict_order(tmp_path):
    scored_pool = {"2": ["y"], "1": ["b", "a"]}
    out_path = str(tmp_path / "out.chunks.trec")
    tr.write_run(out_path, scored_pool, pool_query_order=["1", "2"])
    with open(out_path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    assert lines == [
        "1 Q0 b 1 50.000000 teacher-ceiling",
        "1 Q0 a 2 49.000000 teacher-ceiling",
        "2 Q0 y 1 50.000000 teacher-ceiling",
    ]


# --------------------------------------------------------------------------------------------
# write_sidecar -- spec gap 1 (subsample/subsampleSeed recorded) and spec gap 2 (serving-identity
# flags recorded, null when omitted). Not among the brief's 8 enumerated cases, but the
# controller's Task 1 fix round 1 named both as spec gaps to close, so covered here directly.
# --------------------------------------------------------------------------------------------

def sidecar_args(**overrides):
    defaults = dict(
        base_url="http://vllm.invalid", model="gpt-oss-120b",
        seed=7, shuffle_seed=11,
        subsample=None, subsample_seed=None,
        vllm_version=None, quantisation=None, instance_type=None,
    )
    defaults.update(overrides)
    return Namespace(**defaults)


def test_write_sidecar_records_subsample_and_subsample_seed_when_given(tmp_path):
    out_path = str(tmp_path / "teacher.chunks.trec")
    tr.write_sidecar(out_path, sidecar_args(subsample=50, subsample_seed=123))
    with open(tmp_path / "teacher.meta.json", encoding="utf-8") as f:
        sidecar = json.load(f)
    assert sidecar["reranker"]["subsample"] == 50
    assert sidecar["reranker"]["subsampleSeed"] == 123


def test_write_sidecar_records_null_subsample_when_not_given(tmp_path):
    out_path = str(tmp_path / "teacher.chunks.trec")
    tr.write_sidecar(out_path, sidecar_args())  # subsample=None, subsample_seed=None
    with open(tmp_path / "teacher.meta.json", encoding="utf-8") as f:
        sidecar = json.load(f)
    assert sidecar["reranker"]["subsample"] is None
    assert sidecar["reranker"]["subsampleSeed"] is None


def test_write_sidecar_records_serving_identity_flags_when_given(tmp_path):
    out_path = str(tmp_path / "teacher.chunks.trec")
    tr.write_sidecar(out_path, sidecar_args(
        vllm_version="0.9.1", quantisation="MXFP4", instance_type="A100-80GB",
    ))
    with open(tmp_path / "teacher.meta.json", encoding="utf-8") as f:
        sidecar = json.load(f)
    assert sidecar["reranker"]["vllmVersion"] == "0.9.1"
    assert sidecar["reranker"]["quantisation"] == "MXFP4"
    assert sidecar["reranker"]["instanceType"] == "A100-80GB"


def test_write_sidecar_records_null_serving_identity_when_omitted(tmp_path):
    """Controller ruling: 'Do not guess values; absent means null.'"""
    out_path = str(tmp_path / "teacher.chunks.trec")
    tr.write_sidecar(out_path, sidecar_args())
    with open(tmp_path / "teacher.meta.json", encoding="utf-8") as f:
        sidecar = json.load(f)
    assert sidecar["reranker"]["vllmVersion"] is None
    assert sidecar["reranker"]["quantisation"] is None
    assert sidecar["reranker"]["instanceType"] is None


# --------------------------------------------------------------------------------------------
# Fixtures for the main()-level tests: refusal-to-write and resume
# --------------------------------------------------------------------------------------------

class ScriptedTeacher:
    """Monkeypatches tr.call_teacher with a fixed queue of (content, finish_reason) replies,
    returned in call order. Records how many times it was invoked so a test can assert exactly
    which queries actually triggered a model call (e.g. that a resumed, already-accepted query
    triggers none), and captures every prompt it was called with so a test can inspect what the
    model actually saw (e.g. that documents arrived in the seeded shuffle order, not fusion
    order)."""

    def __init__(self, replies):
        self.replies = list(replies)
        self.calls = 0
        self.prompts = []

    def __call__(self, base_url, model, prompt, seed, api_key=None):
        self.calls += 1
        self.prompts.append(prompt)
        self.api_key = api_key
        return self.replies.pop(0)


def make_fixture_files(tmp_path):
    """Two queries, three docs each -- hand-computable. q1: d1/d2/d3, q2: e1/e2/e3."""
    run_path = write(
        tmp_path / "a0prime.chunks.trec",
        "q1 Q0 d1 1 0.9 rerank-a0prime\n"
        "q1 Q0 d2 2 0.8 rerank-a0prime\n"
        "q1 Q0 d3 3 0.7 rerank-a0prime\n"
        "q2 Q0 e1 1 0.9 rerank-a0prime\n"
        "q2 Q0 e2 2 0.8 rerank-a0prime\n"
        "q2 Q0 e3 3 0.7 rerank-a0prime\n",
    )
    corpus_lines = "\n".join(
        json.dumps({"_id": doc_id, "title": f"title {doc_id}", "text": f"text {doc_id}"})
        for doc_id in ["d1", "d2", "d3", "e1", "e2", "e3"]
    )
    corpus_path = write(tmp_path / "corpus.jsonl", corpus_lines + "\n")
    queries_path = write(
        tmp_path / "queries.jsonl",
        json.dumps({"_id": "q1", "text": "query one"}) + "\n"
        + json.dumps({"_id": "q2", "text": "query two"}) + "\n",
    )
    return run_path, corpus_path, queries_path


def ledger_pass(run_path, shuffle_seed=0, subsample=None, subsample_seed=None):
    """The stamp tr.pass_identity writes into every ledger record. Ledger fixtures below build
    their records through this so a record written by THIS pass is distinguishable, in the
    fixture itself, from one written by another (a different --run, or a different
    --shuffle-seed / --subsample / --subsample-seed over the same pool)."""
    return {
        "run": os.path.abspath(run_path),
        "shuffleSeed": shuffle_seed,
        "subsample": subsample,
        "subsampleSeed": subsample_seed,
    }


def base_argv(run_path, corpus_path, queries_path, responses_path, out_path):
    return [
        "--run", run_path,
        "--corpus", corpus_path,
        "--queries", queries_path,
        "--base-url", "http://unused.invalid",
        "--model", "unused-model",
        "--seed", "0",
        "--shuffle-seed", "0",
        "--responses", responses_path,
        "--out", out_path,
    ]


# --------------------------------------------------------------------------------------------
# The shuffled order must actually reach the prompt -- spec §3: "Presenting them in A0' order
# anchors the teacher to the ranking under test; an anchored teacher measures the old ranking as
# much as itself." Task 1 fix round 1, CRITICAL: this was the one silent failure mode in the
# script -- build_prompt could be called with fusion order instead of shuffled order and every
# other test would still pass, since none of them inspected what the model was actually shown.
# --------------------------------------------------------------------------------------------

DOC_ID_IN_PROMPT = re.compile(r"^\[(\S+)\]", re.M)


# --------------------------------------------------------------------------------------------
# --api-key: some vLLM deployments require Authorization: Bearer <key> (e.g. a RunPod template
# that launches `vllm serve --api-key` from VLLM_API_KEY before the operator runs this script).
# The plumbing test proves args.api_key reaches call_teacher through main(), via ScriptedTeacher
# recording what it received. The transport test proves call_teacher itself sends the header when
# given a key and omits it when not, against a real HTTP server on loopback -- not a mock of
# urllib, since a mock could assert the header was passed to the wrong urllib call.
# --------------------------------------------------------------------------------------------

def test_main_passes_api_key_through_to_call_teacher(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    scripted = ScriptedTeacher([
        ('["d1", "d2", "d3"]', "stop", None, None, None),
        ('["e1", "e2", "e3"]', "stop", None, None, None),
    ])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    argv += ["--api-key", "sk-test-through-main"]
    monkeypatch.setattr(sys, "argv", ["teacher_rerank.py"] + argv)
    tr.main()

    assert scripted.api_key == "sk-test-through-main"


def test_main_omits_api_key_when_not_given(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    scripted = ScriptedTeacher([
        ('["d1", "d2", "d3"]', "stop", None, None, None),
        ('["e1", "e2", "e3"]', "stop", None, None, None),
    ])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    monkeypatch.setattr(sys, "argv", ["teacher_rerank.py"] + argv)
    tr.main()

    assert scripted.api_key is None


class _RecordingAuthHandler(http.server.BaseHTTPRequestHandler):
    """Echoes back a valid chat-completion reply and records the Authorization header it saw."""

    seen_auth = None

    def do_POST(self):
        _RecordingAuthHandler.seen_auth = self.headers.get("Authorization")
        length = int(self.headers["Content-Length"])
        self.rfile.read(length)  # drain the request body
        reply = json.dumps(
            {"choices": [{"message": {"content": "[]"}, "finish_reason": "stop"}]}
        ).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(reply)))
        self.end_headers()
        self.wfile.write(reply)

    def log_message(self, *args):  # silence stdout during the test run
        pass


def test_call_teacher_sends_bearer_header_when_given_an_api_key():
    _RecordingAuthHandler.seen_auth = None
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _RecordingAuthHandler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        tr.call_teacher(f"http://127.0.0.1:{port}", "m", "p", 0, api_key="sk-test-header")
        assert _RecordingAuthHandler.seen_auth == "Bearer sk-test-header"
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_call_teacher_omits_authorization_header_when_api_key_is_none():
    _RecordingAuthHandler.seen_auth = "unset"  # sentinel distinct from None
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _RecordingAuthHandler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        tr.call_teacher(f"http://127.0.0.1:{port}", "m", "p", 0, api_key=None)
        assert _RecordingAuthHandler.seen_auth is None
    finally:
        server.shutdown()
        thread.join(timeout=5)


class _HTTPErrorResponseHandler(http.server.BaseHTTPRequestHandler):
    """Responds with a non-2xx status to test the HTTPError exception handler."""

    def do_POST(self):
        length = int(self.headers["Content-Length"])
        self.rfile.read(length)  # drain the request body
        error_body = "Server internal error: FSM crash"
        self.send_response(500)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(error_body)))
        self.end_headers()
        self.wfile.write(error_body.encode("utf-8"))

    def log_message(self, *args):  # silence stdout during the test run
        pass


def test_call_teacher_catches_http_error_and_returns_http_error_finish_reason():
    """A real HTTP server responding with 5xx status triggers urllib.error.HTTPError.
    The exception must be caught and returned as a 5-tuple with finish_reason='http_error'
    and content containing the status code and reason."""
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _HTTPErrorResponseHandler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        content, finish_reason, reasoning, completion_tokens, prompt_tokens = tr.call_teacher(
            f"http://127.0.0.1:{port}", "m", "p", 0
        )
        assert finish_reason == "http_error"
        assert reasoning is None
        assert completion_tokens is None
        assert prompt_tokens is None
        assert "500" in content, f"HTTP status code 500 must be in error content: {content}"
        assert "Server internal error" in content, f"Error body must be in content: {content}"
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_call_teacher_catches_oserror_and_returns_http_error_finish_reason(monkeypatch):
    """OSError (including socket.timeout, TimeoutError, and connection failures) must be
    caught and returned as a 5-tuple with finish_reason='http_error' and content as str(e)."""
    def mock_urlopen(req, timeout=None):
        raise OSError("simulated connection failure")

    monkeypatch.setattr(urllib.request, "urlopen", mock_urlopen)

    content, finish_reason, reasoning, completion_tokens, prompt_tokens = tr.call_teacher(
        "http://unused.invalid", "m", "p", 0
    )
    assert finish_reason == "http_error"
    assert reasoning is None
    assert completion_tokens is None
    assert prompt_tokens is None
    assert content == "simulated connection failure"


class _UsageReasoningHandler(http.server.BaseHTTPRequestHandler):
    """Echoes back a valid chat-completion reply carrying DISTINCT `usage.completion_tokens` /
    `usage.prompt_tokens` values and a distinct `message.reasoning` string -- final whole-branch
    review, Finding 2: a swap between the two numeric fields, or reading `reasoning_content`
    instead of `reasoning`, left every existing test green, since none of them inspected these
    three fields at all."""

    def do_POST(self):
        length = int(self.headers["Content-Length"])
        self.rfile.read(length)  # drain the request body
        reply = json.dumps(
            {
                "choices": [
                    {
                        "message": {"content": "[]", "reasoning": "distinct reasoning text"},
                        "finish_reason": "stop",
                    }
                ],
                "usage": {"completion_tokens": 111, "prompt_tokens": 222},
            }
        ).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(reply)))
        self.end_headers()
        self.wfile.write(reply)

    def log_message(self, *args):  # silence stdout during the test run
        pass


def test_call_teacher_returns_reasoning_and_token_counts_in_the_right_position():
    """Against a real HTTP server (not a mock of urllib), asserts completion_tokens, prompt_tokens
    and reasoning each land in their own position of the 5-tuple, with distinct values so a swap
    between completion_tokens/prompt_tokens -- or reading `reasoning_content` instead of
    `reasoning` -- would fail this assertion."""
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _UsageReasoningHandler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        content, finish_reason, reasoning, completion_tokens, prompt_tokens = tr.call_teacher(
            f"http://127.0.0.1:{port}", "m", "p", 0
        )
        assert finish_reason == "stop"
        assert reasoning == "distinct reasoning text"
        assert completion_tokens == 111
        assert prompt_tokens == 222
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_main_records_http_error_to_responses_and_continues_retry_loop(tmp_path, monkeypatch):
    """The http_error finish_reason from call_teacher must be handled as a retriable failure
    (like finish_reason='length'), recording a rejected entry and continuing the retry loop.
    A query with http_error on both attempts must be left unscored (refusal-to-write rule)."""
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    # q1: both attempts return http_error; q2: succeeds on first attempt
    scripted = ScriptedTeacher([
        ("HTTP 500: Internal Server Error -- FSM crash", "http_error", None, None, None),
        ("HTTP 502: Bad Gateway", "http_error", None, None, None),
        ('["e2", "e3", "e1"]', "stop", None, None, None),
    ])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)  # refused -- q1 unscored due to http_error on both attempts

    assert exc_info.value.code != 0
    message = str(exc_info.value)
    assert "q1" in message
    assert "1 / 2" in message  # "1 / 2 queries unscored"
    assert not os.path.exists(out_path), "no run file may be written when any query is unscored"
    assert scripted.calls == 3

    # Verify q1's two http_error attempts were durably logged
    with open(responses_path, encoding="utf-8") as f:
        records = [json.loads(line) for line in f if line.strip()]
    q1_records = [r for r in records if r["query_id"] == "q1"]
    assert len(q1_records) == 2
    assert all(r["status"] == "rejected" for r in q1_records)
    assert all("http_error:" in r["reason"] for r in q1_records), (
        "reason must start with 'http_error:' followed by the error details"
    )


def test_main_presents_documents_to_the_model_in_shuffled_not_fusion_order(tmp_path, monkeypatch):
    """q1's fusion-order pool is d1, d2, d3 (spec/brief §5's --run column order). Under
    shuffle_seed=0, tr.shuffled_doc_order(["d1","d2","d3"], 0, "q1") == ["d2","d3","d1"] -- a
    different order, so this assertion is non-vacuous. The prompt's `[docId]` labels (in the
    order build_prompt emits them, DOCUMENT_TEMPLATE = "[{doc_id}] ...") must appear in that
    shuffled order, not fusion order.

    Verified to catch the exact mutation the reviewer found: replacing build_prompt's `shuffled`
    argument with `expected_ids` (fusion order) makes this assertion fail -- confirmed by hand
    before this test was accepted (see the fix-round report)."""
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    scripted = ScriptedTeacher([
        ('["d2", "d3", "d1"]', "stop", None, None, None),
        ('["e2", "e3", "e1"]', "stop", None, None, None),
    ])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    tr.main(argv)

    assert scripted.calls == 2
    q1_prompt, q2_prompt = scripted.prompts
    assert DOC_ID_IN_PROMPT.findall(q1_prompt) == ["d2", "d3", "d1"]
    assert DOC_ID_IN_PROMPT.findall(q2_prompt) == ["e2", "e3", "e1"]
    # And explicitly NOT fusion order (d1, d2, d3 / e1, e2, e3), which is what a
    # build_prompt(query_text, expected_ids, corpus) regression would produce instead.
    assert DOC_ID_IN_PROMPT.findall(q1_prompt) != ["d1", "d2", "d3"]
    assert DOC_ID_IN_PROMPT.findall(q2_prompt) != ["e1", "e2", "e3"]

    # ...and the document BODIES must reach the model, not just the id labels. Final whole-branch
    # review, IMPORTANT: mutating build_prompt's DOCUMENT_TEMPLATE.format(...) to title="",
    # text="" -- every document rendering as a bare `[docid]` with no title and no abstract --
    # left the whole suite green AND the two-sided stub gate byte-identical (0.6980 / 0.9193),
    # because the stub reads only the query text out of the prompt and the assertions above match
    # only the `^\[(\S+)\]` label. The title + abstract IS the measurement's entire input: on the
    # rented GPU the teacher would rank bare ids, score badly, and the verdict doc would record a
    # FAIL that "closes the per-tenant reranking line" off a run in which the model never saw a
    # single document. Asserting the id, title and text as ONE contiguous block also pins that
    # each body is attached to its own id, not merely present somewhere in the prompt.
    for prompt, doc_ids in ((q1_prompt, ["d2", "d3", "d1"]), (q2_prompt, ["e2", "e3", "e1"])):
        for doc_id in doc_ids:
            assert f"[{doc_id}] title {doc_id}\ntext {doc_id}" in prompt, (
                f"document {doc_id}'s title and text must appear in the prompt, under its own id"
            )


# --------------------------------------------------------------------------------------------
# A `content: null` reply (reasoning model puts its answer in reasoning_content instead) must be
# recorded to --responses, not lost to an uncaught TypeError. Task 1 fix round 1, IMPORTANT.
# --------------------------------------------------------------------------------------------

def test_main_records_a_null_content_reply_to_responses_instead_of_crashing(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    # q1: both attempts return content=None (as vLLM does for a reasoning model whose answer
    # landed in reasoning_content) -- q1 is left unscored, but must not crash, and every attempt
    # must still be durably logged.
    scripted = ScriptedTeacher([
        (None, "stop", None, None, None),
        (None, "stop", None, None, None),
        ('["e2", "e3", "e1"]', "stop", None, None, None),
    ])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)  # refused -- q1 unscored -- but must reach here without a bare TypeError

    assert "q1" in str(exc_info.value)
    assert not os.path.exists(out_path)

    with open(responses_path, encoding="utf-8") as f:
        records = [json.loads(line) for line in f if line.strip()]
    q1_records = [r for r in records if r["query_id"] == "q1"]
    assert len(q1_records) == 2, "both null-content attempts must be recorded, not silently lost"
    assert all(r["status"] == "rejected" for r in q1_records)
    assert all(r["content"] is None for r in q1_records)


# --------------------------------------------------------------------------------------------
# Refusal-to-write: any query unscored -> no run file written, non-zero exit, failures listed
# --------------------------------------------------------------------------------------------

def test_main_refuses_to_write_a_run_file_when_any_query_is_unscored(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    # q1 succeeds on its first attempt; q2 fails invalid JSON on both of its RETRY_BUDGET (2)
    # attempts and is therefore left unscored -- the run file must not be written even though q1
    # itself scored fine, and never filled from fusion order for the query that failed.
    scripted = ScriptedTeacher([
        ('["d3", "d1", "d2"]', "stop", None, None, None),
        ("not valid json", "stop", None, None, None),
        ("still not valid json", "stop", None, None, None),
    ])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)

    assert exc_info.value.code != 0
    message = str(exc_info.value)
    assert "q2" in message
    assert "1 / 2" in message  # "1 / 2 queries unscored" -- NOT a bare "1": mutating the count
    # to len(failures)+99 yields "100 / 2 queries unscored", and a bare "1" still matches that,
    # because "1" also occurs inside the embedded reason "Expecting value: line 1 column 1".
    assert not os.path.exists(out_path), "no run file may be written when any query is unscored"
    assert scripted.calls == 3

    # every attempt, including q1's accepted one, was still durably logged to --responses
    with open(responses_path, encoding="utf-8") as f:
        records = [json.loads(line) for line in f if line.strip()]
    assert len(records) == 3
    assert records[0]["status"] == "accepted"
    assert records[0]["query_id"] == "q1"
    assert all(r["status"] == "rejected" for r in records[1:])
    assert all(r["query_id"] == "q2" for r in records[1:])


# --------------------------------------------------------------------------------------------
# Fail loud before any (paid) model call: a query id missing from --queries, or a doc id missing
# from --corpus, must be refused up front -- not surfaced as a KeyError partway through a run
# that already spent money on earlier queries.
# --------------------------------------------------------------------------------------------

def test_main_refuses_a_missing_query_id_before_any_model_call(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    # Rewrite queries.jsonl to omit q2 entirely.
    write(queries_path, json.dumps({"_id": "q1", "text": "query one"}) + "\n")
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    scripted = ScriptedTeacher([])  # must never be called
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)

    assert "q2" in str(exc_info.value)
    assert scripted.calls == 0
    assert not os.path.exists(out_path)
    assert not os.path.exists(responses_path), "nothing should have been written to --responses either"


# --------------------------------------------------------------------------------------------
# Resume: accepted entry skipped; all-rejected entries re-issued with a fresh retry budget;
# where both exist, the accepted entry wins
# --------------------------------------------------------------------------------------------

def test_main_resumes_skipping_accepted_and_reissuing_all_rejected_queries(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    # Ledger from a previous, interrupted invocation:
    #   q1 has BOTH a rejected entry (a failed first attempt) and an accepted entry (a
    #      successful retry) -- the accepted entry must be the one resume uses.
    #   q2 has only rejected entries (its previous invocation exhausted its budget) -- this
    #      invocation must re-issue it with a FRESH RETRY_BUDGET, not treat it as permanently
    #      failed because of entries already in the ledger.
    # Every record carries THIS pass's stamp -- the interrupted-pass case resume exists for.
    this_pass = ledger_pass(run_path)
    prior_records = [
        {"query_id": "q1", "status": "rejected", "content": "bad", "order": None, "reason": "invalid JSON", "pass": this_pass},
        {"query_id": "q1", "status": "accepted", "content": '["d3","d2","d1"]', "order": ["d3", "d2", "d1"], "reason": None, "pass": this_pass},
        {"query_id": "q2", "status": "rejected", "content": "bad", "order": None, "reason": "invalid JSON", "pass": this_pass},
        {"query_id": "q2", "status": "rejected", "content": "bad again", "order": None, "reason": "invalid JSON", "pass": this_pass},
    ]
    write(responses_path, "\n".join(json.dumps(r) for r in prior_records) + "\n")

    # Only ONE reply queued: if q1 were (wrongly) re-called, or if q2's fresh budget were
    # (wrongly) treated as already exhausted, this test would fail -- either by call count or by
    # a SystemExit for an unscored q2.
    scripted = ScriptedTeacher([
        ('["e2", "e1", "e3"]', "stop", None, None, None),
    ])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    tr.main(argv)  # must not raise -- both queries end up scored

    assert scripted.calls == 1, "q1 has an accepted entry and must not trigger a call"

    with open(out_path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    q1_lines = [l for l in lines if l.startswith("q1 ")]
    q2_lines = [l for l in lines if l.startswith("q2 ")]

    # q1's order must be the ACCEPTED ledger entry's order (d3, d2, d1), not the rejected one.
    assert [l.split()[2] for l in q1_lines] == ["d3", "d2", "d1"]
    # q2's order must be the fresh, successful reply from this invocation.
    assert [l.split()[2] for l in q2_lines] == ["e2", "e1", "e3"]


# --------------------------------------------------------------------------------------------
# Resume must not accept a FOREIGN ledger. Final whole-branch review, IMPORTANT: the resume
# branch reused a ledger's accepted order with no check of any kind, so (a) a ledger recorded
# against a different --run replayed wholesale -- exit 0, "300 queries, all scored", and the
# written doc set differed from the --run pool on 300 of 300 queries -- and (b) a ledger from a
# different PASS of the same pool was accepted silently: replaying pass 1's ledger under
# --shuffle-seed 99 made ZERO model calls, wrote byte-identical output, exited 0, and left a
# sidecar claiming "shuffleSeed": 99. (b) is the fabricated zero noise floor plan Task 3 Step 5
# warns about; the verdict doc would report it as a measured result.
# --------------------------------------------------------------------------------------------

def foreign_pool_fixture(tmp_path):
    """A second, complete input set whose pools share NO doc id with make_fixture_files' (x*/y*
    instead of d*/e*), under its own --run path -- a genuinely different pass, not a relabelling."""
    run_path = write(
        tmp_path / "other-a0prime.chunks.trec",
        "q1 Q0 x1 1 0.9 rerank-other\n"
        "q1 Q0 x2 2 0.8 rerank-other\n"
        "q1 Q0 x3 3 0.7 rerank-other\n"
        "q2 Q0 y1 1 0.9 rerank-other\n"
        "q2 Q0 y2 2 0.8 rerank-other\n"
        "q2 Q0 y3 3 0.7 rerank-other\n",
    )
    return run_path


def test_main_refuses_a_responses_ledger_recorded_against_a_different_run(tmp_path, monkeypatch):
    """(a) The ledger's records are stamped with another --run. Every one of its accepted orders
    happens to be internally well-formed, so nothing downstream would have noticed; the refusal
    must come from the stamp, before any model call."""
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    other_run_path = foreign_pool_fixture(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    other_pass = ledger_pass(other_run_path)
    prior_records = [
        {"query_id": "q1", "status": "accepted", "content": '["x3","x2","x1"]', "order": ["x3", "x2", "x1"], "reason": None, "pass": other_pass},
        {"query_id": "q2", "status": "accepted", "content": '["y3","y2","y1"]', "order": ["y3", "y2", "y1"], "reason": None, "pass": other_pass},
    ]
    write(responses_path, "\n".join(json.dumps(r) for r in prior_records) + "\n")

    scripted = ScriptedTeacher([])  # must never be called
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)

    message = str(exc_info.value)
    assert exc_info.value.code != 0
    assert "DIFFERENT" in message
    assert responses_path in message
    assert os.path.abspath(other_run_path) in message, "the refusal must show the ledger's own stamp"
    assert os.path.abspath(run_path) in message, "...and this run's, so the operator can see which differs"
    assert scripted.calls == 0
    assert not os.path.exists(out_path), "a foreign ledger must never produce a run file"


def test_main_refuses_a_responses_ledger_from_another_pass_of_the_same_pool(tmp_path, monkeypatch):
    """(b) Same --run, same pool, same doc ids -- only --shuffle-seed differs, so every accepted
    order IS a valid permutation and the permutation check alone cannot tell the two passes apart.
    Replaying it would issue zero calls and reproduce pass 1 exactly: a noise floor of exactly
    zero, recorded as if measured."""
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    pass_one = ledger_pass(run_path, shuffle_seed=1)  # this invocation runs --shuffle-seed 99
    prior_records = [
        {"query_id": "q1", "status": "accepted", "content": '["d3","d2","d1"]', "order": ["d3", "d2", "d1"], "reason": None, "pass": pass_one},
        {"query_id": "q2", "status": "accepted", "content": '["e3","e2","e1"]', "order": ["e3", "e2", "e1"], "reason": None, "pass": pass_one},
    ]
    write(responses_path, "\n".join(json.dumps(r) for r in prior_records) + "\n")

    scripted = ScriptedTeacher([])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    argv[argv.index("--shuffle-seed") + 1] = "99"
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)

    message = str(exc_info.value)
    assert "shuffleSeed" in message
    assert '"shuffleSeed": 1' in message and '"shuffleSeed": 99' in message
    assert scripted.calls == 0
    assert not os.path.exists(out_path)
    assert not os.path.exists(tr.sidecar_path_for(out_path)), (
        "no sidecar either -- a sidecar recording shuffleSeed 99 over pass 1's replayed answers "
        "is a false provenance record"
    )


def test_main_refuses_a_subsample_ledger_replayed_as_a_different_subsample_pass(tmp_path, monkeypatch):
    """The same hazard through --subsample-seed: Task 3's two 50-query repeat passes differ only
    in which --responses file they use, so a copied or reused ledger path is the realistic slip."""
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    pass_one = ledger_pass(run_path, subsample=1, subsample_seed=5)
    write(responses_path, json.dumps(
        {"query_id": "q1", "status": "accepted", "content": '["d3","d2","d1"]',
         "order": ["d3", "d2", "d1"], "reason": None, "pass": pass_one}
    ) + "\n")

    scripted = ScriptedTeacher([])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path) + [
        "--subsample", "1", "--subsample-seed", "6",
    ]
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)

    assert "subsampleSeed" in str(exc_info.value)
    assert scripted.calls == 0
    assert not os.path.exists(out_path)


def test_main_rejects_a_resumed_order_that_is_not_a_permutation_of_the_pool(tmp_path, monkeypatch):
    """The same contract enforced at the POINT OF USE: the stamp matches this pass, but the
    accepted order names doc ids the pool never contained (a hand-edited or corrupt ledger). Every
    order this script writes is validated as a permutation of that query's ids -- the resume path
    is a way into the run file just as much as a fresh model reply is."""
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    this_pass = ledger_pass(run_path)
    prior_records = [
        {"query_id": "q1", "status": "accepted", "content": '["x3","x2","x1"]', "order": ["x3", "x2", "x1"], "reason": None, "pass": this_pass},
        {"query_id": "q2", "status": "accepted", "content": '["e3","e2"]', "order": ["e3", "e2"], "reason": None, "pass": this_pass},
    ]
    write(responses_path, "\n".join(json.dumps(r) for r in prior_records) + "\n")

    scripted = ScriptedTeacher([])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)

    message = str(exc_info.value)
    assert "q1" in message and "invented" in message  # x1/x2/x3 are not in q1's pool
    assert "q2" in message and "length" in message    # two ids for a three-document pool
    assert not os.path.exists(out_path), "a corrupt resumed order must never reach the run file"


# --------------------------------------------------------------------------------------------
# A truncated TRAILING ledger line (the crash-mid-write spec §6 row 4 exists for) must name
# path:lineno, not raise a bare json.decoder.JSONDecodeError that identifies neither.
# --------------------------------------------------------------------------------------------

def test_read_responses_ledger_names_path_and_line_for_a_truncated_trailing_line(tmp_path):
    responses_path = str(tmp_path / "responses.jsonl")
    good = json.dumps({"query_id": "q1", "status": "accepted", "content": "[]", "order": [], "reason": None, "pass": None})
    truncated = '{"query_id": "q2", "status": "accepted", "content": "[\"e1'  # interrupted append
    write(responses_path, good + "\n" + good + "\n" + truncated)

    with pytest.raises(SystemExit) as exc_info:
        tr.read_responses_ledger(responses_path)

    message = str(exc_info.value)
    assert f"{responses_path}:3" in message, "the operator needs the file AND the line number"
    assert "truncated" in message, "and to be told an interrupted append is the likely cause"


def test_main_reports_path_and_line_for_a_malformed_ledger_before_any_model_call(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")
    write(responses_path, '{"query_id": "q1", "status": "accepted", "order": ["d1"')

    scripted = ScriptedTeacher([])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)

    assert f"{responses_path}:1" in str(exc_info.value)
    assert scripted.calls == 0
    assert not os.path.exists(out_path)


# --------------------------------------------------------------------------------------------
# The length-guard reason must name the knob that actually caused it. finish_reason=length means
# the COMPLETION hit max_tokens; a prompt over budget is the separate max_model_len HTTP 400.
# --------------------------------------------------------------------------------------------

def test_length_finish_reason_blames_max_tokens_not_the_prompt(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    scripted = ScriptedTeacher([
        ('["d2", "d3"', "length", None, None, None),   # q1: truncated completion, both attempts
        ('["d2", "d3"', "length", None, None, None),
        ('["e2", "e3", "e1"]', "stop", None, None, None),
    ])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    with pytest.raises(SystemExit) as exc_info:
        tr.main(argv)

    message = str(exc_info.value)
    assert "max_tokens" in message, "the completion budget is the knob to raise"
    assert "prompt exceeded" not in message, (
        "finish_reason=length is NOT a prompt-too-long failure -- that is max_model_len's HTTP 400, "
        "and naming it here sends the operator to the wrong knob"
    )
    assert "max_model_len" in message, "and the message should distinguish the two explicitly"

    with open(responses_path, encoding="utf-8") as f:
        q1_records = [json.loads(l) for l in f if l.strip() and json.loads(l)["query_id"] == "q1"]
    assert len(q1_records) == 2
    assert all("max_tokens" in r["reason"] for r in q1_records)


# --------------------------------------------------------------------------------------------
# The stamp is written, not merely checked: a fresh pass's records must carry it, or the check
# above would refuse every resume of a genuinely interrupted pass.
# --------------------------------------------------------------------------------------------

def test_main_stamps_each_ledger_record_with_this_pass_and_resumes_from_it(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    scripted = ScriptedTeacher([('["d2", "d3", "d1"]', "stop", None, None, None), ('["e2", "e3", "e1"]', "stop", None, None, None)])
    monkeypatch.setattr(tr, "call_teacher", scripted)
    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    tr.main(argv)

    with open(responses_path, encoding="utf-8") as f:
        records = [json.loads(line) for line in f if line.strip()]
    assert [r["pass"] for r in records] == [ledger_pass(run_path)] * 2

    # Re-running the SAME pass resumes off those stamps: no further calls, same output.
    first_output = open(out_path, encoding="utf-8").read()
    scripted_again = ScriptedTeacher([])
    monkeypatch.setattr(tr, "call_teacher", scripted_again)
    tr.main(argv)
    assert scripted_again.calls == 0
    assert open(out_path, encoding="utf-8").read() == first_output


# --------------------------------------------------------------------------------------------
# Final whole-branch review, Finding 2: the accepted-branch make_record call site in score_query
# must actually carry reasoning/completion_tokens/prompt_tokens through to the --responses
# ledger. Dropping the three fields from that call, or hard-coding `"reasoning": None` inside
# make_record itself, left the earlier length/http_error-branch tests green -- those branches
# pass None for all three fields regardless, so only the accepted branch, with distinct non-None
# values, can catch either mutation.
# --------------------------------------------------------------------------------------------

def test_main_records_reasoning_and_token_counts_for_an_accepted_reply(tmp_path, monkeypatch):
    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
    responses_path = str(tmp_path / "responses.jsonl")
    out_path = str(tmp_path / "teacher.chunks.trec")

    scripted = ScriptedTeacher([
        ('["d1", "d2", "d3"]', "stop", "some reasoning text", 111, 222),
        ('["e1", "e2", "e3"]', "stop", "other reasoning text", 333, 444),
    ])
    monkeypatch.setattr(tr, "call_teacher", scripted)

    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
    tr.main(argv)

    with open(responses_path, encoding="utf-8") as f:
        records = [json.loads(line) for line in f if line.strip()]
    q1_record = next(r for r in records if r["query_id"] == "q1")
    assert q1_record["status"] == "accepted"
    assert q1_record["reasoning"] == "some reasoning text"
    assert q1_record["completion_tokens"] == 111
    assert q1_record["prompt_tokens"] == 222

    q2_record = next(r for r in records if r["query_id"] == "q2")
    assert q2_record["status"] == "accepted"
    assert q2_record["reasoning"] == "other reasoning text"
    assert q2_record["completion_tokens"] == 333
    assert q2_record["prompt_tokens"] == 444

#!/usr/bin/env python3
"""Offline listwise teacher re-ranker (spec docs/specs/2026-09-20-teacher-ceiling-design.md, "Stage
1 of a three-stage line of work on per-tenant distilled rerankers"). Measures the nDCG@10 ceiling a
self-hostable open reasoning model reaches when it re-orders the 50 candidates Iverson's fusion
already retrieved for each query, offline, against a preserved TREC run -- no Iverson server, no
Qdrant, no compose, no Authentik.

One call per query. The prompt carries the query text and all 50 documents (title + abstract from
`corpus.jsonl`), each labelled by doc id, presented in a seeded shuffle (NOT fusion order -- an
anchored teacher would measure the old ranking as much as itself). The model must reply with a JSON
array containing exactly those 50 doc ids, most relevant first; the run file's score column is
rank-derived (`score = 51 - position`), so ties are structurally impossible.

Patterned on `popularity_rerank.py` (`load_run`, `write_run`, argparse `--run`/`--out`) -- per-script
`load_run` helpers are this directory's convention; this script does not import that sibling's.

Stdlib + pytest only. No `openai`, no `requests`, no new dependency.

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py \\
        --run scifact-run-2026-08-26/runs/rerank-a0prime.chunks.trec \\
        --corpus scifact-run-2026-08-26/beir/corpus.jsonl \\
        --queries scifact-run-2026-08-26/beir/queries.jsonl \\
        --base-url http://localhost:8000 --model gpt-oss-120b \\
        --seed 0 --shuffle-seed 0 \\
        --responses scratchpad/teacher-ceiling/main.responses.jsonl \\
        --out scratchpad/teacher-ceiling/teacher.chunks.trec

Then score the output with report.py --pair teacher=<...>.chunks.trec (see this script's task
brief / the design spec's §9 for the exact invocation).
"""
import argparse
import hashlib
import json
import os
import random
import sys
import urllib.request
from datetime import datetime, timezone

# ── Serving constants (spec §4) ──────────────────────────────────────────────────────────────

MAX_COMPLETION_TOKENS = 8192  # NOT enrich_bench.py's MAX_TOKENS=256 -- sized for a 2-3 sentence
# reply, not one 569-594-character listwise answer plus a reasoning trace sharing the same budget.
MAX_MODEL_LEN = 131072  # vLLM's --max-model-len for this teacher (128K); recorded in the sidecar.

# vLLM's guided-decoding parameter is mid-rename (`guided_json` -> `structured_outputs`) as of this
# writing (spec A14). The executing session (Task 3, against a rented instance) MUST confirm this
# name against the installed vLLM version before the paid run, updating this constant if it has
# changed, and record the confirmed name in the run log and the sidecar.
#
# CONFIRMED against this session's vLLM 0.30.0 via its own /openapi.json (2026-09-27): `guided_json`
# is absent from every request schema (ChatCompletionRequest, CompletionRequest, ...);
# `structured_outputs` is present. It is not a bare rename -- the value shape changed too:
# `structured_outputs` is a `StructuredOutputsParams` OBJECT whose `json` field takes the schema
# value `guided_json` used to take directly (confirmed by reading that schema's own definition in
# the same /openapi.json). call_teacher's body-building line below wraps RESPONSE_SCHEMA in
# {"json": ...} accordingly -- if a future session reverts this constant to "guided_json" for an
# older vLLM, that wrapping must be reverted with it, not left in place.
STRUCTURED_OUTPUT_PARAM = "structured_outputs"

# Item type pinned to string (spec: "Pin the guided-decoding schema's item type to string") -- the
# model may still emit unquoted JSON numbers regardless of the schema hint (P22), which
# validate_permutation's str() normalisation handles independently of this schema.
RESPONSE_SCHEMA = {"type": "array", "items": {"type": "string"}, "minItems": 50, "maxItems": 50}
# minItems/maxItems added after live evidence: at temperature 0 with a fixed seed, gpt-oss-120b
# twice returned a well-formed 46-element array for the same query (different ids dropped each
# time -- not deterministic despite the fixed seed), which passed guided-JSON validation and only
# failed Python-side length validation after a full ~3-6 minute generation. Constraining array
# length in the schema itself should make vLLM's guided decoder refuse to terminate the array
# below 50 elements, catching this at generation time instead of after paying for it.
# UNVERIFIED: whether this vLLM version's guided-decoding backend actually enforces minItems/
# maxItems on structured JSON output -- no vLLM is reachable on the dev box to confirm. If it
# does not enforce them, this is a no-op and the post-hoc length check in validate_permutation
# remains the real defense, as it was before this change.

RETRY_BUDGET = 2  # one initial attempt + one retry (spec §6 row 1: "One retry ... on a second
# failure the query is recorded unscored"). A resumed invocation gets a FRESH budget of 2, never a
# reduced one carried over from a previous invocation's rejected ledger entries (spec §6 row 4).

RUN_TAG = "teacher-ceiling"
SIDECAR_COMPOSITE = "31583db5aea49136"  # A0'’s own composite (spec §5 "Sidecar") -- the retrieval
# build that produced the pool is the one under comparison; a teacher-derived composite here would
# make report.py print "BUILD MISMATCH" on every comparison (report.py:592).

# The prompt's format, independent of per-query data -- hashed into the sidecar's
# promptTemplateSha256 so a later change to the wording is visible in the run's provenance.
PROMPT_INSTRUCTIONS = (
    "You are ranking search results by relevance to a query.\n\n"
    "Query: {query}\n\n"
    "Documents (doc id, title, abstract):\n{documents}\n\n"
    "Return a JSON array containing all of the doc ids above, exactly once each, ordered from "
    "most to least relevant to the query. Respond with the JSON array only -- no other text."
)
DOCUMENT_TEMPLATE = "[{doc_id}] {title}\n{text}"
PROMPT_TEMPLATE_SHA256 = hashlib.sha256((PROMPT_INSTRUCTIONS + DOCUMENT_TEMPLATE).encode("utf-8")).hexdigest()


# ── load_run -- 6-column TREC reader (this script's own copy; local convention, not shared) ────

def load_run(path):
    """Parses a 6-column TREC run: `queryId Q0 docId rank score tag`, whitespace-separated.
    Returns {queryId: [docId, ...]} in file (rank) order -- the pool each query is re-ranked over.
    Score and tag are not kept: the teacher only needs the candidate doc-id set and its original
    order (for the pool-invariance check downstream in report.py, which reads the written run, not
    this one). A document appearing twice in one query's pool is rejected, matching
    popularity_rerank.py's load_run -- it would corrupt the permutation check (expected_ids would
    contain a duplicate no reply could ever satisfy)."""
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
    return pool


def load_jsonl_lookup(path, id_key, value_fn):
    """Shared shape for corpus.jsonl and queries.jsonl: one JSON object per line, keyed by
    `id_key`, normalised to str (BEIR ids are numeric strings, but json.loads would hand back an
    int if a file ever quoted one -- str() keeps the lookup key type consistent with load_run's
    query/doc ids, which come from whitespace-split TREC fields and are always str)."""
    lookup = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            lookup[str(record[id_key])] = value_fn(record)
    return lookup


def load_corpus(path):
    """Returns {docId: (title, text)} from corpus.jsonl (keys: _id, title, text)."""
    return load_jsonl_lookup(path, "_id", lambda r: (r.get("title", ""), r.get("text", "")))


def load_queries(path):
    """Returns {queryId: text} from queries.jsonl (keys: _id, text)."""
    return load_jsonl_lookup(path, "_id", lambda r: r["text"])


# ── Seeded shuffle and subsample (P7, P21) ──────────────────────────────────────────────────

def shuffled_doc_order(doc_ids, shuffle_seed, query_id):
    """Seeded per query as `random.Random(f"{shuffle_seed}:{query_id}")` (spec P7) -- deliberately
    NOT `hash(query_id)`, whose `str` hashing is randomized per process (PYTHONHASHSEED) unless
    disabled, which would make the presentation order unreproducible run to run. `random.Random`
    seeded from a string uses a deterministic conversion independent of PYTHONHASHSEED (verified:
    identical output across `PYTHONHASHSEED=random` invocations)."""
    order = list(doc_ids)
    random.Random(f"{shuffle_seed}:{query_id}").shuffle(order)
    return order


def select_subsample(run_query_ids, n, subsample_seed):
    """`random.Random(f"{subsample_seed}").sample(sorted(run_query_ids), n)` -- sorting before
    sampling is load-bearing (P21): if `run_query_ids` were collected into a `set` (or otherwise
    arrived in a non-reproducible order), an unsorted sample would differ across processes/Python
    versions even with the same seed, because `random.sample`'s result depends on input order as
    well as the seed."""
    return random.Random(f"{subsample_seed}").sample(sorted(run_query_ids), n)


# ── Response parsing and permutation validation ─────────────────────────────────────────────

def parse_json_array(text):
    """Parses `text` (a model reply's `message.content`) as JSON and requires a top-level array.
    Returns (ids, None) on success, (None, reason) on failure -- never raises, so a call site can
    record a rejection without wrapping every parse in its own try/except.

    Also catches TypeError: `json.loads(None)` raises one (`the JSON object must be str, bytes or
    bytearray, not NoneType`), and a reasoning model can legitimately return `content: null` on
    vLLM's OpenAI route when its answer landed in `reasoning_content` instead. Without this, that
    reply would abort the whole run with an uncaught traceback -- and since the append to
    --responses happens only after parse_json_array returns, the one response needed to diagnose
    the failure would never be recorded either."""
    try:
        parsed = json.loads(text)
    except (json.JSONDecodeError, TypeError) as e:
        return None, f"invalid JSON: {e}"
    if not isinstance(parsed, list):
        return None, f"expected a JSON array, got {type(parsed).__name__}"
    return parsed, None


def validate_permutation(candidate_ids, expected_ids):
    """Checks that `candidate_ids` is an exact permutation of `expected_ids` (the query's 50 pool
    doc ids). Returns (True, normalized_order) on success, (False, reason) on failure.

    Every element of `candidate_ids` is normalised with `str()` BEFORE any comparison -- a reply
    whose ids are unquoted JSON numbers (`[123, 456, ...]` rather than `["123", "456", ...]`) must
    be ACCEPTED as long as the stringified set matches (P22): without this, `set(int) == set(str)`
    is `False` for every query, rejecting every reply and producing no run file. `expected_ids` is
    already `str` throughout (it comes from `load_run`'s whitespace-split TREC fields), so only the
    candidate side needs normalising.

    Checked in order -- wrong length, then duplicates, then set mismatch (which reports whichever
    of "missing" / "invented" applies, or both) -- so each failure gets a reason naming what's
    actually wrong, for the refusal-to-write rule's failure listing."""
    if len(candidate_ids) != len(expected_ids):
        return False, f"wrong length: expected {len(expected_ids)} ids, got {len(candidate_ids)}"

    normalized = [str(doc_id) for doc_id in candidate_ids]
    seen = set()
    duplicates = set()
    for doc_id in normalized:
        if doc_id in seen:
            duplicates.add(doc_id)
        seen.add(doc_id)
    if duplicates:
        return False, f"duplicated id(s): {sorted(duplicates)}"

    expected_set = set(expected_ids)
    if seen != expected_set:
        missing = sorted(expected_set - seen)
        invented = sorted(seen - expected_set)
        detail = []
        if missing:
            detail.append(f"missing {missing}")
        if invented:
            detail.append(f"invented {invented}")
        return False, "not a permutation of the pool: " + ", ".join(detail)

    return True, normalized


# ── Responses ledger (resume, spec §6 row 4 / A34) ──────────────────────────────────────────

def read_responses_ledger(path):
    """Returns {queryId(str): [record, ...]} in file (append) order, or {} if `path` does not yet
    exist (the first invocation of a pass). Each record's `query_id` field is read back with
    `str()` -- the ledger's query key is written and read as a string, matching how the model's
    reply ids are normalised (spec: "the --responses ledger's query key is written and read as a
    string").

    A malformed line exits with `path:lineno` and the offending text rather than propagating a bare
    `json.decoder.JSONDecodeError` that names neither file nor line. This reader's whole reason to
    exist is the crash-mid-write scenario (spec §6 row 4), where an interrupted append leaves the
    final line truncated, and the operator needs to be told which single line to remove. Silently
    TOLERATING a truncated trailing line was the other option and was rejected: skipping data
    because it looks incomplete is a degradation (this line of work's standing rule is fail loud,
    never degrade), and it would equally mask a genuinely broken writer. Loud, with the exact
    pointer and the one-line fix named, gets the operator moving just as fast."""
    ledger = {}
    if path is None or not os.path.exists(path):
        return ledger
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError as e:
                sys.exit(
                    f"{path}:{lineno}: malformed ledger line ({e}). If this is the file's LAST "
                    "line it is a truncated append from an interrupted pass -- an incomplete "
                    "append is by definition not an accepted entry; delete that one line and "
                    f"resume. Line was: {line[:160]!r}"
                )
            if "query_id" not in record:
                sys.exit(f"{path}:{lineno}: ledger line has no 'query_id' field: {line[:160]!r}")
            query_id = str(record["query_id"])
            ledger.setdefault(query_id, []).append(record)
    return ledger


def accepted_entry(records):
    """The first `status == "accepted"` record among a query's ledger entries, or None. Where a
    query has both an accepted and rejected entry (e.g. attempt 1 rejected, attempt 2 accepted, in
    some earlier invocation), the accepted entry wins (spec §6 row 4) -- this never inspects
    rejected entries once an accepted one exists."""
    for record in records:
        if record.get("status") == "accepted":
            return record
    return None


def append_response(path, record):
    """Appends one JSONL record. Opened and closed per call (not held open across the whole run) so
    that an interrupted pass (spec §6 row 4: "Instance dies mid-run") has every prior attempt
    durably on disk, not buffered in a file object that never got flushed."""
    with open(path, "a", encoding="utf-8") as f:
        f.write(json.dumps(record) + "\n")


def make_record(query_id, status, content, order, reason, pass_id):
    return {
        "query_id": str(query_id),
        "status": status,
        "content": content,
        "order": order,
        "reason": reason,
        "pass": pass_id,
    }


def pass_identity(args):
    """The parameters that identify WHICH PASS a ledger record belongs to, stamped into every
    record by `make_record` and re-checked by `validate_ledger_pass` before a resume reuses any of
    them. Two invocations differing in any of these are different measurements, not two halves of
    one interrupted pass: `--run` is the candidate pool itself, and `--shuffle-seed` /
    `--subsample` / `--subsample-seed` are which presentation of that pool the teacher was asked to
    rank. `--run` is absolutised so the same file reached by a different relative path (a resume
    launched from another working directory) still resumes rather than falsely refusing.

    Deliberately NOT included: `--seed`, `--model`, `--base-url`. Those are serving identity, which
    the sidecar already records, and they do not change which questions were asked."""
    return {
        "run": os.path.abspath(args.run),
        "shuffleSeed": args.shuffle_seed,
        "subsample": args.subsample,
        "subsampleSeed": args.subsample_seed,
    }


def validate_ledger_pass(path, ledger, expected_pass):
    """Refuses loudly -- before any model call -- when `--responses` holds records written by a
    DIFFERENT pass, instead of replaying them (spec §6 row 4: "Resume is for completing an
    interrupted pass only"; plan Task 3 Step 5: "otherwise resume replays pass 1 and the noise
    floor reads as exactly zero").

    Two verified failures this closes. A ledger recorded against a different `--run` replayed
    wholesale -- exit 0, "all scored", and every written doc set foreign to the pool actually
    passed in. And a ledger from a different pass of the SAME pool was accepted silently: replaying
    pass 1's ledger under a new `--shuffle-seed` issued zero model calls, wrote byte-identical
    output, and left a sidecar recording the new seed -- a false provenance record and a fabricated
    zero noise floor in the verdict doc.

    A record with no `pass` stamp at all (a ledger written before this stamp existed) is a mismatch
    too: an unstamped record cannot be shown to belong to this pass, and guessing that it does is
    exactly the silent replay this exists to stop."""
    for query_id, records in ledger.items():
        for record in records:
            stamped = record.get("pass")
            if stamped != expected_pass:
                sys.exit(
                    f"[teacher_rerank] refusing to resume -- {path} was recorded by a DIFFERENT "
                    f"pass (first mismatch at query {query_id!r}):\n"
                    f"  ledger:   {json.dumps(stamped, sort_keys=True)}\n"
                    f"  this run: {json.dumps(expected_pass, sort_keys=True)}\n"
                    "  resume is for completing an interrupted pass only (spec §6 row 4); a new "
                    "pass needs a fresh --responses path"
                )


# ── Prompt construction and the model call (untested here -- spec: "Everything except the model
#    call is tested on the dev box before any GPU is rented") ──────────────────────────────────

def build_prompt(query_text, shuffled_doc_ids, corpus):
    """The query text plus all of `shuffled_doc_ids`' documents (title + abstract), in the seeded
    shuffle order the caller already computed -- never fusion order (spec §3)."""
    documents = "\n\n".join(
        DOCUMENT_TEMPLATE.format(doc_id=doc_id, title=corpus[doc_id][0], text=corpus[doc_id][1])
        for doc_id in shuffled_doc_ids
    )
    return PROMPT_INSTRUCTIONS.format(query=query_text, documents=documents)


def call_teacher(base_url, model, prompt, seed, api_key=None):
    """POSTs to `{base_url}/v1/chat/completions` with stdlib `urllib.request`, the same transport
    shape as `enrich_bench.py:56-62` (temperature 0, stream false), plus `max_tokens:
    MAX_COMPLETION_TOKENS` (NOT enrich_bench.py's 256, spec §4), a fixed `seed`, and vLLM guided
    decoding to `RESPONSE_SCHEMA` under `STRUCTURED_OUTPUT_PARAM`. Returns (content, finish_reason);
    the caller checks `finish_reason == "length"` before ever parsing `content` (spec §6 row 1).

    `api_key`, when given, is sent as `Authorization: Bearer <api_key>` -- some vLLM deployments
    (e.g. a RunPod template that launches `vllm serve --api-key` from a `VLLM_API_KEY` env var
    before the operator ever runs this script) require it; the header is simply omitted when
    `api_key` is None, so unauthenticated servers are unaffected."""
    body = {
        "model": model,
        "messages": [{"role": "user", "content": prompt}],
        "temperature": 0,
        "stream": False,
        "max_tokens": MAX_COMPLETION_TOKENS,
        "seed": seed,
        STRUCTURED_OUTPUT_PARAM: {"json": RESPONSE_SCHEMA},
    }
    req = urllib.request.Request(
        f"{base_url}/v1/chat/completions",
        data=json.dumps(body).encode("utf-8"),
        method="POST",
    )
    req.add_header("Content-Type", "application/json")
    if api_key:
        req.add_header("Authorization", f"Bearer {api_key}")
    with urllib.request.urlopen(req, timeout=600) as resp:
        parsed = json.loads(resp.read())
    choice = parsed["choices"][0]
    return choice["message"]["content"], choice.get("finish_reason")


# ── Per-query scoring: resume + retry budget (spec §6 rows 1 and 4) ────────────────────────────

def score_query(query_id, expected_ids, query_text, corpus, args, ledger):
    """Returns (order, failure_reason). `order` is a list of doc ids (the query's new ranking,
    most relevant first) on success, None on failure. Exactly one of the two is None.

    Resume: a query with an accepted ledger entry is skipped entirely -- no call is issued, and its
    accepted order is reused (spec §6 row 4). Otherwise this invocation gets a FRESH RETRY_BUDGET
    (2) attempts regardless of how many rejected entries already sit in the ledger from a previous
    invocation -- the ledger is a durable log of every attempt, not a countdown.

    A resumed order is re-validated against this query's pool before it is reused: the script's
    contract is that every order it writes is a permutation of that query's 50 ids, and the resume
    path is a way into the run file just as much as a fresh model reply is. `validate_ledger_pass`
    already refused a ledger stamped by another pass; this is the same rule enforced at the point
    of use, so a hand-edited or otherwise corrupt entry cannot reach `write_run` unchecked."""
    pass_id = pass_identity(args)
    existing = ledger.get(str(query_id), [])
    accepted = accepted_entry(existing)
    if accepted is not None:
        ok, result = validate_permutation(accepted.get("order") or [], expected_ids)
        if not ok:
            return None, f"resumed ledger entry is not a permutation of this query's pool: {result}"
        return result, None

    shuffled = shuffled_doc_order(expected_ids, args.shuffle_seed, query_id)
    prompt = build_prompt(query_text, shuffled, corpus)

    last_reason = None
    for _attempt in range(RETRY_BUDGET):
        content, finish_reason = call_teacher(
            args.base_url, args.model, prompt, args.seed, api_key=args.api_key
        )
        if finish_reason == "length":
            last_reason = (
                f"finish_reason=length (the completion hit the {MAX_COMPLETION_TOKENS}-token "
                "max_tokens budget before the reply was complete -- raise MAX_COMPLETION_TOKENS. "
                f"A prompt over the {MAX_MODEL_LEN}-token max_model_len is the SEPARATE HTTP 400 "
                "failure, not this one.)"
            )
            append_response(args.responses, make_record(query_id, "rejected", content, None, last_reason, pass_id))
            continue

        ids, parse_error = parse_json_array(content)
        if parse_error is not None:
            last_reason = parse_error
            append_response(args.responses, make_record(query_id, "rejected", content, None, last_reason, pass_id))
            continue

        ok, result = validate_permutation(ids, expected_ids)
        if ok:
            append_response(args.responses, make_record(query_id, "accepted", content, result, None, pass_id))
            return result, None

        last_reason = result
        append_response(args.responses, make_record(query_id, "rejected", content, None, last_reason, pass_id))

    return None, last_reason


# ── Run-file writer: score = 51 - position (spec §3) ────────────────────────────────────────

def write_run(path, scored_pool, pool_query_order):
    """Writes the re-ranked run as 6-column TREC: `queryId Q0 docId rank score tag`. `rank` is the
    1-indexed position in `scored_pool[queryId]` (the teacher's new, most-relevant-first order);
    `score = 51 - position`, so ranks 1..50 map to scores 50..1 and no score is ever 0.000000 --
    the `+1` is deliberate (spec §3): a rank-50 score of 0.000000 would make report.py's structural
    check misread 14,700 of 15,000 scores as "non-zero" and read as a defect. Scores are
    structurally distinct integers per query, so duplicate `(qid, score)` pairs (gate check 5) can
    only come from a writer bug, never from the ranking itself."""
    with open(path, "w", encoding="utf-8") as f:
        for query_id in pool_query_order:
            for position, doc_id in enumerate(scored_pool[query_id], start=1):
                score = 51 - position
                f.write(f"{query_id} Q0 {doc_id} {position} {score:.6f} {RUN_TAG}\n")


# ── Sidecar (spec §5 "Sidecar") -- write_sidecar is exercised only observationally by report.py
#    at Task 2; it is not among Step 1's 8 enumerated test cases. ─────────────────────────────

def sidecar_path_for(out_path):
    """Mirrors report.py's sidecar_path_for (report.py:167-182): strip a trailing `.trec`, then a
    trailing `.similar` or `.chunks`, then append `.meta.json`. `--out` must therefore end in
    `<label>.chunks.trec` for report.py to find this sidecar at all (spec A26) -- a mismatch
    degrades to report.py printing "BUILD UNKNOWN", which is loud, not silent."""
    base = out_path
    if base.endswith(".trec"):
        base = base[: -len(".trec")]
    for suffix in (".similar", ".chunks"):
        if base.endswith(suffix):
            base = base[: -len(suffix)]
            break
    return base + ".meta.json"


def write_sidecar(out_path, args):
    """Writes `<label>.meta.json` beside the run file, carrying A0's own composite (spec: "the
    retrieval build that produced the pool is the same one under comparison") and the teacher's
    identity under `reranker`. report.py reads only `composite`; every other key is inert to it.

    `subsample`/`subsampleSeed` are recorded whenever `--subsample` was given (both None
    otherwise) -- without this, Task 3's two 50-query repeat passes would write a sidecar
    indistinguishable from the 300-query main run's, leaving no on-disk record of which pass was
    which (plan line 121 / this task's brief line 39: "records both values in the sidecar").

    `vllmVersion`/`quantisation`/`instanceType` come from the optional `--vllm-version`,
    `--quantisation`, `--instance-type` flags (controller ruling, Task 1 fix round 1) and are
    `null` when the flag was omitted -- never guessed here."""
    sidecar = {
        "configLabel": "teacher-ceiling",
        "composite": SIDECAR_COMPOSITE,
        "recordedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "reranker": {
            "baseUrl": args.base_url,
            "modelId": args.model,
            "quantisation": args.quantisation,
            "vllmVersion": args.vllm_version,
            "temperature": 0,
            "seed": args.seed,
            "shuffleSeed": args.shuffle_seed,
            "maxModelLen": MAX_MODEL_LEN,
            "maxCompletionTokens": MAX_COMPLETION_TOKENS,
            "structuredOutputParam": STRUCTURED_OUTPUT_PARAM,
            "promptTemplateSha256": PROMPT_TEMPLATE_SHA256,
            "instanceType": args.instance_type,
            "subsample": args.subsample,
            "subsampleSeed": args.subsample_seed,
        },
    }
    with open(sidecar_path_for(out_path), "w", encoding="utf-8") as f:
        json.dump(sidecar, f, indent=2)
        f.write("\n")


# ── CLI ──────────────────────────────────────────────────────────────────────────────────────

def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run", required=True, help="the captured 6-column TREC run to re-rank (e.g. rerank-a0prime.chunks.trec)")
    ap.add_argument("--corpus", required=True, help="BEIR corpus.jsonl (keys: _id, title, text)")
    ap.add_argument("--queries", required=True, help="BEIR queries.jsonl (keys: _id, text)")
    ap.add_argument("--base-url", required=True, help="vLLM's OpenAI-compatible base URL, e.g. http://localhost:8000")
    ap.add_argument("--model", required=True, help="the model id vLLM was served with")
    ap.add_argument("--seed", type=int, required=True, help="fixed seed passed to the chat completion request")
    ap.add_argument("--shuffle-seed", type=int, required=True, help="seed for the per-query document presentation shuffle")
    ap.add_argument("--responses", required=True, help="JSONL ledger of raw per-query responses (appended to; resumable)")
    ap.add_argument("--out", required=True, help="path the re-ranked TREC run is written to (must end <label>.chunks.trec, spec A26)")
    ap.add_argument("--subsample", type=int, default=None, help="if given (with --subsample-seed), re-rank only N query ids")
    ap.add_argument("--subsample-seed", type=int, default=None, help="seed for --subsample's selection")
    ap.add_argument("--vllm-version", default=None, help="serving identity recorded verbatim in the sidecar's reranker block; omit to record null")
    ap.add_argument("--quantisation", default=None, help="serving identity recorded verbatim in the sidecar's reranker block; omit to record null")
    ap.add_argument("--api-key", default=None, help="sent as 'Authorization: Bearer <key>' if given; omitted otherwise (some vLLM deployments require it, e.g. RunPod's VLLM_API_KEY template)")
    ap.add_argument("--instance-type", default=None, help="serving identity recorded verbatim in the sidecar's reranker block; omit to record null")
    return ap


def validate_inputs_before_any_model_call(pool, pool_query_order, corpus, queries):
    """Checked once, up front, before the first (paid, on a rented GPU) model call: every query id
    about to be processed must have text in --queries, and every doc id in its pool must have an
    entry in --corpus. Without this, a missing id would surface as a bare KeyError from inside
    build_prompt partway through the run -- after some earlier queries had already triggered real
    model calls -- rather than a clean refusal before any money is spent. Exits with every offending
    id listed, not just the first one found."""
    missing_queries = sorted(qid for qid in pool_query_order if qid not in queries)
    missing_docs = sorted({
        doc_id
        for qid in pool_query_order
        for doc_id in pool[qid]
        if doc_id not in corpus
    })
    if missing_queries or missing_docs:
        lines = []
        if missing_queries:
            lines.append(f"  {len(missing_queries)} query id(s) missing from --queries: {missing_queries}")
        if missing_docs:
            lines.append(f"  {len(missing_docs)} doc id(s) missing from --corpus: {missing_docs}")
        sys.exit("[teacher_rerank] refusing to start -- inputs are incomplete:\n" + "\n".join(lines))


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    if (args.subsample is None) != (args.subsample_seed is None):
        sys.exit("--subsample and --subsample-seed must be given together")

    pool = load_run(args.run)
    corpus = load_corpus(args.corpus)
    queries = load_queries(args.queries)

    pool_query_order = list(pool.keys())
    if args.subsample is not None:
        if args.subsample > len(pool_query_order):
            sys.exit(f"--subsample {args.subsample} exceeds the run's {len(pool_query_order)} query ids")
        pool_query_order = select_subsample(pool_query_order, args.subsample, args.subsample_seed)

    validate_inputs_before_any_model_call(pool, pool_query_order, corpus, queries)

    ledger = read_responses_ledger(args.responses)
    validate_ledger_pass(args.responses, ledger, pass_identity(args))

    scored_pool = {}
    failures = []
    for query_id in pool_query_order:
        expected_ids = pool[query_id]
        query_text = queries[query_id]
        order, reason = score_query(query_id, expected_ids, query_text, corpus, args, ledger)
        if order is None:
            failures.append((query_id, reason))
        else:
            scored_pool[query_id] = order

    if failures:
        listing = "\n".join(f"  {qid}: {reason}" for qid, reason in failures)
        sys.exit(
            f"[teacher_rerank] {len(failures)} / {len(pool_query_order)} queries unscored -- "
            f"no run file written (never filled from fusion order):\n{listing}"
        )

    write_run(args.out, scored_pool, pool_query_order)
    write_sidecar(args.out, args)
    print(f"[teacher_rerank] wrote {args.out} ({len(pool_query_order)} queries, all scored)")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""The MatchPattern leg of the MatchPattern-RRF benchmark (spec
docs/specs/2026-09-28-matchpattern-rrf-test-design.md, "Phase 0" probes and "Phase 2").

Talks to the live MatchPattern RPC through the Python SDK (Iverson.Clients/Python). Four subcommands,
all taking --out (the benchmark's OUT directory) and --baseline (a <label>.chunks.trec whose 50
candidates per query are the documents the pattern leg scores):

    probe-a    Two CHUNKS calls over one candidate's chunks. (1) A+, ALL ROWS, s = SIMILARITY(text, q)
               plus a RUNNING SUM(SIMILARITY(A.text, q)), so the aggregate-over-SIMILARITY path -- which
               no server test covers, spec assumption 9 -- is exercised live. (2) The text-free
               calibration call. Prints the rows, and fails on no rows, a NULL s, a running sum that is
               not the cumulative sum of s, or a calibration (ci, s) that differs from call (1)'s
               (chunk_index, s) for any chunk.
    probe-b    Times the calibration call and the theta call (at a placeholder theta) in both call
               shapes over the 10 lowest query ids; projects the per-candidate pass as
               mean seconds per call x 672 x 50; applies the spec's rule (per-candidate if <= 8 h per
               pass, else whole-corpus if no whole-corpus call hit the 30 s timeout, else STOP); prints
               and writes OUT/pattern/probe-b.json.
    calibrate  One similarity pass over every candidate of every query: A, A AS TRUE, ONE ROW PER
               MATCH, s = SIMILARITY(text, q), ci = chunk_index -- one text-free row per chunk. Writes calibration.tsv and theta.json (p50/p75/p90 of s over
               the chunks of the 100 lowest query ids' candidates) and starts build.json.
    run        One theta pass: A+, A AS SIMILARITY(text, q) > theta, ONE ROW PER MATCH, run_len =
               COUNT(*), run_sum = SUM(SIMILARITY(A.text, q)). Writes scores-<t>.tsv and
               mp-<t>-run_len.trec / mp-<t>-run_sum.trec, and appends to build.json.

The two call shapes (spec Phase 0 step 5):
    per-candidate  one call per (query, candidate), `where Id = <parent GUID>`, limit
                   PER_CANDIDATE_LIMIT (10,000). A CHUNKS partition larger than MaxPartitionRows
                   (10,000 by default) is refused by the server before any row is emitted
                   (QdrantChunkRowSource: PatternBudgetExceededException("MaxPartitionRows"), mapped to
                   ResourceExhausted), and a call emits at most one row per chunk (calibration) or one
                   per match (theta, matches do not overlap), so a per-candidate call can reach 10,000
                   rows only for a parent of exactly 10,000 chunks. 10,000 is also the default
                   MaxOutputRows, so the call is valid on a default stack.
    whole-corpus   one call per query with no where, limit WHOLE_CORPUS_LIMIT (18,623, which the stack
                   must allow via Patterns__Limits__MaxOutputRows=18623). The corpus holds 18,622
                   chunks, so no call can emit 18,623 rows; the candidates are kept client-side.
In both shapes the server stops writing at exactly `limit` rows (ObjectSearchGrpcService.MatchPattern
.cs: `if (++written == outputLimit) return;`), so every truncated result has count == limit; a count
at limit therefore aborts as possible truncation. It can only over-abort, never under-abort.

Failure is loud (spec "Failure"): any gRPC error (timeout, InvalidArgument, ...), a count at limit,
a NULL similarity, a row for an unexpected parent, or a /build composite that differs from the
baseline sidecar's (GET before and after every pass) exits non-zero before any output of that pass is
written. A failed query is never read as "no runs".

Identity. The service credentials come from the same env vars as Iverson.Agents and the LoadTest
(IVERSON_GRPC_URL, IVERSON_CLIENT_ID, IVERSON_CLIENT_SECRET, IVERSON_TOKEN_ENDPOINT,
IVERSON_CLIENT_SCOPE -- /home/ben/iverson-benchmark-data/bench-env.sh exports all five). The
service token is minted by this script itself with a `client_credentials` POST (urllib, not
IversonClientCredentials -- see mint_service_token), sent with header Host: IVERSON_ACTING_USER_HOST_HEADER
(default authentik-server:9000): Authentik stamps the JWT's `iss` from the request's Host header
(same mechanism deploy/scripts/mint_acting_user_token.py's docstring and
Iverson.LoadTest/Program.cs's MintClientCredentialsTokenAsync document), and the SDK's own
IversonClientCredentials path cannot set that header, so a token it mints carries
iss=http://localhost:9000/ and the API's issuer validation rejects it with 401 before authorization
is evaluated. The service token is re-minted at expires_in - SERVICE_TOKEN_REFRESH_MARGIN_SECONDS
(300s) after the mint that produced it -- 3600s tokens, so ~55 minutes. The acting-user token is
minted here, not read once:
Authentik issues it with access_token_validity hours=2 (compose-only/service-clients.yaml,
iverson-loadtest-human provider) and a per-candidate pass may run for up to 8 hours. So this script
runs deploy/scripts/mint_acting_user_token.py --target compose --username
iverson-loadtest-bypass-user --password $IVERSON_ACTING_USER_BYPASS_PASSWORD as a subprocess, and
re-mints once the token is TOKEN_REFRESH_SECONDS (90 minutes) old. Both tokens live in one
TokenSession and ride the gRPC channel as metadata-call-credentials plugins that read the session's
current token on every call (Iverson.Clients/Python/conformance/driver.py's build_driver_channel
pattern), so a refresh takes effect on the next call without rebuilding the channel or the
coordinator.

SDK import: the SDK is not pip-installed on this box; like Iverson.Clients/Python/conformance/
driver.py:25, the SDK root is put on sys.path. grpcio and protobuf come from the user site-packages.
No PYTHONPATH is needed. Run with:

    . /home/ben/iverson-benchmark-data/bench-env.sh
    export IVERSON_ACTING_USER_BYPASS_PASSWORD=...
    python3 Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py probe-a --out OUT --baseline OUT/runs/<label>.chunks.trec
    python3 Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py probe-b --out OUT --baseline ...
    python3 Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py calibrate --shape <shape> --out OUT --baseline ...
    python3 Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py run --shape <shape> --theta p50 --out OUT --baseline ...
"""
import argparse
import json
import math
import os
import statistics
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from urllib.parse import urlsplit

SCRIPTS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(SCRIPTS_DIR)))
SDK_ROOT = os.path.join(REPO_ROOT, "Iverson.Clients", "Python")
MINT_SCRIPT = os.path.join(REPO_ROOT, "Iverson.Server", "deploy", "scripts", "mint_acting_user_token.py")

sys.path.insert(0, SCRIPTS_DIR)
sys.path.insert(0, SDK_ROOT)
import grpc  # noqa: E402
import report  # noqa: E402  (sidecar_path_for / load_build_composite -- stdlib-only at import)
from iverson_client import EntityCoordinator, iverson_entity, iverson_key, match_pattern  # noqa: E402
from iverson_client.auth import ACTING_USER_METADATA_KEY  # noqa: E402
from iverson_client.generated import object_search_pb2 as pb  # noqa: E402

TYPE_NAME = "BenchmarkDocument"      # Iverson.LoadTest/Entities/BenchmarkDocument.cs
CHUNK_PROPERTY = "Body"              # its [IversonChunk] property; matched case-insensitively
KEY_COLUMN = "Id"                    # its [IversonKey]; BuildChunksFilter accepts EQUALS on the key
PATTERN = "A+"                       # the theta call (and probe A's running-sum call)
CALIBRATION_PATTERN = "A"            # one single-row match per chunk
PER_CANDIDATE_LIMIT = 10_000
WHOLE_CORPUS_LIMIT = 18_623
SHAPES = ("per-candidate", "whole-corpus")
THETAS = ("p50", "p75", "p90")
SCORES = ("run_len", "run_sum")
THETA_SAMPLE_QUERIES = 100
PROBE_QUERIES = 10
PROBE_PLACEHOLDER_THETA = 0.6
PROJECTION_QUERIES = 672
PROJECTION_CANDIDATES = 50
PER_CANDIDATE_BUDGET_HOURS = 8.0
WHOLE_CORPUS_TIMEOUT_SECONDS = 30.0
TOKEN_REFRESH_SECONDS = 90 * 60
SERVICE_TOKEN_REFRESH_MARGIN_SECONDS = 300      # re-mint expires_in - this many seconds after mint
BYPASS_USERNAME = "iverson-loadtest-bypass-user"
BYPASS_PASSWORD_ENV = "IVERSON_ACTING_USER_BYPASS_PASSWORD"
ACTING_USER_HOST_HEADER_ENV = "IVERSON_ACTING_USER_HOST_HEADER"
DEFAULT_ACTING_USER_HOST_HEADER = "authentik-server:9000"
CORPORA = "/home/ben/repositories/iverson-benchmark-corpora"
DEFAULT_KEYMAP = f"{CORPORA}/freshstack-2048-2026-09-07/keymap.json"
DEFAULT_QUERIES = f"{CORPORA}/freshstack-2048-2026-09-07/beir/queries.jsonl"


@iverson_entity
class BenchmarkDocument:
    """Only its name matters: EntityCoordinator needs an @iverson_entity class, and the SDK derives
    the type name from cls.__name__ (annotations.py `"type_name": cls.__name__`). The request's own
    type_name comes from match_pattern(TYPE_NAME)."""
    id: str = iverson_key()


assert BenchmarkDocument._iverson_meta["type_name"] == TYPE_NAME


# ── Expressions and requests (pure) ─────────────────────────────────────────────────────

def escape_literal(text):
    """A single quote inside an expression string literal is written twice (ExpressionLexer.ReadString)."""
    return text.replace("'", "''")


def similarity(column, query):
    return f"SIMILARITY({column}, '{escape_literal(query)}')"


def format_theta(theta):
    """repr() is the shortest round-trip spelling; the lexer parses it with double.Parse (exponent
    included), so the server compares against exactly this double."""
    theta = float(theta)
    if not math.isfinite(theta):
        sys.exit(f"theta {theta!r} is not finite")
    return repr(theta)


def shape_limit(shape):
    return {"per-candidate": PER_CANDIDATE_LIMIT, "whole-corpus": WHOLE_CORPUS_LIMIT}[shape]


def _chunks_builder(guid):
    builder = match_pattern(TYPE_NAME).chunks(CHUNK_PROPERTY)
    if guid is not None:
        builder = builder.where(KEY_COLUMN, pb.EQUALS, guid)
    return builder


def calibration_request(query, guid, limit):
    """A, A AS TRUE, ONE ROW PER MATCH, s = SIMILARITY(text, q), ci = chunk_index. Every chunk is its
    own one-row match (SKIP PAST LAST ROW), so the call emits one row per chunk carrying only
    parent_key, s and ci -- no text, which ALL ROWS would stream for every chunk (CompiledPattern.cs:
    130 fixes the ALL ROWS columns at parent_key, chunk_index, text). The engine probe
    (.superpowers/rrf-proto/engine-probe) showed bare chunk_index yields each chunk's own index and
    s its own similarity. `guid` None is the whole-corpus shape."""
    return (_chunks_builder(guid).pattern(CALIBRATION_PATTERN).define("A", "TRUE")
            .measure("s", similarity("text", query))
            .measure("ci", "chunk_index")
            .rows_per_match(pb.ONE_ROW).limit(limit).build())


def probe_a_request(query, guid, limit):
    """Probe A's row-carrying call: A+, A AS TRUE, ALL ROWS, s and a RUNNING SUM(SIMILARITY(A.text, q))."""
    return (_chunks_builder(guid).pattern(PATTERN).define("A", "TRUE")
            .measure("s", similarity("text", query))
            .measure("run_sum", f"SUM({similarity('A.text', query)})")
            .rows_per_match(pb.ALL_ROWS_SHOW_EMPTY).limit(limit).build())


def theta_request(query, theta, guid, limit):
    """A+, A AS SIMILARITY(text, q) > theta, ONE ROW PER MATCH, run_len and run_sum."""
    return (_chunks_builder(guid).pattern(PATTERN)
            .define("A", f"{similarity('text', query)} > {format_theta(theta)}")
            .measure("run_len", "COUNT(*)")
            .measure("run_sum", f"SUM({similarity('A.text', query)})")
            .rows_per_match(pb.ONE_ROW).limit(limit).build())


# ── Calls ───────────────────────────────────────────────────────────────────────────────

def mint_service_token(client_id, client_secret, token_endpoint, scope, host_header,
                        urlopen=urllib.request.urlopen):
    """Mint the client-credentials service token ourselves (a `client_credentials` POST, urllib),
    rather than through IversonClientCredentials/IversonClient, because the SDK's token path cannot
    set a Host header and Authentik stamps the JWT's `iss` from the request's Host (module docstring
    "Identity"). Sends `grant_type=client_credentials`, `client_id`, `client_secret`, and `scope`
    when truthy, with header `Host: host_header`. Aborts loudly (sys.exit), WITHOUT printing the
    secret or the token, on anything but a 200 response carrying an access_token and a positive
    expires_in. Returns (token, expires_in). `urlopen` is injected so tests need no network."""
    params = {"grant_type": "client_credentials", "client_id": client_id, "client_secret": client_secret}
    if scope:
        params["scope"] = scope
    request = urllib.request.Request(
        token_endpoint,
        data=urllib.parse.urlencode(params).encode("utf-8"),
        headers={"Content-Type": "application/x-www-form-urlencoded", "Host": host_header},
        method="POST")
    try:
        response = urlopen(request, timeout=30)
    except Exception as e:  # noqa: BLE001 -- every failure mode means "cannot mint the service token"
        sys.exit(f"[pattern_leg] minting the service token failed: {type(e).__name__}: {e}")
    with response:
        status = getattr(response, "status", getattr(response, "code", 200))
        if status != 200:
            sys.exit(f"[pattern_leg] minting the service token failed: HTTP {status} from {token_endpoint}")
        payload = json.load(response)
    token = payload.get("access_token")
    if not token:
        sys.exit(f"[pattern_leg] minting the service token failed: the response carried no access_token")
    expires_in = payload.get("expires_in")
    if not isinstance(expires_in, (int, float)) or expires_in <= 0:
        sys.exit(f"[pattern_leg] minting the service token failed: invalid expires_in {expires_in!r}")
    return token, float(expires_in)


class TokenSession:
    """Owns the two identities the pattern leg's gRPC channel carries as bearer tokens:

        service token       `authorization`                  minted by `mint_service` (returns
                                                                (token, expires_in)); re-minted
                                                                expires_in - `service_refresh_margin`
                                                                seconds after the mint that produced
                                                                it (3600s tokens, so ~55 minutes).
        acting-user token    `x-acting-user-authorization`     minted by `mint_acting_user` (returns
                                                                a token); re-minted every
                                                                `refresh_seconds` (default 90
                                                                minutes) -- Authentik does not report
                                                                this token's own expiry over this
                                                                path, so a fixed refresh age stands.

    Both mints and the clock are injected so tests need neither Authentik nor a subprocess. A
    metadata-call-credentials plugin on the gRPC channel calls `service_token()`/
    `acting_user_token()` on every RPC (see build_channel), so a refresh here takes effect on the
    next call without rebuilding the channel or the coordinator."""

    def __init__(self, mint_acting_user, mint_service, clock=time.monotonic,
                 refresh_seconds=TOKEN_REFRESH_SECONDS,
                 service_refresh_margin_seconds=SERVICE_TOKEN_REFRESH_MARGIN_SECONDS):
        self._mint_acting_user = mint_acting_user
        self._mint_service = mint_service
        self._clock = clock
        self._refresh = refresh_seconds
        self._service_margin = service_refresh_margin_seconds

        self._acting_token = None
        self._acting_minted_at = None
        self.acting_user_mints = 0

        self._service_token = None
        self._service_refresh_at = None
        self.service_mints = 0

    def acting_user_token(self):
        now = self._clock()
        if self._acting_token is None or now - self._acting_minted_at >= self._refresh:
            self._acting_token = self._mint_acting_user()
            self._acting_minted_at = now
            self.acting_user_mints += 1
        return self._acting_token

    def service_token(self):
        now = self._clock()
        if self._service_token is None or now >= self._service_refresh_at:
            self._service_token, expires_in = self._mint_service()
            self._service_refresh_at = now + expires_in - self._service_margin
            self.service_mints += 1
        return self._service_token


class _ServiceBearerAuthPlugin(grpc.AuthMetadataPlugin):
    """Attaches the session's current service token to every call as `authorization`, re-read at
    call time so a refresh takes effect on this same long-lived channel without rebuilding it.
    Mirrors Iverson.Clients/Python/conformance/driver.py's _DriverStaticBearerAuthPlugin, but reads
    a live TokenSession instead of a token fixed at construction."""

    def __init__(self, get_token):
        self._get_token = get_token

    def __call__(self, context, callback):
        try:
            callback((("authorization", f"Bearer {self._get_token()}"),), None)
        except BaseException as exc:  # noqa: BLE001
            callback(None, exc)


class _ActingUserAuthPlugin(grpc.AuthMetadataPlugin):
    """Attaches the session's current acting-user token to every call as ACTING_USER_METADATA_KEY
    (`x-acting-user-authorization`), re-read at call time. Mirrors driver.py's
    _DriverActingUserAuthPlugin, but reads a live TokenSession instead of a token fixed at
    construction."""

    def __init__(self, get_token):
        self._get_token = get_token

    def __call__(self, context, callback):
        try:
            callback(((ACTING_USER_METADATA_KEY, f"Bearer {self._get_token()}"),), None)
        except BaseException as exc:  # noqa: BLE001
            callback(None, exc)


def build_channel(host, port, use_tls, session):
    """The pattern leg's single gRPC channel, carrying both identities from `session`'s current
    tokens (module docstring "Identity"; pattern per
    Iverson.Clients/Python/conformance/driver.py's build_driver_channel). Some ChannelCredentials is
    always required as the base once CallCredentials are present -- grpcio rejects CallCredentials
    on a bare insecure_channel -- so local_channel_credentials() (a "trusted network" designation,
    NOT real TLS/encryption) stands in for the compose stack's plaintext h2c, the same explicit
    choice IversonClient makes (core.py:911-918)."""
    address = f"{host}:{port}"
    base_creds = grpc.ssl_channel_credentials() if use_tls else grpc.local_channel_credentials()
    channel_creds = grpc.composite_channel_credentials(
        base_creds,
        grpc.metadata_call_credentials(_ServiceBearerAuthPlugin(session.service_token)),
        grpc.metadata_call_credentials(_ActingUserAuthPlugin(session.acting_user_token)),
    )
    return grpc.secure_channel(address, channel_creds)


def execute(coordinator, request, what):
    """Run one MatchPattern call to completion. A gRPC error or a count at `limit` aborts."""
    try:
        rows = coordinator.match_pattern(request)
    except grpc.RpcError as e:
        code = e.code() if hasattr(e, "code") else None
        details = e.details() if hasattr(e, "details") else str(e)
        sys.exit(f"[pattern_leg] {what}: MatchPattern failed with {code}: {details} -- aborting; a failed "
                 "call is never read as 'no runs'")
    if len(rows) == request.limit:
        sys.exit(f"[pattern_leg] {what}: {len(rows)} rows == limit {request.limit}: the result may be "
                 "truncated -- aborting")
    return rows


def field(row, name, what):
    if name not in row.data:
        sys.exit(f"[pattern_leg] {what}: a result row has no {name!r} column (columns: {sorted(row.data)})")
    return row.data[name]


def whole_number(value, name, what, minimum):
    if not isinstance(value, float) or not value.is_integer() or value < minimum:
        sys.exit(f"[pattern_leg] {what}: {name} {value!r} is not a whole number >= {minimum}")
    return int(value)


def finite_number(value, name, what):
    if value is None:
        sys.exit(f"[pattern_leg] {what}: {name} is NULL -- the chunk's similarity could not be computed")
    if not isinstance(value, float) or not math.isfinite(value):
        sys.exit(f"[pattern_leg] {what}: {name} {value!r} is not a finite number")
    return value


def rows_by_candidate(rows, guid_to_doc, candidates, expected_guid, what):
    """{doc_id: [row, ...]} for the query's candidates. In the per-candidate shape every row must be
    `expected_guid`'s; in the whole-corpus shape (expected_guid None) every parent must be a known
    corpus document, and non-candidates are dropped."""
    grouped = {}
    for row in rows:
        parent = field(row, "parent_key", what)
        if expected_guid is not None and parent != expected_guid:
            sys.exit(f"[pattern_leg] {what}: a row for parent {parent!r} came back from a call filtered to "
                     f"{expected_guid!r}")
        doc_id = guid_to_doc.get(parent)
        if doc_id is None:
            sys.exit(f"[pattern_leg] {what}: parent {parent!r} is not in the key map -- wrong collection?")
        if doc_id in candidates:
            grouped.setdefault(doc_id, []).append(row)
    return grouped


def calibration_chunks(rows, what, index_column="ci"):
    """[(chunk_index, s), ...] for one candidate, in chunk order, from the calibration call's `ci` and
    `s` measures (probe A's ALL ROWS call passes index_column="chunk_index"); NULL s and duplicate
    chunks abort."""
    chunks = {}
    for row in rows:
        index = whole_number(field(row, index_column, what), index_column, what, 0)
        if index in chunks:
            sys.exit(f"[pattern_leg] {what}: chunk {index} appears twice")
        chunks[index] = finite_number(field(row, "s", what), "s", what)
    return sorted(chunks.items())


def theta_runs(rows, what):
    """[(run_len, run_sum), ...] for one candidate; a NULL run_sum aborts."""
    return [(whole_number(field(row, "run_len", what), "run_len", what, 1),
             finite_number(field(row, "run_sum", what), "run_sum", what)) for row in rows]


def query_calls(shape, candidates, doc_to_guid):
    """[guid, ...] -- one call per candidate -- or [None] for the one whole-corpus call."""
    if shape == "whole-corpus":
        return [None]
    return [doc_to_guid[doc] for doc in candidates]


def run_pass(coordinator, shape, build_request, per_candidate, baseline, baseline_order, maps, progress="pass"):
    """Every query's calls in `shape`; returns {query_id: {doc_id: parsed}} where parsed is
    `per_candidate(rows, what)` over that candidate's rows (candidates with no rows are absent)."""
    doc_to_guid, guid_to_doc = maps
    result = {}
    for n, query_id in enumerate(baseline_order, start=1):
        candidates = set(baseline[query_id])
        grouped = {}
        for guid in query_calls(shape, baseline[query_id], doc_to_guid):
            what = f"{progress} query {query_id}" + (f" parent {guid}" if guid else "")
            rows = execute(coordinator, build_request(query_id, guid), what)
            for doc_id, doc_rows in rows_by_candidate(rows, guid_to_doc, candidates, guid, what).items():
                grouped.setdefault(doc_id, []).extend(doc_rows)
        result[query_id] = {doc: per_candidate(doc_rows, f"{progress} query {query_id} doc {doc}")
                            for doc, doc_rows in grouped.items()}
        if n % 50 == 0 or n == len(baseline_order):
            print(f"[pattern_leg] {progress}: {n}/{len(baseline_order)} queries", flush=True)
    return result


# ── Scoring (pure) ──────────────────────────────────────────────────────────────────────

def best_run(runs, primary):
    """(primary value, other value) of the candidate's best run under `primary`: the highest primary,
    ties broken by the higher other score."""
    index = SCORES.index(primary)          # runs are (run_len, run_sum) tuples, in SCORES order
    return max((run[index], run[1 - index]) for run in runs)


def pattern_order(runs_by_doc, primary, baseline_docs):
    """Matched candidates in pattern-rank order: best primary descending, then the best run's other
    score descending, then baseline rank ascending (unique, so the order is strict)."""
    baseline_rank = {doc: i for i, doc in enumerate(baseline_docs, start=1)}
    keyed = []
    for doc, runs in runs_by_doc.items():
        value, other = best_run(runs, primary)
        keyed.append((-value, -other, baseline_rank[doc], doc))
    return [doc for *_, doc in sorted(keyed)]


def ordered_query_ids(query_ids):
    """Numeric order when every id is all digits (FreshStack's are), string order otherwise."""
    ids = list(query_ids)
    if all(q.isdigit() for q in ids):
        return sorted(ids, key=int)
    return sorted(ids)


def sample_query_ids(query_ids, n=THETA_SAMPLE_QUERIES):
    ordered = ordered_query_ids(query_ids)
    if len(ordered) < n:
        sys.exit(f"theta needs the {n} lowest query ids, but only {len(ordered)} queries exist")
    return ordered[:n]


def percentiles(values):
    """p50/p75/p90 with linear interpolation between order statistics (statistics.quantiles'
    "inclusive" method, the same as numpy.percentile's default)."""
    if len(values) < 2:
        sys.exit(f"theta needs at least 2 similarity values, got {len(values)}")
    cuts = statistics.quantiles(values, n=100, method="inclusive")
    return {"p50": cuts[49], "p75": cuts[74], "p90": cuts[89]}


def theta_from_calibration(calibration, query_ids):
    sample = sample_query_ids(query_ids)
    values = [s for q in sample for chunks in calibration[q].values() for _, s in chunks]
    return {**percentiles(values), "sample_query_ids": sample, "rows_in_sample": len(values)}


def projection_hours(mean_seconds):
    return mean_seconds * PROJECTION_QUERIES * PROJECTION_CANDIDATES / 3600.0


def probe_decision(per_candidate_means, whole_corpus_calls):
    """The spec's rule, fixed by user decision. `per_candidate_means` is {kind: mean seconds per call};
    `whole_corpus_calls` is [(seconds, timed_out), ...]. Every per-candidate pass, calibration as
    well as theta, is 672 x 50 calls, so both must fit the budget (the conservative reading)."""
    worst_hours = max(projection_hours(m) for m in per_candidate_means.values())
    if worst_hours <= PER_CANDIDATE_BUDGET_HOURS:
        return "per-candidate"
    if all(not timed_out and seconds < WHOLE_CORPUS_TIMEOUT_SECONDS for seconds, timed_out in whole_corpus_calls):
        return "whole-corpus"
    return "STOP"


# ── Build identity ──────────────────────────────────────────────────────────────────────

def fetch_build(url):
    """GET /build (anonymous, listener 8081) -> composite; any failure aborts, like benchmark-query."""
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            body = json.load(response)
    except Exception as e:  # noqa: BLE001 -- every failure mode means "cannot attribute this pass"
        sys.exit(f"[pattern_leg] could not GET {url}: {e}")
    composite = body.get("composite")
    if not composite:
        sys.exit(f"[pattern_leg] {url} returned no composite")
    return composite


def check_build(fetch, url, expected, label):
    composite = fetch(url)
    if composite != expected:
        sys.exit(f"[pattern_leg] BUILD MISMATCH at {label}: {url} reports {composite!r}, the baseline sidecar "
                 f"{expected!r} -- the pattern leg must run on the baseline's build")
    return composite


def run_checked_pass(fetch, url, expected, pass_name, body):
    """/build before, the pass, /build after; returns (result, check record)."""
    before = check_build(fetch, url, expected, f"{pass_name} (before)")
    result = body()
    after = check_build(fetch, url, expected, f"{pass_name} (after)")
    return result, {"pass": pass_name, "before": before, "after": after}


# ── Inputs ──────────────────────────────────────────────────────────────────────────────

def load_baseline(path):
    """{query_id: [doc_id, ...]} in rank order and the file's query order (rank must equal position)."""
    baseline, order = {}, []
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            if not line.strip():
                continue
            fields = line.split()
            if len(fields) != 6:
                sys.exit(f"{path}:{lineno}: expected 6 fields, got {len(fields)}")
            query_id, _iter, doc_id, rank, _score, _tag = fields
            if query_id not in baseline:
                baseline[query_id] = []
                order.append(query_id)
            docs = baseline[query_id]
            if doc_id in docs or rank != str(len(docs) + 1):
                sys.exit(f"{path}:{lineno}: duplicate document or rank {rank} out of order in query {query_id}")
            docs.append(doc_id)
    if not baseline:
        sys.exit(f"{path}: no rows")
    return baseline, order


def load_keymap(path, baseline):
    """(doc_to_guid, guid_to_doc); the map must be one-to-one and cover every candidate."""
    with open(path, encoding="utf-8") as f:
        guid_to_doc = json.load(f)
    doc_to_guid = {doc: guid for guid, doc in guid_to_doc.items()}
    if len(doc_to_guid) != len(guid_to_doc):
        sys.exit(f"{path}: two GUIDs map to one doc id")
    missing = sorted({d for docs in baseline.values() for d in docs} - set(doc_to_guid))
    if missing:
        sys.exit(f"{path}: {len(missing)} baseline candidate(s) have no GUID, e.g. {missing[:3]}")
    return doc_to_guid, guid_to_doc


def load_queries(path, query_ids):
    texts = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.strip():
                row = json.loads(line)
                texts[row["_id"]] = row.get("text") or ""
    missing = [q for q in query_ids if not texts.get(q)]
    if missing:
        sys.exit(f"{path}: {len(missing)} baseline query id(s) have no text, e.g. {missing[:3]}")
    return texts


# ── Writers (pure over their inputs) ────────────────────────────────────────────────────

def write_calibration(path, calibration, query_order, baseline):
    with open(path, "w", encoding="utf-8") as f:
        f.write("query_id\tdoc_id\tchunk_index\ts\n")
        for query_id in query_order:
            for doc_id in baseline[query_id]:
                for index, s in calibration[query_id][doc_id]:
                    f.write(f"{query_id}\t{doc_id}\t{index}\t{s!r}\n")


def write_scores(path, runs, query_order, baseline):
    """One row per matched candidate. `run_len` is the arm-run_len primary score (the candidate's
    longest run) and `run_sum` the arm-run_sum primary (its highest run_sum): the best run under
    each score, which may be two different runs. The last two columns are each best run's other
    score, the tie-break pattern_order applied."""
    with open(path, "w", encoding="utf-8") as f:
        f.write("query_id\tdoc_id\trun_len\trun_sum\trun_sum_of_best_len\trun_len_of_best_sum\n")
        for query_id in query_order:
            for doc_id in baseline[query_id]:
                if doc_id in runs.get(query_id, {}):
                    best_len, sum_of_len = best_run(runs[query_id][doc_id], "run_len")
                    best_sum, len_of_sum = best_run(runs[query_id][doc_id], "run_sum")
                    f.write(f"{query_id}\t{doc_id}\t{best_len}\t{best_sum!r}\t{sum_of_len!r}\t{len_of_sum}\n")


def write_pattern_trec(path, runs, query_order, baseline, primary, tag):
    """Matched candidates only, in pattern-rank order; rank from 1, file order == rank order. The
    score column is (matched + 1 - rank), strictly decreasing, so the file scores in its own order
    even under a scorer that breaks score ties by doc id."""
    with open(path, "w", encoding="utf-8") as f:
        for query_id in query_order:
            ordered = pattern_order(runs.get(query_id, {}), primary, baseline[query_id])
            for rank, doc_id in enumerate(ordered, start=1):
                f.write(f"{query_id} Q0 {doc_id} {rank} {len(ordered) + 1 - rank:.6f} {tag}\n")


def write_json(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
        f.write("\n")


# ── Wiring (live) ───────────────────────────────────────────────────────────────────────

def require_env(name):
    value = os.environ.get(name)
    if not value:
        sys.exit(f"[pattern_leg] {name} is not set (source /home/ben/iverson-benchmark-data/bench-env.sh)")
    return value


def connect(session):
    """(channel, coordinator) for BenchmarkDocument from the IVERSON_GRPC_URL env var, carrying both
    identities via `session`'s tokens (module docstring "Identity"). Unlike the removed
    IversonClient/IversonClientCredentials path, this never mints a token itself with an
    unconfigurable Host header -- session.service_token()/acting_user_token() do that (see
    mint_service_token, TokenSession)."""
    url = urlsplit(os.environ.get("IVERSON_GRPC_URL", "http://localhost:8080"))
    if url.scheme not in ("http", "https"):
        sys.exit(f"IVERSON_GRPC_URL must start with http:// or https://, got {url.geturl()!r}")
    host = url.hostname or "localhost"
    port = url.port or (443 if url.scheme == "https" else 8080)
    channel = build_channel(host, port, url.scheme == "https", session)
    return channel, EntityCoordinator(BenchmarkDocument, channel)


def mint_acting_user_token(username):
    """stdout of mint_acting_user_token.py (everything else it prints goes to stderr)."""
    password = require_env(BYPASS_PASSWORD_ENV)
    result = subprocess.run(
        [sys.executable, MINT_SCRIPT, "--target", "compose", "--username", username, "--password", password],
        capture_output=True, text=True)
    token = result.stdout.strip()
    if result.returncode != 0 or not token:
        sys.exit(f"[pattern_leg] minting the acting-user token failed (exit {result.returncode}):\n"
                 f"{result.stderr[-2000:]}")
    print(f"[pattern_leg] minted an acting-user token for {username}", flush=True)
    return token


def mint_service_token_from_env():
    """mint_service_token() wired to the same env vars connect() used to read via
    IversonClientCredentials, plus the Host header override (module docstring "Identity")."""
    return mint_service_token(
        require_env("IVERSON_CLIENT_ID"), require_env("IVERSON_CLIENT_SECRET"),
        require_env("IVERSON_TOKEN_ENDPOINT"), os.environ.get("IVERSON_CLIENT_SCOPE"),
        os.environ.get(ACTING_USER_HOST_HEADER_ENV, DEFAULT_ACTING_USER_HOST_HEADER))


class Context:
    """Everything a subcommand needs, loaded once from the CLI arguments."""

    def __init__(self, args):
        self.args = args
        self.pattern_dir = os.path.join(args.out, "pattern")
        os.makedirs(self.pattern_dir, exist_ok=True)
        self.baseline, self.order = load_baseline(args.baseline)
        self.maps = load_keymap(args.keymap, self.baseline)
        self.texts = load_queries(args.queries, self.order)
        self.expected = report.load_build_composite(args.baseline)
        if not self.expected:
            sys.exit(f"{report.sidecar_path_for(args.baseline)}: missing, or has no composite")
        self.session = TokenSession(lambda: mint_acting_user_token(args.username), mint_service_token_from_env)
        self.channel, self.coordinator = connect(self.session)

    def path(self, name):
        return os.path.join(self.pattern_dir, name)


def calibration_pass(ctx, shape, order):
    limit = shape_limit(shape)
    return run_pass(ctx.coordinator, shape, lambda q, guid: calibration_request(ctx.texts[q], guid, limit),
                    calibration_chunks, ctx.baseline, order, ctx.maps, progress="calibration")


def theta_pass(ctx, shape, theta, order, label):
    limit = shape_limit(shape)
    return run_pass(ctx.coordinator, shape, lambda q, guid: theta_request(ctx.texts[q], theta, guid, limit),
                    theta_runs, ctx.baseline, order, ctx.maps, progress=label)


def cmd_probe_a(ctx):
    query_id = ctx.args.query_id or ordered_query_ids(ctx.order)[0]
    doc_id = ctx.args.doc_id or ctx.baseline[query_id][0]
    guid = ctx.maps[0][doc_id]
    rows = execute(ctx.coordinator, probe_a_request(ctx.texts[query_id], guid, PER_CANDIDATE_LIMIT),
                   f"probe A query {query_id} doc {doc_id}")
    calibration_rows = execute(ctx.coordinator, calibration_request(ctx.texts[query_id], guid, PER_CANDIDATE_LIMIT),
                               f"probe A calibration query {query_id} doc {doc_id}")
    for name, got in (("ALL ROWS running-sum call", rows), ("text-free calibration call", calibration_rows)):
        print(f"[probe-a] query {query_id}, candidate {doc_id} ({guid}), {name}: {len(got)} row(s)")
        for row in got:
            shown = {k: (v[:60] + "..." if isinstance(v, str) and len(v) > 60 else v) for k, v in row.data.items()}
            print(f"  match {row.match_number} classifier {row.classifier!r} {shown}")
    if not rows:
        sys.exit("[probe-a] FAIL: no rows -- the candidate's chunks are not readable")
    chunks = calibration_chunks(rows, "probe A", index_column="chunk_index")
    calibrated = calibration_chunks(calibration_rows, "probe A calibration")
    if calibrated != chunks:
        sys.exit(f"[probe-a] FAIL: the calibration call's (ci, s) {calibrated} differ from the ALL ROWS call's "
                 f"(chunk_index, s) {chunks}")
    if any("text" in row.data for row in calibration_rows):
        sys.exit("[probe-a] FAIL: the calibration call returned a text column")
    running = 0.0
    for (index, s), row in zip(chunks, sorted(rows, key=lambda r: r.data["chunk_index"])):
        running += s
        got = finite_number(row.data.get("run_sum"), "run_sum", "probe A")
        if not math.isclose(got, running, rel_tol=1e-9, abs_tol=1e-12):
            sys.exit(f"[probe-a] FAIL: chunk {index}: RUNNING SUM(SIMILARITY) {got!r} != cumulative s {running!r}")
    print(f"[probe-a] PASS: {len(chunks)} chunk(s), every s non-NULL, SUM(SIMILARITY(A.text, q)) is the running "
          "sum of s, and the text-free calibration call gives the same (chunk index, s) per chunk")


def cmd_probe_b(ctx):
    queries = ordered_query_ids(ctx.order)[:PROBE_QUERIES]
    theta = ctx.args.theta
    composite = fetch_build(ctx.args.build_url)
    timings = {}
    for shape in SHAPES:
        limit = shape_limit(shape)
        for kind in ("calibration", "theta"):
            calls = []
            for query_id in queries:
                for guid in query_calls(shape, ctx.baseline[query_id], ctx.maps[0]):
                    request = (calibration_request(ctx.texts[query_id], guid, limit) if kind == "calibration"
                               else theta_request(ctx.texts[query_id], theta, guid, limit))
                    what = f"probe B {shape} {kind} query {query_id}"
                    start = time.perf_counter()
                    timed_out = False
                    try:
                        rows = ctx.coordinator.match_pattern(request)
                    except grpc.RpcError as e:
                        if shape == "whole-corpus" and e.code() == grpc.StatusCode.DEADLINE_EXCEEDED:
                            timed_out = True
                            rows = []
                        else:
                            sys.exit(f"[probe-b] {what}: {e.code()}: {e.details()}")
                    seconds = time.perf_counter() - start
                    if len(rows) == limit:
                        sys.exit(f"[probe-b] {what}: {len(rows)} rows == limit {limit}: possible truncation")
                    calls.append((seconds, timed_out))
            timings[(shape, kind)] = calls
            mean = statistics.fmean(s for s, _ in calls)
            print(f"[probe-b] {shape:13s} {kind:11s} {len(calls):4d} calls  mean {mean:.3f}s  "
                  f"max {max(s for s, _ in calls):.3f}s  timeouts {sum(t for _, t in calls)}", flush=True)
    if fetch_build(ctx.args.build_url) != composite:
        sys.exit("[probe-b] the /build composite changed during the probe")
    means = {kind: statistics.fmean(s for s, _ in timings[("per-candidate", kind)]) for kind in ("calibration", "theta")}
    whole = timings[("whole-corpus", "calibration")] + timings[("whole-corpus", "theta")]
    decision = probe_decision(means, whole)
    result = {
        "query_ids": queries, "placeholder_theta": theta, "composite": composite,
        "limits": {"per-candidate": PER_CANDIDATE_LIMIT, "whole-corpus": WHOLE_CORPUS_LIMIT},
        "calls": {f"{shape}/{kind}": {"n": len(c), "mean_s": statistics.fmean(s for s, _ in c),
                                       "max_s": max(s for s, _ in c), "timeouts": sum(t for _, t in c)}
                  for (shape, kind), c in timings.items()},
        "per_candidate_projection_hours": {k: projection_hours(m) for k, m in means.items()},
        "decision": decision,
    }
    write_json(ctx.path("probe-b.json"), result)
    print(f"[probe-b] projected per-candidate pass: calibration "
          f"{result['per_candidate_projection_hours']['calibration']:.2f} h, theta "
          f"{result['per_candidate_projection_hours']['theta']:.2f} h; decision: {decision}")
    if decision == "STOP":
        sys.exit("[probe-b] STOP: per-candidate exceeds 8 h per pass and a whole-corpus call hit the 30 s "
                 "timeout -- return to the user")


def cmd_calibrate(ctx):
    calibration, check = run_checked_pass(
        fetch_build, ctx.args.build_url, ctx.expected, "calibration",
        lambda: calibration_pass(ctx, ctx.args.shape, ctx.order))
    for query_id in ctx.order:
        absent = [d for d in ctx.baseline[query_id] if d not in calibration[query_id]]
        if absent:
            sys.exit(f"[pattern_leg] calibration: query {query_id}: {len(absent)} candidate(s) returned no "
                     f"chunk, e.g. {absent[:3]}")
    theta = theta_from_calibration(calibration, ctx.order)
    write_calibration(ctx.path("calibration.tsv"), calibration, ctx.order, ctx.baseline)
    write_json(ctx.path("theta.json"), theta)
    write_json(ctx.path("build.json"), {"baseline_composite": ctx.expected, "checks": [check]})
    print(f"[pattern_leg] theta p50 {theta['p50']!r}  p75 {theta['p75']!r}  p90 {theta['p90']!r} "
          f"over {theta['rows_in_sample']} chunks of the {THETA_SAMPLE_QUERIES} lowest query ids")


def cmd_run(ctx):
    label = ctx.args.theta
    with open(ctx.path("theta.json"), encoding="utf-8") as f:
        theta = json.load(f)[label]
    with open(ctx.path("build.json"), encoding="utf-8") as f:
        build = json.load(f)
    if build.get("baseline_composite") != ctx.expected:
        sys.exit(f"build.json baseline_composite {build.get('baseline_composite')!r} != {ctx.expected!r}")
    runs, check = run_checked_pass(fetch_build, ctx.args.build_url, ctx.expected, label,
                                   lambda: theta_pass(ctx, ctx.args.shape, theta, ctx.order, label))
    matched = sum(len(docs) for docs in runs.values())
    if matched == 0:
        sys.exit(f"[pattern_leg] {label}: no candidate of any query has a run above theta {theta!r} -- this is what "
                 "missing vectors look like (NULL > theta never matches); aborting")
    write_scores(ctx.path(f"scores-{label}.tsv"), runs, ctx.order, ctx.baseline)
    for score in SCORES:
        write_pattern_trec(ctx.path(f"mp-{label}-{score}.trec"), runs, ctx.order, ctx.baseline, score,
                           f"mp-{label}-{score}")
    build["checks"].append(check)
    write_json(ctx.path("build.json"), build)
    print(f"[pattern_leg] {label} (theta {theta!r}): {matched} matched candidate(s) over {len(runs)} queries")


def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("probe-a", "probe-b", "calibrate", "run"):
        p = sub.add_parser(name)
        p.add_argument("--out", required=True, help="the benchmark's OUT directory; files go to OUT/pattern/")
        p.add_argument("--baseline", required=True, help="<label>.chunks.trec whose candidates are scored")
        p.add_argument("--keymap", default=DEFAULT_KEYMAP, help="Iverson GUID -> BEIR doc id")
        p.add_argument("--queries", default=DEFAULT_QUERIES, help="BEIR queries.jsonl (the text benchmark-query sends)")
        p.add_argument("--build-url", default=os.environ.get("IVERSON_HTTP_URL", "http://localhost:8081") + "/build")
        p.add_argument("--username", default=BYPASS_USERNAME, help="the acting user to mint a token for")
        if name == "probe-a":
            p.add_argument("--query-id", default=None, help="default: the lowest query id")
            p.add_argument("--doc-id", default=None, help="default: that query's rank-1 candidate")
        if name == "probe-b":
            p.add_argument("--theta", type=float, default=PROBE_PLACEHOLDER_THETA, help="placeholder theta")
        if name in ("calibrate", "run"):
            p.add_argument("--shape", required=True, choices=SHAPES, help="the shape probe-b decided")
        if name == "run":
            p.add_argument("--theta", required=True, choices=THETAS)
    return ap


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    ctx = Context(args)
    try:
        {"probe-a": cmd_probe_a, "probe-b": cmd_probe_b, "calibrate": cmd_calibrate, "run": cmd_run}[args.cmd](ctx)
    finally:
        ctx.channel.close()


if __name__ == "__main__":
    main()

"""pytest suite for pattern_leg.py (spec docs/specs/2026-09-28-matchpattern-rrf-test-design.md, Phase 0
and Phase 2). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py -q

No network: the coordinator, the token minter, the clock and the /build fetch are fakes. The request
builders run through the real SDK (Iverson.Clients/Python, put on sys.path by pattern_leg itself), so
the field assertions below pin the exact protos the live run sends. grpcio and protobuf come from the
user site-packages; nothing needs PYTHONPATH (setting it to python-libs is harmless)."""
import json
import os
import sys
import urllib.parse

import grpc
import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pattern_leg as pl  # noqa: E402
from iverson_client import MatchPatternResult  # noqa: E402  (on sys.path via pattern_leg)

pb = pl.pb


# ── fakes ───────────────────────────────────────────────────────────────────────────────

class FakeRpcError(grpc.RpcError):
    def __init__(self, code=grpc.StatusCode.DEADLINE_EXCEEDED, details="MatchPattern exceeded its time limit."):
        self._code, self._details = code, details

    def code(self):
        return self._code

    def details(self):
        return self._details


class FakeCoordinator:
    """Records every request; `respond(request)` returns rows or raises. Unlike before the fix, the
    coordinator no longer carries an acting-user token of its own -- both the service and the
    acting-user identity now ride the gRPC channel's metadata-call-credentials plugins (see
    TokenSession, _ServiceBearerAuthPlugin, _ActingUserAuthPlugin), which these offline tests cover
    separately since there is no real channel here."""

    def __init__(self, respond, log=None):
        self.respond = respond
        self.log = log if log is not None else []

    def match_pattern(self, request):
        self.log.append(request)
        return self.respond(request)


def coordinator_for(respond):
    coordinator = FakeCoordinator(respond)
    return coordinator, coordinator.log


def row(**data):
    return MatchPatternResult(data=data, match_number=1, classifier="A")


class FakeTokenResponse:
    """A fake `urlopen(...)` return value: a context manager whose `.status` and `.read()` mirror
    `http.client.HTTPResponse` closely enough for `mint_service_token` (json.load only needs
    `.read()`)."""

    def __init__(self, status, payload):
        self.status = status
        self._body = json.dumps(payload).encode("utf-8")

    def read(self, *args, **kwargs):
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc_info):
        return False


def capture_metadata(plugin):
    """Invoke a grpc.AuthMetadataPlugin's __call__ the way grpcio does, and return the (metadata,
    error) it hands its callback."""
    captured = []
    plugin(None, lambda metadata, error: captured.append((metadata, error)))
    assert len(captured) == 1
    return captured[0]


# ── escaping and expressions ────────────────────────────────────────────────────────────

def test_escape_literal_doubles_every_single_quote():
    assert pl.escape_literal("it's") == "it''s"
    assert pl.escape_literal("''") == "''''"
    assert pl.escape_literal('no "quotes" here') == 'no "quotes" here'


def test_similarity_wraps_the_escaped_query_in_a_string_literal():
    assert pl.similarity("A.text", "Bob's `x`") == "SIMILARITY(A.text, 'Bob''s `x`')"


def test_format_theta_round_trips_and_refuses_non_finite():
    assert float(pl.format_theta(0.1 + 0.2)) == 0.1 + 0.2
    with pytest.raises(SystemExit):
        pl.format_theta(float("nan"))


# ── request fields: both shapes x both call kinds ───────────────────────────────────────

GUID = "7c5a8e9b-82ef-5c17-91d6-7e55ac356209"
QUERY = "why won't it's work?"
ESCAPED = "why won''t it''s work?"


def common_fields(request, pattern="A+"):
    assert request.type_name == "BenchmarkDocument"
    assert request.source == pb.CHUNKS
    assert request.chunk_property == "Body"
    assert list(request.partition_by) == [] and list(request.order_by) == []
    assert request.pattern == pattern
    assert not request.HasField("after_match")        # server default: SKIP PAST LAST ROW


def assert_per_candidate_where(request):
    assert len(request.where) == 1
    clause = request.where[0]
    assert (clause.property, clause.operator, clause.clause_type) == ("Id", pb.EQUALS, pb.FILTER)
    assert clause.value.WhichOneof("kind") == "string_val" and clause.value.string_val == GUID


@pytest.mark.parametrize("shape, guid", [("per-candidate", GUID), ("whole-corpus", None)])
def test_calibration_request_fields(shape, guid):
    """The text-free calibration call: pattern A (one single-row match per chunk), ONE ROW, s and ci."""
    request = pl.calibration_request(QUERY, guid, pl.shape_limit(shape))
    common_fields(request, pattern="A")
    if guid:
        assert_per_candidate_where(request)
        assert request.limit == 10_000
    else:
        assert len(request.where) == 0
        assert request.limit == 18_623
    assert [(d.name, d.expr) for d in request.define] == [("A", "TRUE")]
    assert [(m.name, m.expr) for m in request.measures] == [
        ("s", f"SIMILARITY(text, '{ESCAPED}')"),
        ("ci", "chunk_index"),
    ]
    assert request.rows_per_match == pb.ONE_ROW


@pytest.mark.parametrize("shape, guid", [("per-candidate", GUID), ("whole-corpus", None)])
def test_theta_request_fields(shape, guid):
    request = pl.theta_request(QUERY, 0.61, guid, pl.shape_limit(shape))
    common_fields(request)
    if guid:
        assert_per_candidate_where(request)
        assert request.limit == 10_000
    else:
        assert len(request.where) == 0
        assert request.limit == 18_623
    assert [(d.name, d.expr) for d in request.define] == [("A", f"SIMILARITY(text, '{ESCAPED}') > 0.61")]
    assert [(m.name, m.expr) for m in request.measures] == [
        ("run_len", "COUNT(*)"),
        ("run_sum", f"SUM(SIMILARITY(A.text, '{ESCAPED}'))"),
    ]
    assert request.rows_per_match == pb.ONE_ROW


def test_probe_a_request_is_the_row_carrying_running_sum_call():
    request = pl.probe_a_request(QUERY, GUID, pl.PER_CANDIDATE_LIMIT)
    common_fields(request)
    assert_per_candidate_where(request)
    assert [(d.name, d.expr) for d in request.define] == [("A", "TRUE")]
    assert [(m.name, m.expr) for m in request.measures] == [
        ("s", f"SIMILARITY(text, '{ESCAPED}')"),
        ("run_sum", f"SUM(SIMILARITY(A.text, '{ESCAPED}'))"),
    ]
    assert request.rows_per_match == pb.ALL_ROWS_SHOW_EMPTY


# ── best-run selection and every tie-break ──────────────────────────────────────────────

def test_best_run_takes_the_highest_primary():
    runs = [(2, 1.9), (3, 1.2), (1, 0.8)]
    assert pl.best_run(runs, "run_len") == (3, 1.2)
    assert pl.best_run(runs, "run_sum") == (1.9, 2)


def test_best_run_breaks_a_primary_tie_by_the_other_score():
    assert pl.best_run([(3, 1.2), (3, 1.5), (2, 1.9)], "run_len") == (3, 1.5)
    assert pl.best_run([(2, 1.5), (4, 1.5)], "run_sum") == (1.5, 4)


def test_pattern_order_sorts_by_primary_descending():
    runs = {"a": [(1, 0.9)], "b": [(3, 0.7)], "c": [(2, 0.8)]}
    assert pl.pattern_order(runs, "run_len", ["a", "b", "c"]) == ["b", "c", "a"]
    assert pl.pattern_order(runs, "run_sum", ["a", "b", "c"]) == ["a", "c", "b"]


def test_pattern_order_breaks_a_primary_tie_by_the_other_score():
    runs = {"a": [(2, 1.1)], "b": [(2, 1.4)]}
    assert pl.pattern_order(runs, "run_len", ["a", "b"]) == ["b", "a"]
    runs = {"a": [(1, 0.9)], "b": [(2, 0.9)]}
    assert pl.pattern_order(runs, "run_sum", ["a", "b"]) == ["b", "a"]


def test_pattern_order_breaks_a_full_tie_by_baseline_rank():
    runs = {"a": [(2, 1.4)], "b": [(2, 1.4)], "c": [(2, 1.4)]}
    assert pl.pattern_order(runs, "run_len", ["c", "a", "b"]) == ["c", "a", "b"]
    assert pl.pattern_order(runs, "run_sum", ["b", "c", "a"]) == ["b", "c", "a"]


def test_pattern_order_uses_the_other_score_of_the_best_run_not_the_best_other_score():
    # a's longest run has run_sum 1.0; its best run_sum (2.0) belongs to a shorter run.
    runs = {"a": [(3, 1.0), (1, 2.0)], "b": [(3, 1.5)]}
    assert pl.pattern_order(runs, "run_len", ["a", "b"]) == ["b", "a"]


# ── calls: count at limit, gRPC errors, NULLs ───────────────────────────────────────────

def test_execute_aborts_when_the_count_reaches_limit():
    request = pl.calibration_request(QUERY, GUID, 3)
    coordinator, _ = coordinator_for(lambda r: [row(parent_key=GUID, ci=float(i), s=0.5) for i in range(3)])
    with pytest.raises(SystemExit, match="truncated"):
        pl.execute(coordinator, request, "t")


def test_execute_accepts_a_count_one_below_limit():
    request = pl.calibration_request(QUERY, GUID, 3)
    coordinator, _ = coordinator_for(lambda r: [row(parent_key=GUID, ci=float(i), s=0.5) for i in range(2)])
    assert len(pl.execute(coordinator, request, "t")) == 2


@pytest.mark.parametrize("code", [grpc.StatusCode.DEADLINE_EXCEEDED, grpc.StatusCode.INVALID_ARGUMENT,
                                  grpc.StatusCode.UNAVAILABLE])
def test_a_grpc_error_aborts(code):
    def raise_(request):
        raise FakeRpcError(code, "boom")
    coordinator, _ = coordinator_for(raise_)
    with pytest.raises(SystemExit, match=str(code)):
        pl.execute(coordinator, pl.theta_request(QUERY, 0.5, GUID, 10), "t")


def test_a_null_similarity_aborts_calibration():
    with pytest.raises(SystemExit, match="NULL"):
        pl.calibration_chunks([row(parent_key=GUID, ci=0.0, s=None)], "t")


def test_a_null_run_sum_aborts_a_theta_pass():
    with pytest.raises(SystemExit, match="NULL"):
        pl.theta_runs([row(parent_key=GUID, run_len=2.0, run_sum=None)], "t")


def test_calibration_chunks_reads_ci_and_s_in_chunk_order():
    """The engine emits ci as a long and the server a proto number, so ci arrives as a float."""
    rows = [row(parent_key=GUID, s=0.9, ci=2.0), row(parent_key=GUID, s=0.3, ci=0.0),
            row(parent_key=GUID, s=0.6, ci=1.0)]
    assert pl.calibration_chunks(rows, "t") == [(0, 0.3), (1, 0.6), (2, 0.9)]


def test_calibration_chunks_refuses_a_row_without_ci():
    """A row carrying chunk_index but no ci is the old ALL ROWS shape, not the calibration call's."""
    with pytest.raises(SystemExit, match="'ci'"):
        pl.calibration_chunks([row(parent_key=GUID, chunk_index=0.0, s=0.1)], "t")


def test_probe_a_parses_its_all_rows_call_by_chunk_index():
    rows = [row(parent_key=GUID, chunk_index=1.0, text="b", s=0.4), row(parent_key=GUID, chunk_index=0.0, text="a", s=0.2)]
    assert pl.calibration_chunks(rows, "t", index_column="chunk_index") == [(0, 0.2), (1, 0.4)]


def test_calibration_chunks_refuses_a_duplicate_or_fractional_chunk_index():
    with pytest.raises(SystemExit):
        pl.calibration_chunks([row(ci=0.0, s=0.1), row(ci=0.0, s=0.2)], "t")
    with pytest.raises(SystemExit):
        pl.calibration_chunks([row(ci=0.5, s=0.1)], "t")


def respond_calibration(request):
    """One text-free row per chunk, as the engine probe showed: g1 has chunks 0..2, g3 one chunk,
    g4 is a non-candidate only the whole corpus returns."""
    chunks = {"g1": [(0, 0.1), (1, 0.7), (2, 0.4)], "g2": [(0, 0.5)], "g3": [(0, 0.2)], "g4": [(0, 0.9)]}
    rows = {g: [row(parent_key=g, s=s, ci=float(i)) for i, s in cs] for g, cs in chunks.items()}
    if request.where:
        return rows[request.where[0].value.string_val]
    return [r for rs in rows.values() for r in rs]


@pytest.mark.parametrize("shape, calls", [("per-candidate", 3), ("whole-corpus", 2)])
def test_a_calibration_pass_gives_the_same_chunks_in_both_shapes(shape, calls):
    coordinator, log = coordinator_for(respond_calibration)
    result = pl.run_pass(coordinator, shape, lambda q, guid: pl.calibration_request("q", guid, pl.shape_limit(shape)),
                         pl.calibration_chunks, BASELINE, ["q1", "q2"], MAPS)
    assert result == {"q1": {"d1": [(0, 0.1), (1, 0.7), (2, 0.4)], "d2": [(0, 0.5)]}, "q2": {"d3": [(0, 0.2)]}}
    assert len(log) == calls
    assert all(r.pattern == "A" and r.rows_per_match == pb.ONE_ROW for r in log)


def test_rows_by_candidate_refuses_a_foreign_parent_in_the_per_candidate_shape():
    with pytest.raises(SystemExit):
        pl.rows_by_candidate([row(parent_key="other")], {"other": "d2", GUID: "d1"}, {"d1"}, GUID, "t")


def test_rows_by_candidate_refuses_an_unknown_parent_and_drops_non_candidates_whole_corpus():
    keymap = {"g1": "d1", "g2": "d2"}
    grouped = pl.rows_by_candidate([row(parent_key="g1"), row(parent_key="g2")], keymap, {"d1"}, None, "t")
    assert list(grouped) == ["d1"]
    with pytest.raises(SystemExit):
        pl.rows_by_candidate([row(parent_key="g9")], keymap, {"d1"}, None, "t")


# ── a whole pass through the fake coordinator ───────────────────────────────────────────

BASELINE = {"q1": ["d1", "d2"], "q2": ["d3"]}
MAPS = ({"d1": "g1", "d2": "g2", "d3": "g3", "d4": "g4"},
        {"g1": "d1", "g2": "d2", "g3": "d3", "g4": "d4"})


def respond_theta(request):
    """g1 has two runs, g3 one, g2 none; g4 is a non-candidate that only the whole corpus returns."""
    rows = {"g1": [row(parent_key="g1", run_len=2.0, run_sum=1.3), row(parent_key="g1", run_len=1.0, run_sum=0.9)],
            "g3": [row(parent_key="g3", run_len=1.0, run_sum=0.7)],
            "g4": [row(parent_key="g4", run_len=5.0, run_sum=3.0)]}
    if request.where:
        return rows.get(request.where[0].value.string_val, [])
    return [r for rs in rows.values() for r in rs]


@pytest.mark.parametrize("shape, calls", [("per-candidate", 3), ("whole-corpus", 2)])
def test_run_pass_gives_the_same_runs_in_both_shapes(shape, calls):
    coordinator, log = coordinator_for(respond_theta)
    runs = pl.run_pass(coordinator, shape, lambda q, guid: pl.theta_request("q", 0.5, guid, pl.shape_limit(shape)),
                       pl.theta_runs, BASELINE, ["q1", "q2"], MAPS)
    assert runs == {"q1": {"d1": [(2, 1.3), (1, 0.9)]}, "q2": {"d3": [(1, 0.7)]}}
    assert len(log) == calls


# ── TokenSession: both refreshes, independently timed and independently injectable ─────────────

def session_with(mint_acting_user=None, mint_service=None, clock=lambda: 0.0):
    """A TokenSession over fake minters, one of which counts calls if not overridden."""
    if mint_acting_user is None:
        actors = iter(f"actor-{i}" for i in range(1, 100))
        mint_acting_user = lambda: next(actors)  # noqa: E731
    if mint_service is None:
        services = iter((f"service-{i}", 3600.0) for i in range(1, 100))
        mint_service = lambda: next(services)  # noqa: E731
    return pl.TokenSession(mint_acting_user, mint_service, clock=clock)


def test_the_acting_user_token_is_not_reminted_before_the_refresh_age():
    now = [0.0]
    session = session_with(clock=lambda: now[0])
    first = session.acting_user_token()
    assert first == "actor-1"
    now[0] = pl.TOKEN_REFRESH_SECONDS - 1
    assert session.acting_user_token() == "actor-1"
    now[0] = pl.TOKEN_REFRESH_SECONDS
    assert session.acting_user_token() == "actor-2"
    assert session.acting_user_mints == 2


def test_a_pass_that_outlives_the_refresh_age_re_mints_the_acting_user_token():
    now = [0.0]
    actors = iter(f"actor-{i}" for i in range(1, 10))

    def respond(request):
        now[0] += 40 * 60          # every call takes 40 minutes
        return []
    session = session_with(mint_acting_user=lambda: next(actors), clock=lambda: now[0])
    coordinator, _ = coordinator_for(respond)
    tokens_used = []

    def build_request(q, guid):
        tokens_used.append(session.acting_user_token())
        return pl.theta_request("q", 0.5, guid, 10)

    pl.run_pass(coordinator, "per-candidate", build_request, pl.theta_runs, BASELINE, ["q1", "q2"], MAPS)
    # calls start at 0, 40 and 80 minutes (actor-1), then... the third starts at 80 < 90: still actor-1.
    assert tokens_used == ["actor-1", "actor-1", "actor-1"]
    pl.run_pass(coordinator, "per-candidate", build_request, pl.theta_runs, BASELINE, ["q1"], MAPS)
    # the fourth call starts at 120 minutes >= 90: re-minted.
    assert tokens_used[3:] == ["actor-2", "actor-2"]
    assert session.acting_user_mints == 2


def test_the_service_token_is_not_reminted_before_expires_in_minus_the_margin():
    now = [0.0]
    session = session_with(clock=lambda: now[0])
    first = session.service_token()
    assert first == "service-1"
    now[0] = 3600.0 - pl.SERVICE_TOKEN_REFRESH_MARGIN_SECONDS - 1
    assert session.service_token() == "service-1"
    now[0] = 3600.0 - pl.SERVICE_TOKEN_REFRESH_MARGIN_SECONDS
    assert session.service_token() == "service-2"
    assert session.service_mints == 2


def test_the_two_refreshes_are_independently_timed():
    """A long pass re-mints the service token on its own 3600s-based schedule, not the acting-user
    token's fixed 90-minute one -- confirming 'both refreshes' really are two independent clocks
    sharing only the injected clock function."""
    now = [0.0]
    session = session_with(clock=lambda: now[0])
    session.acting_user_token()
    session.service_token()
    now[0] = 3600.0 - pl.SERVICE_TOKEN_REFRESH_MARGIN_SECONDS      # service due, acting-user not
    assert session.service_token() == "service-2"
    assert session.acting_user_token() == "actor-1"
    assert (session.service_mints, session.acting_user_mints) == (2, 1)


# ── the channel's metadata plugins: both headers, current tokens, re-read every call ───────────

def test_service_bearer_plugin_carries_the_sessions_current_token():
    now = [0.0]
    session = session_with(clock=lambda: now[0])
    plugin = pl._ServiceBearerAuthPlugin(session.service_token)
    assert capture_metadata(plugin) == ((("authorization", "Bearer service-1"),), None)
    now[0] = 3600.0 - pl.SERVICE_TOKEN_REFRESH_MARGIN_SECONDS
    assert capture_metadata(plugin) == ((("authorization", "Bearer service-2"),), None)


def test_acting_user_plugin_carries_the_sessions_current_token():
    now = [0.0]
    session = session_with(clock=lambda: now[0])
    plugin = pl._ActingUserAuthPlugin(session.acting_user_token)
    assert capture_metadata(plugin) == (((pl.ACTING_USER_METADATA_KEY, "Bearer actor-1"),), None)
    now[0] = pl.TOKEN_REFRESH_SECONDS
    assert capture_metadata(plugin) == (((pl.ACTING_USER_METADATA_KEY, "Bearer actor-2"),), None)


def test_acting_user_metadata_key_is_x_acting_user_authorization():
    """Pins the literal header name the API expects, independent of the ACTING_USER_METADATA_KEY
    import succeeding for the wrong reason."""
    assert pl.ACTING_USER_METADATA_KEY == "x-acting-user-authorization"


# ── mint_service_token: the request, and every abort path ──────────────────────────────────────

def test_mint_service_token_request_carries_host_header_scope_and_grant_type():
    captured = {}

    def fake_urlopen(request, timeout=None):
        captured["headers"] = dict(request.header_items())
        captured["body"] = urllib.parse.parse_qs(request.data.decode("utf-8"))
        captured["timeout"] = timeout
        return FakeTokenResponse(200, {"access_token": "svc-tok", "expires_in": 3600})

    token, expires_in = pl.mint_service_token(
        "cid", "csecret", "https://token.example/token", "tenant_id_admin schema_admin admin",
        "authentik-server:9000", urlopen=fake_urlopen)
    assert (token, expires_in) == ("svc-tok", 3600.0)
    assert captured["headers"]["Host"] == "authentik-server:9000"
    assert captured["body"] == {
        "grant_type": ["client_credentials"], "client_id": ["cid"], "client_secret": ["csecret"],
        "scope": ["tenant_id_admin schema_admin admin"],
    }


def test_mint_service_token_omits_scope_when_falsy():
    captured = {}

    def fake_urlopen(request, timeout=None):
        captured["body"] = urllib.parse.parse_qs(request.data.decode("utf-8"))
        return FakeTokenResponse(200, {"access_token": "t", "expires_in": 3600})

    pl.mint_service_token("cid", "csecret", "https://token.example/token", None, "host", urlopen=fake_urlopen)
    assert "scope" not in captured["body"]


def test_mint_service_token_aborts_on_a_non_200_response():
    def fake_urlopen(request, timeout=None):
        return FakeTokenResponse(500, {"error": "boom"})

    with pytest.raises(SystemExit, match="HTTP 500"):
        pl.mint_service_token("cid", "csecret", "https://token.example/token", None, "host", urlopen=fake_urlopen)


def test_mint_service_token_aborts_on_a_missing_access_token():
    def fake_urlopen(request, timeout=None):
        return FakeTokenResponse(200, {"expires_in": 3600})

    with pytest.raises(SystemExit, match="access_token"):
        pl.mint_service_token("cid", "csecret", "https://token.example/token", None, "host", urlopen=fake_urlopen)


def test_mint_service_token_aborts_on_a_non_positive_expires_in():
    def fake_urlopen(request, timeout=None):
        return FakeTokenResponse(200, {"access_token": "t", "expires_in": 0})

    with pytest.raises(SystemExit, match="expires_in"):
        pl.mint_service_token("cid", "csecret", "https://token.example/token", None, "host", urlopen=fake_urlopen)


def test_mint_service_token_aborts_on_a_transport_error():
    def fake_urlopen(request, timeout=None):
        raise OSError("connection refused")

    with pytest.raises(SystemExit, match="connection refused"):
        pl.mint_service_token("cid", "csecret", "https://token.example/token", None, "host", urlopen=fake_urlopen)


def test_mint_service_token_never_leaks_the_secret_or_the_token_in_an_abort_message():
    def fake_urlopen(request, timeout=None):
        return FakeTokenResponse(500, {"error": "boom"})

    with pytest.raises(SystemExit) as exc_info:
        pl.mint_service_token("cid", "top-secret-value", "https://token.example/token", None, "host",
                              urlopen=fake_urlopen)
    assert "top-secret-value" not in str(exc_info.value)


# ── /build ──────────────────────────────────────────────────────────────────────────────

def test_run_checked_pass_records_both_checks_when_the_build_matches():
    result, check = pl.run_checked_pass(lambda url: "c1", "u", "c1", "p50", lambda: 42)
    assert result == 42 and check == {"pass": "p50", "before": "c1", "after": "c1"}


def test_a_build_mismatch_before_the_pass_aborts_without_running_it():
    ran = []
    with pytest.raises(SystemExit, match="BUILD MISMATCH"):
        pl.run_checked_pass(lambda url: "c2", "u", "c1", "p50", lambda: ran.append(1))
    assert ran == []


def test_a_build_mismatch_after_the_pass_aborts():
    answers = iter(["c1", "c2"])
    with pytest.raises(SystemExit, match="BUILD MISMATCH"):
        pl.run_checked_pass(lambda url: next(answers), "u", "c1", "calibration", lambda: None)


# ── theta percentiles over the 100 lowest query ids ─────────────────────────────────────

def test_sample_is_the_100_lowest_query_ids_in_numeric_order():
    ids = [str(i) for i in range(1, 106)]                  # "10" < "9" as strings, not as numbers
    assert pl.sample_query_ids(ids) == [str(i) for i in range(1, 101)]


def test_theta_uses_only_the_sample_queries_chunks():
    # query i has one candidate with chunks s = i/1000 and i/1000 + 0.0005; queries 101..105 carry
    # huge values that would move every percentile if they leaked in.
    calibration = {str(i): {"d": [(0, i / 1000), (1, i / 1000 + 0.0005)]} for i in range(1, 101)}
    calibration.update({str(i): {"d": [(0, 99.0)]} for i in range(101, 106)})
    theta = pl.theta_from_calibration(calibration, list(calibration))
    values = sorted(s for i in range(1, 101) for s in (i / 1000, i / 1000 + 0.0005))
    assert theta["rows_in_sample"] == 200
    assert theta["sample_query_ids"] == [str(i) for i in range(1, 101)]
    # inclusive (linear) method: position (n - 1) * p over the sorted 200 values
    for name, p in (("p50", 0.50), ("p75", 0.75), ("p90", 0.90)):
        pos = (len(values) - 1) * p
        lo = int(pos)
        expected = values[lo] + (values[lo + 1] - values[lo]) * (pos - lo)
        assert theta[name] == pytest.approx(expected)


def test_percentiles_on_a_hand_computable_set():
    assert pl.percentiles([float(i) for i in range(101)]) == {"p50": 50.0, "p75": 75.0, "p90": 90.0}


def test_fewer_than_100_queries_refuses_theta():
    with pytest.raises(SystemExit):
        pl.sample_query_ids([str(i) for i in range(99)])


# ── writers: TREC order equals rank order ───────────────────────────────────────────────

def test_pattern_trec_file_order_is_rank_order(tmp_path):
    runs = {"q1": {"d1": [(1, 0.4)], "d2": [(3, 1.8)], "d3": [(3, 1.8)], "d4": [(2, 2.5)]}}
    baseline = {"q1": ["d1", "d2", "d3", "d4", "d5"], "q2": ["d6"]}
    path = tmp_path / "mp-p50-run_len.trec"
    pl.write_pattern_trec(str(path), runs, ["q1", "q2"], baseline, "run_len", "mp-p50-run_len")
    rows = [line.split() for line in path.read_text().splitlines()]
    assert [r[2] for r in rows] == ["d2", "d3", "d4", "d1"] == pl.pattern_order(runs["q1"], "run_len", baseline["q1"])
    assert [int(r[3]) for r in rows] == [1, 2, 3, 4]
    scores = [float(r[4]) for r in rows]
    assert all(a > b for a, b in zip(scores, scores[1:]))
    assert {r[0] for r in rows} == {"q1"}                   # q2 matched nothing: no rows


def test_write_scores_holds_each_arms_primary_and_its_tiebreak(tmp_path):
    runs = {"q1": {"d1": [(3, 1.0), (1, 2.0)]}}
    path = tmp_path / "scores-p50.tsv"
    pl.write_scores(str(path), runs, ["q1"], {"q1": ["d1", "d2"]})
    lines = path.read_text().splitlines()
    assert lines[0] == "query_id\tdoc_id\trun_len\trun_sum\trun_sum_of_best_len\trun_len_of_best_sum"
    assert lines[1:] == ["q1\td1\t3\t2.0\t1.0\t1"]


# ── probe B's decision rule ─────────────────────────────────────────────────────────────

def test_probe_decision_prefers_per_candidate_within_8_hours():
    # 8 h = 28,800 s over 33,600 calls = 0.857 s per call
    assert pl.probe_decision({"calibration": 0.85, "theta": 0.80}, [(40.0, True)]) == "per-candidate"


def test_probe_decision_falls_back_to_whole_corpus_then_stops():
    slow = {"calibration": 0.9, "theta": 0.5}
    assert pl.probe_decision(slow, [(12.0, False), (29.9, False)]) == "whole-corpus"
    assert pl.probe_decision(slow, [(12.0, False), (30.0, True)]) == "STOP"

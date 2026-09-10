"""pytest suite for beta_invariant.py's three preconditions (spec
docs/specs/2026-09-09-chunk-coverage-phase2-design.md §4). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_beta_invariant.py -q

No non-stdlib imports -- nothing needs PYTHONPATH."""
import json
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import beta_invariant  # noqa: E402


def write_hits(path, rows):
    """rows: [(queryId, parentKey, rank, score)] -- matches ChunkHitDumpWriter's exact header."""
    with open(path, "w", encoding="utf-8") as f:
        f.write("queryId\tparentKey\trank\tscore\n")
        for query_id, parent_key, rank, score in rows:
            f.write(f"{query_id}\t{parent_key}\t{rank}\t{score}\n")


def write_keymap(path, mapping):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(mapping, f)


def write_scores(path, rows):
    """rows: [(queryId, docId, scoreString)] -- header matches benchmark-aggregate's --scores-path
    output exactly: `queryId\tdocId\tscore`. `scoreString` is written verbatim, unparsed -- the
    production loader keeps it as the exact source string too."""
    with open(path, "w", encoding="utf-8") as f:
        f.write("queryId\tdocId\tscore\n")
        for query_id, doc_id, score in rows:
            f.write(f"{query_id}\t{doc_id}\t{score}\n")


def write_sidecar(path, beta, extra=None):
    payload = {"beta": beta}
    if extra:
        payload.update(extra)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f)


# ── load_scores ──────────────────────────────────────────────────────────────────────────

def test_load_scores_parses_rows_keeping_score_as_the_source_string(tmp_path):
    p = tmp_path / "l.scores.tsv"
    write_scores(p, [("q1", "d1", "0.9000000000000001"), ("q1", "d2", "0.5")])
    assert beta_invariant.load_scores(str(p)) == {
        ("q1", "d1"): "0.9000000000000001",
        ("q1", "d2"): "0.5",
    }


def test_load_scores_exits_on_malformed_row(tmp_path):
    p = tmp_path / "l.scores.tsv"
    p.write_text("queryId\tdocId\tscore\nq1\td1\n", encoding="utf-8")  # missing the score field
    with pytest.raises(SystemExit):
        beta_invariant.load_scores(str(p))


def test_load_scores_exits_on_non_numeric_score(tmp_path):
    p = tmp_path / "l.scores.tsv"
    p.write_text("queryId\tdocId\tscore\nq1\td1\tnotanumber\n", encoding="utf-8")
    with pytest.raises(SystemExit):
        beta_invariant.load_scores(str(p))


def test_load_scores_exits_on_duplicate_pair(tmp_path):
    p = tmp_path / "l.scores.tsv"
    p.write_text("queryId\tdocId\tscore\nq1\td1\t0.9\nq1\td1\t0.5\n", encoding="utf-8")
    with pytest.raises(SystemExit):
        beta_invariant.load_scores(str(p))


# ── load_sidecar_beta ────────────────────────────────────────────────────────────────────

def test_load_sidecar_beta_reads_the_beta_field(tmp_path):
    p = tmp_path / "b3387.meta.json"
    write_sidecar(p, 0.003387, extra={"composite": "abc123", "reranker": None})
    assert beta_invariant.load_sidecar_beta(str(p)) == 0.003387


def test_load_sidecar_beta_exits_when_beta_field_is_absent(tmp_path):
    p = tmp_path / "b0.meta.json"
    p.write_text(json.dumps({"composite": "abc123"}), encoding="utf-8")
    with pytest.raises(SystemExit):
        beta_invariant.load_sidecar_beta(str(p))


def test_load_sidecar_beta_exits_on_invalid_json(tmp_path):
    p = tmp_path / "b0.meta.json"
    p.write_text("{not json", encoding="utf-8")
    with pytest.raises(SystemExit):
        beta_invariant.load_sidecar_beta(str(p))


# ── find_single_and_multi_chunk_pairs ────────────────────────────────────────────────────

def test_find_single_and_multi_chunk_pairs_splits_by_pooled_chunk_count():
    by_doc = {
        ("q1", "d1"): [0.9],                # 1 chunk -- single
        ("q1", "d2"): [0.9, 0.5],           # 2 chunks -- multi
        ("q1", "d3"): [0.9, 0.5, 0.4],      # 3 chunks -- multi
    }
    single, multi = beta_invariant.find_single_and_multi_chunk_pairs(by_doc)
    assert single == [("q1", "d1")]
    assert multi == [("q1", "d2"), ("q1", "d3")]


# ── check_single_chunk_pairs_identical (spec §4 check 1) ────────────────────────────────
# The tests below are the ones that matter: each makes check 1 go red by disabling the exact
# condition it exists to catch.

def test_check_single_chunk_pairs_identical_exits_when_zero_single_chunk_pairs():
    """A fixture with zero single-chunk pairs: the mandatory non-zero count must itself fail, and
    the message must name the zero count -- spec §4's own rule, that a check which silently
    asserts nothing is indistinguishable from a check that passed."""
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.check_single_chunk_pairs_identical([], {}, {})
    assert "0 single-chunk pairs" in str(excinfo.value)


def test_check_single_chunk_pairs_identical_exits_when_a_pair_differs():
    single_keys = [("q1", "d1")]
    scores_zero = {("q1", "d1"): "0.9"}
    scores_parity = {("q1", "d1"): "0.8"}  # a single-chunk pair has no tail -- must be identical
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.check_single_chunk_pairs_identical(single_keys, scores_zero, scores_parity)
    assert "d1" in str(excinfo.value)


def test_check_single_chunk_pairs_identical_passes_and_returns_the_asserted_count():
    single_keys = [("q1", "d1"), ("q1", "d2")]
    scores_zero = {("q1", "d1"): "0.9", ("q1", "d2"): "0.7"}
    scores_parity = {("q1", "d1"): "0.9", ("q1", "d2"): "0.7"}
    assert beta_invariant.check_single_chunk_pairs_identical(single_keys, scores_zero, scores_parity) == 2


def test_check_single_chunk_pairs_identical_skips_pairs_missing_from_either_scores_file():
    single_keys = [("q1", "d1"), ("q1", "d2")]
    scores_zero = {("q1", "d1"): "0.9"}       # d2 absent here
    scores_parity = {("q1", "d1"): "0.9", ("q1", "d2"): "0.7"}
    assert beta_invariant.check_single_chunk_pairs_identical(single_keys, scores_zero, scores_parity) == 1


# ── check_multi_chunk_pairs_differ (spec §4 check 2, the positive control) ──────────────

def test_check_multi_chunk_pairs_differ_exits_when_nothing_differs():
    """The positive control's own falsifying case: it must catch a self-comparison (or a
    silently-inert beta) even when every input is otherwise well-formed."""
    multi_keys = [("q1", "d1")]
    scores_zero = {("q1", "d1"): "0.9"}
    scores_parity = {("q1", "d1"): "0.9"}
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.check_multi_chunk_pairs_differ(multi_keys, scores_zero, scores_parity)
    assert "check 2" in str(excinfo.value)


def test_check_multi_chunk_pairs_differ_passes_and_returns_the_differing_count():
    multi_keys = [("q1", "d1"), ("q1", "d2")]
    scores_zero = {("q1", "d1"): "0.9", ("q1", "d2"): "0.7"}
    scores_parity = {("q1", "d1"): "0.95", ("q1", "d2"): "0.7"}  # only d1 differs
    assert beta_invariant.check_multi_chunk_pairs_differ(multi_keys, scores_zero, scores_parity) == 1


# ── check_sidecars_carry_the_ladder (spec §4 check 3) ────────────────────────────────────

def test_check_sidecars_carry_the_ladder_exits_when_a_beta_exceeds_max_beta():
    """spec §4's own example: 'a decimal slip putting the top arm at 0.35800 -- ten times parity'
    must be caught here, and only here -- checks 1 and 2 cannot see it, because a single-chunk
    score does not depend on beta at all and any other wrong non-zero beta still moves every
    multi-chunk pair. The message must name the bound."""
    betas_by_path = {"b0.meta.json": 0.0, "b35800.meta.json": 0.35800}
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.check_sidecars_carry_the_ladder(betas_by_path, [0.0, 0.35800], max_beta=0.035800)
    message = str(excinfo.value)
    assert "0.0358" in message   # the bound
    assert "0.358" in message    # the offending value


def test_check_sidecars_carry_the_ladder_exits_when_a_sidecar_is_missing():
    """Five sidecars where the ladder specifies six -- multiset comparison must catch a dropped
    arm even though every present value is individually within bound and on the ladder."""
    ladder = [0.0, 0.003387, 0.006107, 0.011012, 0.019855, 0.035800]
    betas_by_path = {f"b{i}.meta.json": v for i, v in enumerate(ladder[:-1])}  # last arm missing
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.check_sidecars_carry_the_ladder(betas_by_path, ladder, max_beta=0.035800)
    assert "check 3" in str(excinfo.value)


def test_check_sidecars_carry_the_ladder_passes_when_multiset_matches_and_none_exceed_bound():
    ladder = [0.0, 0.003387, 0.006107, 0.011012, 0.019855, 0.035800]
    betas_by_path = {f"b{i}.meta.json": v for i, v in enumerate(ladder)}
    assert beta_invariant.check_sidecars_carry_the_ladder(betas_by_path, ladder, max_beta=0.035800) == 6


# ── validate_max_beta ─────────────────────────────────────────────────────────────────────
# check 3's bound comparison is `beta > max_beta`, and that comparison fails OPEN for a non-finite
# max_beta -- `0.358 > float("nan")` and `0.358 > float("inf")` are both False. These are the tests
# that matter: each gives the guard exactly the value that would otherwise silently defeat check 3,
# the sweep's only protection against a mistyped non-zero beta.

def test_validate_max_beta_exits_on_nan():
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.validate_max_beta(float("nan"))
    assert "nan" in str(excinfo.value).lower()


def test_validate_max_beta_exits_on_positive_infinity():
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.validate_max_beta(float("inf"))
    assert "inf" in str(excinfo.value).lower()


def test_validate_max_beta_exits_on_negative_value():
    with pytest.raises(SystemExit):
        beta_invariant.validate_max_beta(-1.0)


def test_validate_max_beta_accepts_the_default_and_an_explicit_in_range_value():
    """Guard against over-rejecting: ordinary, in-range values (including the zero boundary) must
    not raise."""
    beta_invariant.validate_max_beta(0.035800)  # the default
    beta_invariant.validate_max_beta(0.05)      # an explicit in-range value
    beta_invariant.validate_max_beta(0.0)       # the boundary -- zero is finite and non-negative


# ── build_arg_parser: the nargs='+' detail ───────────────────────────────────────────────

def test_sidecar_and_ladder_accept_multiple_values_after_a_single_flag():
    """The detail the task brief calls out as easy to get wrong: nargs='+' lets one --sidecar /
    one --ladder flag gather several space-separated tokens, exactly how Task 3 invokes this
    script (a shell glob expansion, and six ladder values). Built with action='append' instead,
    each of these flags would consume only its first following token and leave the rest as
    unrecognized arguments -- parse_args would raise SystemExit here, not return a 3-element list."""
    args = beta_invariant.build_arg_parser().parse_args([
        "--hits", "h", "--keymap", "k", "--scores-zero", "z", "--scores-parity", "p",
        "--sidecar", "s1", "s2", "s3",
        "--ladder", "0", "0.1", "0.2",
    ])
    assert args.sidecar == ["s1", "s2", "s3"]
    assert args.ladder == [0.0, 0.1, 0.2]


# ── main(): end-to-end CLI ────────────────────────────────────────────────────────────────

def test_main_exits_on_an_invalid_max_beta_before_touching_any_input_file(monkeypatch):
    """End-to-end confirmation of 'before any check runs': --hits/--keymap/--scores-zero/
    --scores-parity/--sidecar all point at paths that do not exist. If validate_max_beta's call in
    main() were ever removed, or moved to run after the file loads instead of before them, this
    test would fail with an unhandled FileNotFoundError instead of the clean SystemExit asserted
    here -- so it pins both the guard's presence and its position."""
    monkeypatch.setattr(sys, "argv", [
        "beta_invariant.py",
        "--hits", "/nonexistent/h.chunks.hits.tsv", "--keymap", "/nonexistent/keymap.json",
        "--scores-zero", "/nonexistent/z.scores.tsv", "--scores-parity", "/nonexistent/p.scores.tsv",
        "--sidecar", "/nonexistent/s.meta.json", "--ladder", "0",
        "--max-beta", "nan",
    ])
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.main()
    assert "max-beta" in str(excinfo.value)


def test_main_exits_on_check_2_when_the_same_scores_file_is_passed_twice(tmp_path, monkeypatch):
    """spec §4's positive control, exercised through the real CLI argument names: passing one file
    as both --scores-zero and --scores-parity means every pair -- including every multi-chunk pair
    -- compares equal to itself. Check 1 passes trivially (equal is exactly what it wants), so this
    is check 2's failure alone."""
    hits_path = tmp_path / "l.chunks.hits.tsv"
    keymap_path = tmp_path / "keymap.json"
    scores_path = tmp_path / "l.scores.tsv"

    write_hits(hits_path, [
        ("q1", "pA", 1, 0.9),                          # single-chunk -> d1
        ("q1", "pB", 2, 0.8), ("q1", "pB", 3, 0.3),     # multi-chunk -> d2
    ])
    write_keymap(keymap_path, {"pA": "d1", "pB": "d2"})
    write_scores(scores_path, [("q1", "d1", "0.9"), ("q1", "d2", "0.8")])

    monkeypatch.setattr(sys, "argv", [
        "beta_invariant.py",
        "--hits", str(hits_path), "--keymap", str(keymap_path),
        "--scores-zero", str(scores_path), "--scores-parity", str(scores_path),
        "--sidecar", "unused.json", "--ladder", "0",
    ])
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.main()
    assert "check 2" in str(excinfo.value)


def test_main_end_to_end_well_formed_input_exits_zero_and_prints_both_counts(tmp_path, monkeypatch, capsys):
    """spec §4's full sweep of a well-formed input: one single-chunk pair identical at both beta,
    two multi-chunk pairs that each move, and six sidecars whose beta values are exactly the
    ladder. main() must return normally (no SystemExit) and print all three check counts."""
    hits_path = tmp_path / "fs2048-pool.chunks.hits.tsv"
    keymap_path = tmp_path / "keymap.json"
    zero_path = tmp_path / "fs2048-b0.scores.tsv"
    parity_path = tmp_path / "fs2048-b35800.scores.tsv"

    write_hits(hits_path, [
        ("q1", "pA", 1, 0.90),                                                 # single-chunk -> d1
        ("q1", "pB", 2, 0.80), ("q1", "pB", 3, 0.30),                          # multi-chunk -> d2
        ("q2", "pC", 1, 0.70), ("q2", "pC", 2, 0.60), ("q2", "pC", 3, 0.20),   # multi-chunk -> d3
    ])
    write_keymap(keymap_path, {"pA": "d1", "pB": "d2", "pC": "d3"})
    # d1 (single-chunk) is identical at both beta -- it has no tail to move it.
    # d2, d3 (multi-chunk) differ at parity -- the tail term moved them.
    write_scores(zero_path, [("q1", "d1", "0.9"), ("q1", "d2", "0.8"), ("q2", "d3", "0.7")])
    write_scores(parity_path, [("q1", "d1", "0.9"), ("q1", "d2", "0.8109"), ("q2", "d3", "0.7132")])

    ladder = [0.0, 0.003387, 0.006107, 0.011012, 0.019855, 0.035800]
    sidecar_paths = []
    for i, beta in enumerate(ladder):
        sp = tmp_path / f"b{i}.meta.json"
        write_sidecar(sp, beta)
        sidecar_paths.append(str(sp))

    monkeypatch.setattr(sys, "argv", [
        "beta_invariant.py",
        "--hits", str(hits_path), "--keymap", str(keymap_path),
        "--scores-zero", str(zero_path), "--scores-parity", str(parity_path),
        "--sidecar", *sidecar_paths,
        "--ladder", *[str(b) for b in ladder],
    ])
    beta_invariant.main()  # must not raise
    out = capsys.readouterr().out
    assert "check 1 PASSED: 1 single-chunk pairs" in out
    assert "check 2 PASSED: 2 multi-chunk pairs" in out
    assert "check 3 PASSED: 6 sidecar" in out


def test_main_exits_on_check_3_when_a_sidecar_beta_exceeds_max_beta(tmp_path, monkeypatch):
    """Full-CLI version of the decimal-slip scenario: checks 1 and 2 pass (the fixture is
    otherwise well-formed), so a beta over --max-beta must be caught by check 3 alone, and the
    exit message must name the bound."""
    hits_path = tmp_path / "l.chunks.hits.tsv"
    keymap_path = tmp_path / "keymap.json"
    zero_path = tmp_path / "b0.scores.tsv"
    parity_path = tmp_path / "b35800.scores.tsv"

    write_hits(hits_path, [
        ("q1", "pA", 1, 0.9),                        # single-chunk -> d1
        ("q1", "pB", 2, 0.8), ("q1", "pB", 3, 0.3),   # multi-chunk -> d2
    ])
    write_keymap(keymap_path, {"pA": "d1", "pB": "d2"})
    write_scores(zero_path, [("q1", "d1", "0.9"), ("q1", "d2", "0.8")])
    write_scores(parity_path, [("q1", "d1", "0.9"), ("q1", "d2", "0.81")])  # d2 moves -- check 2 passes

    good_sidecar = tmp_path / "b0.meta.json"
    bad_sidecar = tmp_path / "b35800.meta.json"
    write_sidecar(good_sidecar, 0.0)
    write_sidecar(bad_sidecar, 0.35800)  # a decimal slip: ten times parity

    monkeypatch.setattr(sys, "argv", [
        "beta_invariant.py",
        "--hits", str(hits_path), "--keymap", str(keymap_path),
        "--scores-zero", str(zero_path), "--scores-parity", str(parity_path),
        "--sidecar", str(good_sidecar), str(bad_sidecar),
        "--ladder", "0", "0.35800",
    ])
    with pytest.raises(SystemExit) as excinfo:
        beta_invariant.main()
    message = str(excinfo.value)
    assert "check 3" in message
    assert "0.0358" in message  # the bound is named

"""pytest suite for tail_stats.py's pure functions. Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_tail_stats.py -q

No non-stdlib imports -- nothing needs PYTHONPATH."""
import json
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tail_stats  # noqa: E402


def write_hits(path, rows):
    """rows: [(queryId, parentKey, rank, score)] -- matches ChunkHitDumpWriter's exact header."""
    with open(path, "w", encoding="utf-8") as f:
        f.write("queryId\tparentKey\trank\tscore\n")
        for query_id, parent_key, rank, score in rows:
            f.write(f"{query_id}\t{parent_key}\t{rank}\t{score}\n")


def write_run(path, rows, tag="t"):
    """rows: [(queryId, [(docId, score), ...])] -- ranks assigned 1..n in list order, like
    TrecRunWriter."""
    with open(path, "w", encoding="utf-8") as f:
        for query_id, doc_scores in rows:
            for i, (doc_id, score) in enumerate(doc_scores):
                f.write(f"{query_id} Q0 {doc_id} {i + 1} {score:.6f} {tag}\n")


def write_keymap(path, mapping):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(mapping, f)


# ── load_hits / load_run_doc_ids / load_keymap ──────────────────────────────────────────

def test_load_hits_parses_the_dump_in_file_order(tmp_path):
    p = tmp_path / "l.chunks.hits.tsv"
    write_hits(p, [("q1", "pA", 1, 0.9), ("q1", "pB", 2, 0.5), ("q2", "pC", 1, 0.7)])
    assert tail_stats.load_hits(str(p)) == [("q1", "pA", 0.9), ("q1", "pB", 0.5), ("q2", "pC", 0.7)]


def test_load_hits_header_only_file_yields_no_rows(tmp_path):
    p = tmp_path / "l.chunks.hits.tsv"
    write_hits(p, [])
    assert tail_stats.load_hits(str(p)) == []


def test_load_hits_exits_on_malformed_row(tmp_path):
    p = tmp_path / "l.chunks.hits.tsv"
    p.write_text("queryId\tparentKey\trank\tscore\nq1\tpA\tnotenough\n", encoding="utf-8")
    with pytest.raises(SystemExit):
        tail_stats.load_hits(str(p))


def test_load_hits_exits_when_rank_disagrees_with_file_order(tmp_path):
    """A dump that has been sorted, filtered or concatenated still parses row-by-row, and every
    figure this script prints is derived from file order -- so the rank column is the only thing
    that can catch it."""
    p = tmp_path / "l.chunks.hits.tsv"
    write_hits(p, [("q1", "pA", 1, 0.9), ("q1", "pB", 3, 0.5)])  # row 2 of q1 claims rank 3
    with pytest.raises(SystemExit) as excinfo:
        tail_stats.load_hits(str(p))
    assert "rank 3" in str(excinfo.value)
    assert ":3" in str(excinfo.value)  # the offending row number


def test_load_hits_rank_is_per_query_not_per_file(tmp_path):
    """Each query's ranks restart at 1; a per-file running rank would be wrong."""
    p = tmp_path / "l.chunks.hits.tsv"
    write_hits(p, [("q1", "pA", 1, 0.9), ("q1", "pB", 2, 0.5), ("q2", "pC", 1, 0.7)])
    assert tail_stats.load_hits(str(p)) == [("q1", "pA", 0.9), ("q1", "pB", 0.5), ("q2", "pC", 0.7)]


def test_load_hits_exits_on_non_integer_rank(tmp_path):
    p = tmp_path / "l.chunks.hits.tsv"
    p.write_text("queryId\tparentKey\trank\tscore\nq1\tpA\tx\t0.9\n", encoding="utf-8")
    with pytest.raises(SystemExit):
        tail_stats.load_hits(str(p))


def test_load_run_doc_ids_groups_by_query_in_file_order(tmp_path):
    p = tmp_path / "l.chunks.trec"
    write_run(p, [("q1", [("d1", 0.9), ("d2", 0.5)]), ("q2", [("d3", 0.7)])])
    assert tail_stats.load_run_doc_ids(str(p)) == {
        "q1": [("d1", 0.9), ("d2", 0.5)],
        "q2": [("d3", 0.7)],
    }


def test_load_keymap_reads_flat_parentkey_to_docid_json(tmp_path):
    p = tmp_path / "keymap.json"
    write_keymap(p, {"pA": "d1", "pB": "d2"})
    assert tail_stats.load_keymap(str(p)) == {"pA": "d1", "pB": "d2"}


# ── resolve_by_doc ───────────────────────────────────────────────────────────────────────

def test_resolve_by_doc_groups_hits_by_query_and_resolved_doc_id():
    hits = [("q1", "pA", 0.9), ("q1", "pA", 0.4), ("q1", "pB", 0.8)]
    keymap = {"pA": "d1", "pB": "d2"}
    by_doc, unresolved = tail_stats.resolve_by_doc(hits, keymap)
    assert by_doc == {("q1", "d1"): [0.9, 0.4], ("q1", "d2"): [0.8]}
    assert unresolved == 0


def test_resolve_by_doc_excludes_unmapped_parent_keys():
    hits = [("q1", "pA", 0.9), ("q1", "pGhost", 0.99)]
    keymap = {"pA": "d1"}
    by_doc, unresolved = tail_stats.resolve_by_doc(hits, keymap)
    assert by_doc == {("q1", "d1"): [0.9]}
    assert unresolved == 1


# ── tail_scores / tail_depth_histogram / mean_tail_score ────────────────────────────────

def test_tail_scores_takes_2nd_through_4th_highest_only():
    assert tail_stats.tail_scores([0.9]) == []                               # 1 chunk: no tail
    assert tail_stats.tail_scores([0.9, 0.5]) == [0.5]                       # 2 chunks: 1 tail value
    assert tail_stats.tail_scores([0.9, 0.5, 0.4]) == [0.5, 0.4]             # 3 chunks: 2 tail values
    assert tail_stats.tail_scores([0.9, 0.5, 0.4, 0.3]) == [0.5, 0.4, 0.3]   # 4 chunks: capped at 3
    assert tail_stats.tail_scores([0.9, 0.5, 0.4, 0.3, 0.2]) == [0.5, 0.4, 0.3]  # 5th dropped by the cap
    # order in the input must not matter -- sorted internally, highest first
    assert tail_stats.tail_scores([0.3, 0.9, 0.4, 0.5]) == [0.5, 0.4, 0.3]


def test_tail_depth_histogram_buckets_by_pooled_chunk_count():
    by_doc = {
        ("q1", "d1"): [0.9],                    # 1 chunk -- in neither bucket
        ("q1", "d2"): [0.9, 0.5],                # 2 chunks
        ("q1", "d3"): [0.9, 0.5, 0.4],           # 3 chunks
        ("q1", "d4"): [0.9, 0.5, 0.4, 0.3],      # 4 chunks
        ("q1", "d5"): [0.9, 0.5, 0.4, 0.3, 0.2],  # 5 chunks -- still "4+"
        ("q2", "d1"): [0.9, 0.5],                # another 2-chunk doc, different query
    }
    hist = tail_stats.tail_depth_histogram(by_doc)
    assert hist == {2: 2, 3: 1, "4+": 2}


def test_mean_tail_score_averages_only_flattened_tail_values():
    by_doc = {
        ("q1", "d1"): [0.9],                 # no tail -- contributes nothing
        ("q1", "d2"): [0.9, 0.6],            # tail = [0.6]
        ("q1", "d3"): [0.9, 0.4, 0.2],       # tail = [0.4, 0.2]
    }
    mean, n = tail_stats.mean_tail_score(by_doc)
    # flattened tail values: 0.6, 0.4, 0.2 -> mean = 0.4, not a mean of per-doc means (0.6, 0.3)
    assert mean == pytest.approx(0.4)
    assert n == 3


def test_mean_tail_score_is_none_when_nothing_has_a_tail():
    by_doc = {("q1", "d1"): [0.9], ("q1", "d2"): [0.5]}
    mean, n = tail_stats.mean_tail_score(by_doc)
    assert mean is None
    assert n == 0


def test_one_chunk_document_contributes_to_neither_histogram_nor_s():
    by_doc = {("q1", "d1"): [0.9]}
    hist = tail_stats.tail_depth_histogram(by_doc)
    mean, n = tail_stats.mean_tail_score(by_doc)
    assert hist == {2: 0, 3: 0, "4+": 0}
    assert mean is None
    assert n == 0


# ── scoped_to_run ─────────────────────────────────────────────────────────────────────────

def test_scoped_to_run_selects_on_the_run_files_document_set_not_the_dumps():
    """A document can carry many pooled chunks in the dump and still miss the top 50 (e.g. its
    max chunk score wasn't competitive). scoped_to_run must exclude it -- the histogram and s are
    properties of the top-50 ranking, not of the raw pool."""
    by_doc = {
        ("q1", "d1"): [0.9, 0.5, 0.4, 0.3],  # in the run file
        ("q1", "d2"): [0.9, 0.5, 0.4, 0.3],  # NOT in the run file -- deep pooled chunks, never top-50
    }
    run_by_query = {"q1": [("d1", 0.9)]}
    scoped = tail_stats.scoped_to_run(by_doc, run_by_query)
    assert scoped == {("q1", "d1"): [0.9, 0.5, 0.4, 0.3]}


def test_scoped_to_run_is_per_query():
    by_doc = {("q1", "d1"): [0.9, 0.5], ("q2", "d1"): [0.9, 0.5]}
    run_by_query = {"q1": [("d1", 0.9)]}  # q2/d1 not in the run file at all
    scoped = tail_stats.scoped_to_run(by_doc, run_by_query)
    assert scoped == {("q1", "d1"): [0.9, 0.5]}


def test_pool_wide_figure_covers_every_parent_in_the_dump():
    """Unlike scoped_to_run, the pool-wide mean_tail_score call is made directly on by_doc (no
    run-file filtering) -- so a document that never reached the top 50 still contributes."""
    by_doc = {
        ("q1", "d1"): [0.9, 0.5],   # reaches top 50
        ("q1", "d2"): [0.8, 0.1],   # does not
    }
    run_by_query = {"q1": [("d1", 0.9)]}
    scoped_mean, _ = tail_stats.mean_tail_score(tail_stats.scoped_to_run(by_doc, run_by_query))
    pool_mean, pool_n = tail_stats.mean_tail_score(by_doc)
    assert scoped_mean == pytest.approx(0.5)
    assert pool_mean == pytest.approx((0.5 + 0.1) / 2)
    assert pool_n == 2


# ── check_scope_covers_run ───────────────────────────────────────────────────────────────

def test_check_scope_covers_run_passes_when_every_run_row_is_recovered():
    by_doc = {("q1", "d1"): [0.9, 0.5], ("q1", "d2"): [0.8], ("q1", "d3"): [0.4]}
    run_by_query = {"q1": [("d1", 0.9), ("d2", 0.8)]}
    scoped = tail_stats.scoped_to_run(by_doc, run_by_query)
    tail_stats.check_scope_covers_run(scoped, run_by_query, "h", "r", "k")  # must not exit


def test_check_scope_covers_run_exits_on_a_run_keymap_dump_mismatch():
    """The wrong corpus's keymap.json still resolves every parentKey -- just to other documents --
    so `unresolved` stays 0 and the intersection silently shrinks. Only the row-count equality
    catches it."""
    by_doc = {("q1", "other-1"): [0.9, 0.5], ("q1", "d1"): [0.8, 0.2]}
    run_by_query = {"q1": [("d1", 0.9), ("d2", 0.8), ("d3", 0.7)]}  # 3 rows; only d1 is recoverable
    scoped = tail_stats.scoped_to_run(by_doc, run_by_query)
    assert len(scoped) == 1
    with pytest.raises(SystemExit) as excinfo:
        tail_stats.check_scope_covers_run(scoped, run_by_query, "hits.tsv", "run.trec", "keymap.json")
    message = str(excinfo.value)
    assert "1" in message and "3" in message      # both counts named
    assert "keymap.json" in message               # and the likely culprit pointed at


def test_main_exits_on_a_run_keymap_mismatch(tmp_path, monkeypatch):
    """End to end: a keymap that maps the dump's parents to documents the run file never names.
    `unresolved` is 0 throughout, and before this check the script printed a confident, wrong s."""
    hits_path = tmp_path / "l.chunks.hits.tsv"
    run_path = tmp_path / "l.chunks.trec"
    keymap_path = tmp_path / "keymap.json"

    write_hits(hits_path, [("q1", "pA", 1, 0.9), ("q1", "pA", 2, 0.5)])
    write_run(run_path, [("q1", [("d1", 0.9), ("d2", 0.8)])])
    write_keymap(keymap_path, {"pA": "wrong-corpus-doc"})  # valid JSON, wrong corpus

    monkeypatch.setattr(sys, "argv", [
        "tail_stats.py", "--hits", str(hits_path), "--run", str(run_path), "--keymap", str(keymap_path),
    ])
    with pytest.raises(SystemExit):
        tail_stats.main()


# ── span_rank1_to_rank50 / median_top10_adjacent_gap ────────────────────────────────────

def test_span_rank1_to_rank50_is_the_mean_per_query_top_to_bottom_gap():
    run_by_query = {
        "q1": [("d1", 1.0), ("d2", 0.9), ("d3", 0.8)],  # span 0.2
        "q2": [("d1", 2.0), ("d2", 1.0)],                # span 1.0
    }
    assert tail_stats.span_rank1_to_rank50(run_by_query) == pytest.approx((0.2 + 1.0) / 2)


def test_span_rank1_to_rank50_ignores_single_row_queries():
    run_by_query = {"q1": [("d1", 1.0)], "q2": [("d1", 2.0), ("d2", 1.0)]}
    assert tail_stats.span_rank1_to_rank50(run_by_query) == pytest.approx(1.0)


def test_span_rank1_to_rank50_is_none_when_no_query_qualifies():
    assert tail_stats.span_rank1_to_rank50({"q1": [("d1", 1.0)]}) is None


def test_median_top10_adjacent_gap_pools_gaps_across_queries_before_taking_the_median():
    run_by_query = {
        "q1": [("d1", 1.00), ("d2", 0.90), ("d3", 0.70)],  # gaps: 0.10, 0.20
        "q2": [("d1", 1.00), ("d2", 0.95)],                 # gap: 0.05
    }
    # pooled gaps = [0.10, 0.20, 0.05] -> median 0.10
    assert tail_stats.median_top10_adjacent_gap(run_by_query) == pytest.approx(0.10)


def test_median_top10_adjacent_gap_only_considers_the_top_10_rows_per_query():
    rows = [("d%d" % i, 1.0 - i * 0.01) for i in range(15)]  # 15 rows, gap 0.01 each
    run_by_query = {"q1": rows}
    # only the first 10 rows -> 9 gaps, all 0.01
    assert tail_stats.median_top10_adjacent_gap(run_by_query) == pytest.approx(0.01)


def test_median_top10_adjacent_gap_is_none_when_no_query_has_two_rows():
    assert tail_stats.median_top10_adjacent_gap({"q1": [("d1", 1.0)]}) is None


# ── main(): end-to-end CLI ──────────────────────────────────────────────────────────────

def test_main_prints_histogram_s_and_ladder_endpoints(tmp_path, monkeypatch, capsys):
    hits_path = tmp_path / "l.chunks.hits.tsv"
    run_path = tmp_path / "l.chunks.trec"
    keymap_path = tmp_path / "keymap.json"

    write_hits(hits_path, [
        # rank is 1..N per QUERY in supply order, exactly as ChunkHitDumpWriter writes it -- it does
        # not restart per parentKey.
        ("q1", "pA", 1, 0.90), ("q1", "pA", 2, 0.50), ("q1", "pA", 3, 0.40), ("q1", "pA", 4, 0.30),
        ("q1", "pB", 5, 0.80), ("q1", "pB", 6, 0.20),
        ("q1", "pC", 7, 0.10),  # single-chunk document, never reaches top 50 -- pool-wide only
    ])
    write_run(run_path, [("q1", [("d1", 0.90), ("d2", 0.80)])])
    write_keymap(keymap_path, {"pA": "d1", "pB": "d2", "pC": "d3"})

    monkeypatch.setattr(sys, "argv", [
        "tail_stats.py", "--hits", str(hits_path), "--run", str(run_path), "--keymap", str(keymap_path),
    ])
    tail_stats.main()
    out = capsys.readouterr().out

    assert "4+ chunks   1" in out   # d1 has 4 pooled chunks
    assert "2 chunks    1" in out   # d2 has 2 pooled chunks
    assert "s (top-50 scoped)" in out
    assert "s (pool-wide)" in out
    assert "tie-break beta" in out
    assert "parity beta" in out


def test_main_exits_when_s_is_undefined(tmp_path, monkeypatch):
    """Every document in the dump has exactly one chunk -- s has no values, and the ladder cannot
    be derived. Must fail loudly rather than divide by zero or print a nonsense endpoint."""
    hits_path = tmp_path / "l.chunks.hits.tsv"
    run_path = tmp_path / "l.chunks.trec"
    keymap_path = tmp_path / "keymap.json"

    write_hits(hits_path, [("q1", "pA", 1, 0.9)])
    write_run(run_path, [("q1", [("d1", 0.9)])])
    write_keymap(keymap_path, {"pA": "d1"})

    monkeypatch.setattr(sys, "argv", [
        "tail_stats.py", "--hits", str(hits_path), "--run", str(run_path), "--keymap", str(keymap_path),
    ])
    with pytest.raises(SystemExit):
        tail_stats.main()

"""pytest suite for aspect_oracle.py's three oracle rankings and file I/O (spec
docs/specs/2026-09-13-aspect-coverage-oracle-design.md §2.2, §2.8). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_aspect_oracle.py -q

No non-stdlib imports -- nothing needs PYTHONPATH."""
import collections
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import aspect_oracle  # noqa: E402


# --- Shared fixture: one small hand-written run covering three properties -----------------
#
# q_none   -- a query with NO relevant documents.
# q_tie    -- two relevant docs with the SAME nugget count (1 each); B order is x1, x2, x3.
# q_greedy -- the discriminator fixture from the plan: B order d3, d2, d1; d1 and d2 both
#             cover {n1, n2}, d3 covers only {n1}. The alpha-discounted greedy and a
#             not-yet-covered-count greedy diverge on their SECOND pick.

RUN = collections.OrderedDict([
    ("q_none", ["a1", "a2", "a3"]),
    ("q_tie", ["x1", "x2", "x3"]),
    ("q_greedy", ["d3", "d2", "d1"]),
])

REL = {
    "q_tie": {"x1", "x2"},
    "q_greedy": {"d1", "d2", "d3"},
}

NUG = {
    "q_tie": {"x1": {"n1"}, "x2": {"n2"}},
    "q_greedy": {"d1": {"n1", "n2"}, "d2": {"n1", "n2"}, "d3": {"n1"}},
}


def count_greedy(docs, rel, nug):
    """A not-yet-covered-COUNT greedy, structurally identical to rank_G except the gain is
    the count of nuggets not yet covered rather than the alpha-discounted sum. Used only to
    prove rank_G is not secretly this function. Not part of aspect_oracle.py -- it computes
    a different objective, kept local to the test that discriminates against it."""
    order = aspect_oracle.b_index(docs)
    remaining = [d for d in docs if d in rel]
    covered = set()
    out = []
    while remaining:
        best = max(remaining, key=lambda d: (
            len(set(nug.get(d, ())) - covered),
            -order[d],
        ))
        out.append(best)
        covered |= set(nug.get(best, ()))
        remaining.remove(best)
    return out + [d for d in docs if d not in rel]


# --- rank_R / rank_A / rank_G: permutation + identical-prefix properties ------------------

@pytest.mark.parametrize("qid", list(RUN.keys()))
@pytest.mark.parametrize("rank_fn", [aspect_oracle.rank_R, aspect_oracle.rank_A, aspect_oracle.rank_G])
def test_ranking_is_permutation_of_input(rank_fn, qid):
    docs = RUN[qid]
    rel = REL.get(qid, set())
    nug = NUG.get(qid, {})
    result = rank_fn(docs, rel, nug)
    assert sorted(result) == sorted(docs)
    assert len(result) == len(docs)


@pytest.mark.parametrize("qid", list(RUN.keys()))
def test_all_three_rankings_place_identical_relevant_set_in_prefix(qid):
    docs = RUN[qid]
    rel = REL.get(qid, set())
    nug = NUG.get(qid, {})
    n_rel = len(rel)
    for rank_fn in (aspect_oracle.rank_R, aspect_oracle.rank_A, aspect_oracle.rank_G):
        result = rank_fn(docs, rel, nug)
        prefix = set(result[:n_rel])
        assert prefix == rel, f"{rank_fn.__name__} on {qid}: prefix {prefix} != rel {rel}"
        # and every non-relevant doc is strictly after the prefix
        assert all(d not in rel for d in result[n_rel:])


def test_rank_R_no_relevant_documents_returns_original_order():
    docs = RUN["q_none"]
    assert aspect_oracle.rank_R(docs, set(), {}) == docs


def test_rank_A_no_relevant_documents_returns_original_order():
    docs = RUN["q_none"]
    assert aspect_oracle.rank_A(docs, set(), {}) == docs


def test_rank_G_no_relevant_documents_returns_original_order():
    docs = RUN["q_none"]
    assert aspect_oracle.rank_G(docs, set(), {}) == docs


def test_rank_A_ties_resolved_by_b_order():
    # x1 and x2 both cover exactly 1 nugget; B order is x1 before x2.
    docs = RUN["q_tie"]
    result = aspect_oracle.rank_A(docs, REL["q_tie"], NUG["q_tie"])
    assert result == ["x1", "x2", "x3"]


def test_rank_G_first_pick_matches_count_greedy_but_full_ordering_diverges():
    docs = RUN["q_greedy"]
    rel = REL["q_greedy"]
    nug = NUG["q_greedy"]

    g_result = aspect_oracle.rank_G(docs, rel, nug)
    c_result = count_greedy(docs, rel, nug)

    # Exact orderings from the plan's discriminating fixture.
    assert g_result == ["d2", "d1", "d3"]
    assert c_result == ["d2", "d3", "d1"]

    # First pick is provably identical (seen is empty, so (1-ALPHA)**0 == 1 == count).
    assert g_result[0] == c_result[0] == "d2"

    # Full ordering differs -- the assertion is on the full sequence, not just "differs".
    assert g_result != c_result


def test_rank_G_does_not_stop_once_all_nuggets_are_covered():
    # After picking d2 then d1, both nuggets {n1, n2} are already covered by earlier picks,
    # yet d3 (still relevant) must still be appended -- there is no termination clause.
    docs = RUN["q_greedy"]
    result = aspect_oracle.rank_G(docs, REL["q_greedy"], NUG["q_greedy"])
    assert len(result) == 3
    assert set(result) == {"d1", "d2", "d3"}
    # and the still-covered-but-relevant d3 is ordered last, by its (now-zero) discounted gain
    assert result[-1] == "d3"


# --- b_index --------------------------------------------------------------------------------

def test_b_index_maps_docs_to_position():
    assert aspect_oracle.b_index(["d3", "d2", "d1"]) == {"d3": 0, "d2": 1, "d1": 2}


# --- write_trec -----------------------------------------------------------------------------

def test_write_trec_strictly_decreasing_scores_and_six_columns(tmp_path):
    ranking = collections.OrderedDict([
        ("q1", ["d2", "d1", "d3"]),
        ("q2", ["e1"]),
    ])
    path = tmp_path / "oracle-R.trec"
    aspect_oracle.write_trec(str(path), ranking, "oracle-R")

    lines = path.read_text(encoding="utf-8").splitlines()
    assert len(lines) == 4  # 3 + 1 rows

    by_qid = collections.defaultdict(list)
    for line in lines:
        fields = line.split()
        assert len(fields) == 6
        qid, q0, docid, rank, score, tag = fields
        assert q0 == "Q0"
        assert tag == "oracle-R"
        by_qid[qid].append((int(rank), float(score), docid))

    for qid, rows in by_qid.items():
        rows.sort(key=lambda r: r[0])
        scores = [r[1] for r in rows]
        assert scores == sorted(scores, reverse=True)
        assert len(set(scores)) == len(scores)  # strictly decreasing, no ties

    # rank order matches the ranking's document order
    assert [docid for _, _, docid in sorted(by_qid["q1"])] == ["d2", "d1", "d3"]


# --- filter_qrels -----------------------------------------------------------------------------

def test_filter_qrels_keeps_only_requested_qids_and_drops_the_rest(tmp_path):
    src = tmp_path / "qrels.trec"
    src.write_text(
        "q1 0 d1 1\n"
        "q2 0 d2 0\n"
        "q3 0 d3 1\n"
        "q1 0 d4 0\n",
        encoding="utf-8",
    )
    dst = tmp_path / "qrels.610.trec"
    aspect_oracle.filter_qrels(str(src), str(dst), {"q1", "q3"})

    lines = dst.read_text(encoding="utf-8").splitlines()
    qids = [line.split()[0] for line in lines]
    assert qids == ["q1", "q3", "q1"]
    assert "q2" not in qids


# --- select_610 -----------------------------------------------------------------------------

def test_select_610_requires_relevant_document_inside_the_runs_own_list():
    run = collections.OrderedDict([
        ("q_in_run", ["d1", "d2"]),      # d1 is relevant AND in the run's 50
        ("q_only_in_qrels", ["d3", "d4"]),  # relevant doc d5 exists only in the qrels, not here
        ("q_no_relevant", ["d6", "d7"]),
    ])
    rel = {
        "q_in_run": {"d1"},
        "q_only_in_qrels": {"d5"},
    }
    assert aspect_oracle.select_610(run, rel) == {"q_in_run"}


# --- loaders --------------------------------------------------------------------------------

def test_load_run_preserves_file_order_and_groups_by_qid(tmp_path):
    path = tmp_path / "run.trec"
    path.write_text(
        "q1 Q0 d1 1 3.000000 tag\n"
        "q1 Q0 d2 2 2.000000 tag\n"
        "q2 Q0 d3 1 1.000000 tag\n",
        encoding="utf-8",
    )
    run = aspect_oracle.load_run(str(path))
    assert list(run.keys()) == ["q1", "q2"]
    assert run["q1"] == ["d1", "d2"]
    assert run["q2"] == ["d3"]


def test_load_rel_keeps_only_positive_relevance(tmp_path):
    path = tmp_path / "qrels.trec"
    path.write_text(
        "q1 0 d1 1\n"
        "q1 0 d2 0\n"
        "q2 0 d3 2\n",
        encoding="utf-8",
    )
    rel = aspect_oracle.load_rel(str(path))
    assert rel == {"q1": {"d1"}, "q2": {"d3"}}


def test_load_nuggets_groups_by_doc_and_drops_non_positive_relevance(tmp_path):
    path = tmp_path / "qrels.nugget.trec"
    path.write_text(
        "q1 n1 d1 1\n"
        "q1 n2 d1 1\n"
        "q1 n1 d2 0\n"
        "q2 n3 d3 1\n",
        encoding="utf-8",
    )
    nug = aspect_oracle.load_nuggets(str(path))
    assert nug == {"q1": {"d1": {"n1", "n2"}}, "q2": {"d3": {"n3"}}}


# --- summary_rows -----------------------------------------------------------------------------

def test_summary_rows_counts_relevant_nuggets_and_top10_reach():
    run = collections.OrderedDict([("q_tie", RUN["q_tie"])])
    rel = {"q_tie": REL["q_tie"]}
    nug = {"q_tie": NUG["q_tie"]}
    rankings = collections.OrderedDict([
        ("B", {"q_tie": run["q_tie"]}),
        ("R", {"q_tie": aspect_oracle.rank_R(run["q_tie"], rel["q_tie"], nug["q_tie"])}),
    ])
    rows, labels = aspect_oracle.summary_rows(run, rel, nug, rankings)
    assert labels == ["B", "R"]
    assert len(rows) == 1
    qid, relevant_count, distinct_nuggets, top10_b, top10_r = rows[0]
    assert qid == "q_tie"
    assert relevant_count == 2       # x1, x2
    assert distinct_nuggets == 2     # n1, n2
    assert top10_b == 2              # all 3 docs fit in top 10 either way
    assert top10_r == 2


# --- end-to-end: main() writes exactly the six required files -------------------------------

def test_main_writes_exactly_the_required_output_files(tmp_path, monkeypatch):
    run_path = tmp_path / "run.trec"
    qrels_path = tmp_path / "qrels.trec"
    nugget_path = tmp_path / "qrels.nugget.trec"
    out_dir = tmp_path / "out"

    run_path.write_text(
        "q_greedy Q0 d3 1 0.900000 b0\n"
        "q_greedy Q0 d2 2 0.800000 b0\n"
        "q_greedy Q0 d1 3 0.700000 b0\n"
        "q_none Q0 a1 1 0.900000 b0\n"
        "q_none Q0 a2 2 0.800000 b0\n",
        encoding="utf-8",
    )
    qrels_path.write_text(
        "q_greedy 0 d1 1\n"
        "q_greedy 0 d2 1\n"
        "q_greedy 0 d3 1\n",
        encoding="utf-8",
    )
    nugget_path.write_text(
        "q_greedy n1 d1 1\n"
        "q_greedy n2 d1 1\n"
        "q_greedy n1 d2 1\n"
        "q_greedy n2 d2 1\n"
        "q_greedy n1 d3 1\n",
        encoding="utf-8",
    )

    argv = [
        "aspect_oracle.py",
        "--run", str(run_path),
        "--qrels", str(qrels_path),
        "--nugget-qrels", str(nugget_path),
        "--out-dir", str(out_dir),
    ]
    monkeypatch.setattr(sys, "argv", argv)
    aspect_oracle.main()

    expected = {
        "oracle-R.trec", "oracle-A.trec", "oracle-G.trec",
        "qrels.610.trec", "qrels.nugget.610.trec", "aspect-summary.tsv",
    }
    assert set(os.listdir(out_dir)) == expected

    # q_none has no relevant docs, so it must be absent from the 610-restricted qrels.
    kept_qids = {line.split()[0] for line in (out_dir / "qrels.610.trec").read_text().splitlines()}
    assert kept_qids == {"q_greedy"}

    # oracle-G.trec must reorder q_greedy relative to B (d3, d2, d1 -> d2, d1, d3).
    g_run = aspect_oracle.load_run(str(out_dir / "oracle-G.trec"))
    assert g_run["q_greedy"] == ["d2", "d1", "d3"]

    summary_lines = (out_dir / "aspect-summary.tsv").read_text().splitlines()
    assert summary_lines[0].split("\t") == [
        "qid", "relevant_in_50", "distinct_nuggets",
        "nuggets_top10_B", "nuggets_top10_R", "nuggets_top10_A", "nuggets_top10_G",
    ]
    assert len(summary_lines) == 3  # header + q_greedy row + q_none row

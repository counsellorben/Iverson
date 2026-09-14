"""pytest suite for popularity_rerank.py's arithmetic (spec
docs/specs/2026-09-13-popularity-signal-measurement-design.md, "Phase 1 -- offline screen"). Run
with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_popularity_rerank.py -q

These pin the piecewise identity itself, not the data: every fixture below is hand-computable, so a
refactor that quietly swapped a branch, a divisor, or a `pop=0`/median substitution for the absent
case fails here even though the real corpus (0 divisor exceptions, mostly-resolved counts) would
never exercise it.

Four things are pinned:
  * the present-branch arithmetic, at both divisors (0.90 uniform, 0.45 for a --divisor-exceptions
    document -- the latter is unreachable on real data per Task 3, so this is its only coverage);
  * the absent branch leaves the score untouched -- not pop=0, not a median;
  * the resolved-zero-count case takes the PRESENT branch (pop=0/(0+S)=0), not the absent one;
  * both of Step 2's mandatory assertions, including their failure paths.

No non-stdlib imports beyond pytest -- nothing needs PYTHONPATH."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import popularity_rerank as pr  # noqa: E402


# --------------------------------------------------------------------------------------------
# popularity() / fuse() -- the identity's arithmetic, hand-computable
# --------------------------------------------------------------------------------------------

def test_popularity_is_count_over_count_plus_saturation():
    assert pr.popularity(10, 190) == pytest.approx(10.0 / 200.0)


def test_popularity_of_a_resolved_zero_count_is_zero_not_absent():
    """A resolved zero is data, not a missing value -- it takes pop = 0/(0+S) = 0."""
    assert pr.popularity(0, 182) == 0.0


def test_fuse_at_the_uniform_divisor_matches_the_spec_identity_by_hand():
    """fused_old=0.6, count=18, S=182 -> pop = 18/200 = 0.09.
    W=0.2: fused_new = (0.90*0.6 + 0.2*0.09) / (0.90+0.2) = (0.54+0.018)/1.10 = 0.558/1.10."""
    result = pr.fuse(fused_old=0.6, count=18, w=0.2, saturation=182, divisor_base=pr.UNIFORM_DIVISOR)
    assert result == pytest.approx((0.90 * 0.6 + 0.2 * 0.09) / 1.10)
    assert result == pytest.approx(0.558 / 1.10)


def test_fuse_at_the_divisor_exception_base_matches_the_spec_identity_by_hand():
    """Same inputs, but divisor_base=0.45 (a document lacking body_centroid):
    fused_new = (0.45*0.6 + 0.2*0.09) / (0.45+0.2) = (0.27+0.018)/0.65."""
    result = pr.fuse(fused_old=0.6, count=18, w=0.2, saturation=182, divisor_base=0.45)
    assert result == pytest.approx((0.45 * 0.6 + 0.2 * 0.09) / 0.65)
    assert result == pytest.approx(0.288 / 0.65)


def test_fuse_at_the_two_divisors_gives_genuinely_different_results():
    """Same fused_old, count, W, S -- only the divisor differs. If a refactor ever collapsed the two
    divisor branches into one constant, this fails."""
    uniform = pr.fuse(fused_old=0.6, count=18, w=0.2, saturation=182, divisor_base=pr.UNIFORM_DIVISOR)
    exception = pr.fuse(fused_old=0.6, count=18, w=0.2, saturation=182, divisor_base=0.45)
    assert uniform != pytest.approx(exception)


def test_fuse_of_a_resolved_zero_count_pulls_the_score_toward_zero():
    """count=0 -> pop=0, so fused_new = (0.90*fused_old + 0) / (0.90+W), strictly less than
    fused_old for any W>0 and fused_old>0 -- the present branch punishes a genuine zero, which is
    the documented, intended behaviour (spec: "a count of 0 ... scores 0.0 and is punished hard" is
    what substituting 0 for ABSENT would wrongly do to a document that isn't even zero; a document
    that IS genuinely zero is correctly pulled down some, not slammed to 0.0, by the weighted mean)."""
    fused_old = 0.6
    result = pr.fuse(fused_old=fused_old, count=0, w=0.2, saturation=182, divisor_base=pr.UNIFORM_DIVISOR)
    assert result == pytest.approx((0.90 * fused_old) / 1.10)
    assert result < fused_old


# --------------------------------------------------------------------------------------------
# rerank_query -- both branches selected correctly, per document
# --------------------------------------------------------------------------------------------

def test_rerank_query_routes_present_documents_through_fuse():
    rows = [("a", 0.6, "tag")]
    counts = {"a": 18}
    reranked, absent = pr.rerank_query(rows, counts, set(), w=0.2, saturation=182)
    expected = pr.fuse(0.6, 18, 0.2, 182, pr.UNIFORM_DIVISOR)
    assert reranked == [("a", pytest.approx(expected), "tag")]
    assert absent == []


def test_rerank_query_leaves_absent_documents_exactly_unchanged():
    """No resolved count for 'x' -- fused_old must come back bit-identical, not recomputed."""
    rows = [("x", 0.42, "tag")]
    counts = {}
    reranked, absent = pr.rerank_query(rows, counts, set(), w=0.2, saturation=182)
    assert reranked == [("x", 0.42, "tag")]
    assert reranked[0][1] is rows[0][1]  # the SAME float object, never recomputed
    assert absent == [("x", 0.42, 0.42)]


def test_rerank_query_a_resolved_zero_count_takes_the_present_branch_not_absent():
    """The distinction the whole exercise rests on: {'z': 0} is PRESENT (membership, not
    truthiness). It must be scored by fuse(), not passed through, and must not appear in `absent`."""
    rows = [("z", 0.6, "tag")]
    counts = {"z": 0}
    reranked, absent = pr.rerank_query(rows, counts, set(), w=0.2, saturation=182)
    expected = pr.fuse(0.6, 0, 0.2, 182, pr.UNIFORM_DIVISOR)
    assert reranked == [("z", pytest.approx(expected), "tag")]
    assert reranked[0][1] != 0.6  # it WAS changed -- pulled toward zero, per fuse()'s own math
    assert absent == []


def test_rerank_query_routes_a_divisor_exception_document_through_the_045_base():
    """Only reachable via --divisor-exceptions -- Task 3 found zero of these in the real corpus, so
    this is the branch's only coverage. Without --divisor-exceptions naming it, the same document
    would fuse at 0.90 instead; the two must differ."""
    rows = [("e", 0.6, "tag")]
    counts = {"e": 18}
    reranked_exception, _ = pr.rerank_query(rows, counts, {"e"}, w=0.2, saturation=182)
    reranked_uniform, _ = pr.rerank_query(rows, counts, set(), w=0.2, saturation=182)
    assert reranked_exception[0][1] != pytest.approx(reranked_uniform[0][1])
    assert reranked_exception[0][1] == pytest.approx(pr.fuse(0.6, 18, 0.2, 182, 0.45))


def test_rerank_query_resorts_descending_by_new_score():
    """'a' starts ranked first but has a low count; 'b' starts second but has a high count. The
    identity must re-sort by the NEW score, not preserve the input order."""
    rows = [("a", 0.60, "tag"), ("b", 0.59, "tag")]
    counts = {"a": 0, "b": 1000}
    reranked, _ = pr.rerank_query(rows, counts, set(), w=5.0, saturation=182)
    assert [doc_id for doc_id, _, _ in reranked] == ["b", "a"]


def test_rerank_query_breaks_ties_by_original_order_stably():
    """Two absent documents keep identical scores (unchanged) -- the sort must be stable and
    preserve their original relative order, matching LINQ OrderByDescending's stability."""
    rows = [("first", 0.5, "tag"), ("second", 0.5, "tag")]
    reranked, _ = pr.rerank_query(rows, {}, set(), w=0.2, saturation=182)
    assert [doc_id for doc_id, _, _ in reranked] == ["first", "second"]


# --------------------------------------------------------------------------------------------
# Step 2 assertion 1 -- at least one query's order must change
# --------------------------------------------------------------------------------------------

def test_check_order_changed_counts_queries_whose_sequence_differs():
    pool = {"q1": [("a", 0.6, "t"), ("b", 0.5, "t")], "q2": [("c", 0.4, "t")]}
    reranked_pool = {"q1": [("b", 0.9, "t"), ("a", 0.1, "t")], "q2": [("c", 0.4, "t")]}
    assert pr.check_order_changed(pool, reranked_pool) == 1


def test_check_order_changed_exits_when_zero_queries_reorder():
    """A re-ranker that silently changed nothing must fail loudly, not report a quiet pass."""
    pool = {"q1": [("a", 0.6, "t"), ("b", 0.5, "t")]}
    reranked_pool = {"q1": [("a", 0.6, "t"), ("b", 0.5, "t")]}
    with pytest.raises(SystemExit):
        pr.check_order_changed(pool, reranked_pool)


# --------------------------------------------------------------------------------------------
# Step 2 assertion 2 -- the absent set must be non-empty AND every score unchanged
# --------------------------------------------------------------------------------------------

def test_check_absent_unchanged_exits_on_an_empty_set():
    """Vacuous truth is not a pass: zero absent pairs must fail loudly, since it is what both 'every
    document resolved' and 'the absent branch never ran' look like."""
    with pytest.raises(SystemExit):
        pr.check_absent_unchanged([])


def test_check_absent_unchanged_passes_when_every_pair_is_unchanged():
    pairs = [("q1", "a", 0.4, 0.4), ("q1", "b", 0.7, 0.7)]
    assert pr.check_absent_unchanged(pairs) == 2


def test_check_absent_unchanged_exits_when_any_absent_score_moved():
    """This is the branch most likely to be silently wrong: a mutant that substituted pop=0 (or any
    other recompute) for the absent case must be caught here."""
    pairs = [("q1", "a", 0.4, 0.4), ("q1", "b", 0.7, 0.0)]
    with pytest.raises(SystemExit):
        pr.check_absent_unchanged(pairs)


# --------------------------------------------------------------------------------------------
# validate_positive_finite -- guards --w and --saturation
# --------------------------------------------------------------------------------------------

@pytest.mark.parametrize("value", [0.0, -1.0, float("nan"), float("inf"), float("-inf")])
def test_validate_positive_finite_rejects_non_positive_or_non_finite(value):
    with pytest.raises(SystemExit):
        pr.validate_positive_finite(value, "--w")


def test_validate_positive_finite_accepts_an_ordinary_positive_value():
    pr.validate_positive_finite(0.2, "--w")  # must not raise


# --------------------------------------------------------------------------------------------
# loaders -- format assumptions, each a sys.exit rather than a warning
# --------------------------------------------------------------------------------------------

def write(path, text):
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    return str(path)


def test_load_run_reads_six_column_trec_preserving_tag_and_rank_order(tmp_path):
    path = write(tmp_path / "r.trec", "1 Q0 a 1 0.9 mytag\n1 Q0 b 2 0.8 mytag\n2 Q0 c 1 0.7 mytag\n")
    pool = pr.load_run(path)
    assert pool == {"1": [("a", 0.9, "mytag"), ("b", 0.8, "mytag")], "2": [("c", 0.7, "mytag")]}


def test_load_run_refuses_a_duplicate_document_within_one_pool(tmp_path):
    path = write(tmp_path / "r.trec", "1 Q0 a 1 0.9 t\n1 Q0 a 2 0.8 t\n")
    with pytest.raises(SystemExit):
        pr.load_run(path)


def test_load_run_refuses_a_malformed_row(tmp_path):
    path = write(tmp_path / "r.trec", "1 Q0 a 1 0.9\n")  # only 5 fields
    with pytest.raises(SystemExit):
        pr.load_run(path)


def test_load_divisor_exceptions_defaults_to_empty_when_path_is_none():
    assert pr.load_divisor_exceptions(None) == set()


def test_load_divisor_exceptions_reads_one_docid_per_line(tmp_path):
    path = write(tmp_path / "exc.txt", "1001\n1002\n\n1003\n")
    assert pr.load_divisor_exceptions(path) == {"1001", "1002", "1003"}


def test_load_divisor_exceptions_reads_an_empty_file_as_the_empty_set(tmp_path):
    """Task 3's real divisor-exceptions.txt is exactly this shape -- zero exceptions -- and it must
    stay inert, not be treated as a missing/invalid file."""
    path = write(tmp_path / "exc.txt", "")
    assert pr.load_divisor_exceptions(path) == set()


def test_load_divisor_exceptions_exits_when_the_file_does_not_exist(tmp_path):
    with pytest.raises(SystemExit):
        pr.load_divisor_exceptions(str(tmp_path / "missing.txt"))


# --------------------------------------------------------------------------------------------
# write_run -- rank recomputed, query order preserved from the input run
# --------------------------------------------------------------------------------------------

def test_write_run_recomputes_rank_and_preserves_query_order(tmp_path):
    reranked_pool = {
        "2": [("y", 0.9, "tag2")],
        "1": [("b", 0.8, "tag1"), ("a", 0.5, "tag1")],
    }
    out_path = str(tmp_path / "out.trec")
    pr.write_run(out_path, reranked_pool, pool_query_order=["1", "2"])
    with open(out_path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    assert lines == [
        "1 Q0 b 1 0.800000 tag1",
        "1 Q0 a 2 0.500000 tag1",
        "2 Q0 y 1 0.900000 tag2",
    ]

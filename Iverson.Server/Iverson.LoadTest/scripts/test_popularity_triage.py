"""pytest suite for popularity_triage.py's arithmetic (spec
docs/specs/2026-09-13-popularity-signal-measurement-design.md, "Phase 0 -- triage"). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_popularity_triage.py -q

These test the STATISTICS, not the data. The script's own sys.exit assertions check that the real
inputs are shaped as expected; nothing in them can catch an AUC that is computed wrongly but
computed consistently, and a wrong-but-consistent AUC is exactly the failure that would sail through
Phase 0 and land in the gate doc. So every fixture below is hand-checkable on paper: small integer
counts whose concordant-pair sums can be written out by hand.

Four things are pinned here:
  * the AUC arithmetic, including the tie-at-0.5 rule, which citation counts exercise constantly;
  * the eligibility predicate, including its ORDER against the admissibility restriction;
  * stratum assignment across all four date classes a document can fall into;
  * that the two estimators of measurement 1 are genuinely different statistics -- a fixture where
    they disagree, so a refactor that quietly collapsed one into the other would fail here.

No non-stdlib imports beyond pytest -- nothing needs PYTHONPATH."""
import datetime
import json
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import popularity_triage as pt  # noqa: E402


# --------------------------------------------------------------------------------------------
# concordant_pairs / auc -- the tie rule and the hand-checkable cross-products
# --------------------------------------------------------------------------------------------

def test_auc_perfect_separation():
    """pos 3 beats neg 1 and neg 2: 2 of 2 pairs concordant."""
    assert pt.concordant_pairs([3], [1, 2]) == (2.0, 2)
    assert pt.auc([3], [1, 2]) == 1.0


def test_auc_perfect_inversion():
    assert pt.concordant_pairs([1], [2, 3]) == (0.0, 2)
    assert pt.auc([1], [2, 3]) == 0.0


def test_a_single_tie_counts_one_half():
    assert pt.concordant_pairs([1], [1]) == (0.5, 1)
    assert pt.auc([1], [1]) == 0.5


def test_all_pairs_tied_is_exactly_one_half():
    """Six pairs, every one a tie: 6 * 0.5 = 3.0. Citation counts tie often enough that an
    implementation crediting a tie as a win (or as a loss) would visibly bias the result."""
    assert pt.concordant_pairs([5, 5], [5, 5, 5]) == (3.0, 6)
    assert pt.auc([5, 5], [5, 5, 5]) == 0.5


def test_mixed_wins_and_ties_by_hand():
    """pos [2, 3] vs neg [1, 2]:
        (2,1) win 1.0   (2,2) tie 0.5   (3,1) win 1.0   (3,2) win 1.0   = 3.5 over 4 pairs."""
    assert pt.concordant_pairs([2, 3], [1, 2]) == (3.5, 4)
    assert pt.auc([2, 3], [1, 2]) == pytest.approx(0.875)


def test_a_resolved_zero_is_a_real_value_and_ranks_below_everything():
    """A genuine count of 0 is data, not absence. It must lose every pair, not be dropped."""
    assert pt.auc([0], [1, 2]) == 0.0
    assert pt.auc([1, 2], [0]) == 1.0


def test_an_empty_side_yields_zero_pairs_not_an_undefined_ratio():
    """This is what lets 4b sum a degenerate stratum instead of aborting on it."""
    assert pt.concordant_pairs([], [1, 2]) == (0.0, 0)
    assert pt.concordant_pairs([1, 2], []) == (0.0, 0)
    assert pt.auc([], [1]) is None
    assert pt.auc([1], []) is None


def test_auc_is_order_free():
    """Shuffling either side must not move the statistic -- the bisect implementation sorts the
    negatives internally, and this pins that it does not depend on the positives' order either."""
    assert pt.auc([3, 1, 2], [2, 4]) == pt.auc([2, 3, 1], [4, 2])


# --------------------------------------------------------------------------------------------
# Hanley-McNeil SE
# --------------------------------------------------------------------------------------------

def test_hanley_mcneil_se_is_zero_under_perfect_separation():
    """At AUC = 1, Q1 = 1/(2-1) = 1 and Q2 = 2/(1+1) = 1, so every variance term is (1 - 1) = 0."""
    assert pt.hanley_mcneil_se(1.0, 10, 10) == pytest.approx(0.0)


def test_hanley_mcneil_se_shrinks_as_the_sample_grows():
    small = pt.hanley_mcneil_se(0.62, 50, 50)
    large = pt.hanley_mcneil_se(0.62, 500, 500)
    assert 0.0 < large < small


def test_hanley_mcneil_se_matches_the_published_formula():
    """A = 0.75, n_pos = 2, n_neg = 4 worked by hand:
        Q1 = 0.75 / 1.25 = 0.6,  Q2 = 2*0.5625 / 1.75 = 0.642857142857...,  A^2 = 0.5625
        var = (0.75*0.25 + 1*(0.6 - 0.5625) + 3*(0.642857142857 - 0.5625)) / 8"""
    q1, q2, a2 = 0.6, 2 * 0.5625 / 1.75, 0.5625
    expected = ((0.75 * 0.25) + 1 * (q1 - a2) + 3 * (q2 - a2)) / 8.0
    assert pt.hanley_mcneil_se(0.75, 2, 4) == pytest.approx(expected ** 0.5)


def test_hanley_mcneil_se_is_undefined_without_both_sides():
    assert pt.hanley_mcneil_se(0.5, 0, 10) is None
    assert pt.hanley_mcneil_se(0.5, 10, 0) is None


# --------------------------------------------------------------------------------------------
# eligibility -- and the order it is applied in
# --------------------------------------------------------------------------------------------

def test_eligibility_needs_both_a_relevant_and_a_non_relevant_document():
    pool = {"only-rel": ["a", "b"], "only-non": ["c", "d"], "both": ["a", "c"]}
    relevant = {"only-rel": {"a", "b"}, "both": {"a"}}
    assert pt.eligible_queries(pool, relevant, lambda _d: True) == ["both"]


def test_a_query_with_no_judgement_at_all_is_ineligible():
    """300 - 275 of the real queries are this case: retrieved 50, judged relevant on none of them."""
    pool = {"unjudged": ["a", "b", "c"]}
    assert pt.eligible_queries(pool, {}, lambda _d: True) == []


def test_eligibility_is_judged_after_the_admissibility_restriction_not_before():
    """THE ORDER TEST. `q` looks eligible on its raw pool -- one relevant, two non-relevant -- but
    its only relevant document has no count. Judged raw, it would be called eligible while having no
    positive left to rank, and its per-query AUC would be undefined. It must be ineligible."""
    pool = {"q": ["rel-no-count", "n1", "n2"]}
    relevant = {"q": {"rel-no-count"}}
    counted = {"n1", "n2"}
    assert pt.eligible_queries(pool, relevant, lambda _d: True) == ["q"]
    assert pt.eligible_queries(pool, relevant, counted.__contains__) == []


#: Three queries, deliberately spanning all three cases the drop count has to distinguish:
#:   `kept`     -- raw-eligible, survives the restriction, holds 1 uncounted observation
#:   `lost`     -- raw-eligible, loses its only positive to the restriction, holds 1 uncounted
#:   `unjudged` -- NOT raw-eligible (no relevant document at all), holds 2 uncounted observations
#: `unjudged` is what makes the fixture able to tell the two candidate scopes apart: counted over the
#: RAW-ELIGIBLE pools the drop is 2, counted over every pool in the run it is 4. On the real data that
#: same distinction is 803 against 897, and 25 of the 300 queries are `unjudged`-shaped.
DROP_SCOPE_POOL = {
    "kept": ["r1", "n1", "n-no-count"],
    "lost": ["r-no-count", "n2", "n3"],
    "unjudged": ["u1", "u-no-count-a", "u-no-count-b"],
}
DROP_SCOPE_RELEVANT = {"kept": {"r1"}, "lost": {"r-no-count"}}
DROP_SCOPE_COUNTED = {"r1", "n1", "n2", "n3", "u1"}


def test_observations_reports_the_drop_that_reconciles_the_two_populations():
    """`kept` survives the restriction; `lost` does not."""
    eligible, split, dropped = pt.observations(
        DROP_SCOPE_POOL, DROP_SCOPE_RELEVANT, DROP_SCOPE_COUNTED.__contains__)
    assert eligible == ["kept"]
    assert split == {"kept": (["r1"], ["n1"])}
    assert dropped == 2  # `n-no-count` from kept's pool and `r-no-count` from lost's


def test_the_drop_count_is_scoped_to_the_raw_eligible_pools_not_to_every_pool():
    """THE SCOPE TEST. The drop count is the figure that reconciles the raw-pool population against
    the analysed one, so it must be counted over exactly the pools the raw-pool population is counted
    over -- the RAW-ELIGIBLE ones -- and not over every pool in the run. An ineligible query's
    uncounted observations were never in the 311/13,439 side of the reconciliation, so counting them
    into the drop would make the two sides fail to reconcile while still looking plausible.

    `unjudged` is ineligible and holds 2 uncounted observations. Widening the scope to `pool` would
    report 4 instead of 2; on the real data it reports 897 instead of 803."""
    _, _, dropped = pt.observations(
        DROP_SCOPE_POOL, DROP_SCOPE_RELEVANT, DROP_SCOPE_COUNTED.__contains__)
    raw_eligible = pt.eligible_queries(DROP_SCOPE_POOL, DROP_SCOPE_RELEVANT, lambda _d: True)
    assert set(raw_eligible) == {"kept", "lost"}  # `unjudged` is NOT raw-eligible

    over_raw_eligible = sum(1 for q in raw_eligible for d in DROP_SCOPE_POOL[q]
                            if d not in DROP_SCOPE_COUNTED)
    over_every_pool = sum(1 for q in DROP_SCOPE_POOL for d in DROP_SCOPE_POOL[q]
                          if d not in DROP_SCOPE_COUNTED)
    assert (over_raw_eligible, over_every_pool) == (2, 4)  # the two scopes genuinely differ here
    assert dropped == over_raw_eligible
    assert dropped != over_every_pool


def test_has_resolved_count_uses_membership_not_truthiness():
    """The distinction the whole include/exclude decision rests on. A resolved 0 is admissible; an
    absent id is not. `bool(counts.get(doc))` would confuse the two, and the real cache contains
    resolved zeros, so the confusion would silently delete real observations."""
    admissible = pt.has_resolved_count({"zero": 0, "some": 7})
    assert admissible("zero") is True
    assert admissible("some") is True
    assert admissible("absent") is False


def test_has_resolved_date_requires_a_count_as_well_as_a_date():
    """4a runs over the same analysis population as measurement 1, so a dated document with no
    count is still inadmissible."""
    counts = {"a": 0, "b": 3}
    dates = {"a": "2001-01-01", "dated-uncounted": "2001-01-01"}
    years = {"b": 1999, "yearonly-uncounted": 1999}
    admissible = pt.has_resolved_date(counts, dates, years)
    assert admissible("a") is True      # resolved zero count + publicationDate
    assert admissible("b") is True      # count + year-only fallback
    assert admissible("c") is False     # neither a count nor a date

    # The two cases the name is actually about: a document that HAS a date but NO count. Without
    # these, dropping `doc_id in counts` from the predicate passes every other assertion here --
    # no other fixture document carries a date without a count.
    assert admissible("dated-uncounted") is False     # publicationDate, but absent from counts
    assert admissible("yearonly-uncounted") is False  # year fallback, but absent from counts

    assert pt.has_resolved_date({"d": 1}, {}, {})("d") is False  # count but no date of either kind


def test_observations_keeps_a_resolved_zero_in_the_population():
    """Presence must be tested with `in`, never truthiness: a count of 0 is admissible."""
    counts = {"r": 0, "n": 7}
    pool, relevant = {"q": ["r", "n"]}, {"q": {"r"}}
    eligible, split, dropped = pt.observations(pool, relevant, counts.__contains__)
    assert eligible == ["q"] and dropped == 0
    assert split["q"] == (["r"], ["n"])
    assert pt.auc([counts[d] for d in split["q"][0]], [counts[d] for d in split["q"][1]]) == 0.0


# --------------------------------------------------------------------------------------------
# the two estimators of measurement 1 are different statistics
# --------------------------------------------------------------------------------------------

DISAGREEING_SPLIT = {
    # query A: its relevant paper is the most-cited thing in its own pool  -> per-query AUC 1.0
    "A": (["a-rel"], ["a-n1", "a-n2", "a-n3"]),
    # query B: its relevant paper is the least-cited thing in its own pool -> per-query AUC 0.0
    "B": (["b-rel"], ["b-n1", "b-n2", "b-n3"]),
}
DISAGREEING_COUNTS = {"a-rel": 10, "a-n1": 1, "a-n2": 2, "a-n3": 3,
                      "b-rel": 4, "b-n1": 5, "b-n2": 6, "b-n3": 7}


def test_the_pooled_estimator_takes_the_full_cross_product():
    """pos [10, 4] vs neg [1, 2, 3, 5, 6, 7]: 10 beats all six; 4 beats 1, 2, 3 and loses to
    5, 6, 7. 9 concordant of 12 pairs = 0.75. Only 6 of those 12 pairs are within-pool."""
    result = pt.pooled_estimator(DISAGREEING_SPLIT, DISAGREEING_COUNTS.__getitem__)
    assert result["n_pos"] == 2 and result["n_neg"] == 6
    assert result["pairs"] == 12
    assert result["within_pool_pairs"] == 6
    assert result["auc"] == pytest.approx(0.75)


def test_the_per_query_estimator_averages_within_pool_aucs():
    """1.0 and 0.0, so the pool-matched mean is 0.5 -- against the pooled form's 0.75 on the very
    same observations. If a refactor ever collapsed one estimator into the other, this fails."""
    result = pt.per_query_estimator(DISAGREEING_SPLIT, DISAGREEING_COUNTS.__getitem__)
    assert result["per_query"] == {"A": 1.0, "B": 0.0}
    assert result["mean_auc"] == pytest.approx(0.5)
    assert result["n_queries"] == 2
    pooled = pt.pooled_estimator(DISAGREEING_SPLIT, DISAGREEING_COUNTS.__getitem__)
    assert result["mean_auc"] != pytest.approx(pooled["auc"])


def test_the_per_query_ci_comes_from_the_between_query_spread():
    """sd of {1.0, 0.0} is sqrt(0.5); the half-width is Z * sd / sqrt(2). Nothing here touches
    Hanley-McNeil, which is defined only for the pooled form."""
    result = pt.per_query_estimator(DISAGREEING_SPLIT, DISAGREEING_COUNTS.__getitem__)
    sd = 0.5 ** 0.5
    assert result["sd"] == pytest.approx(sd)
    half = pt.Z95 * sd / (2 ** 0.5)
    assert result["ci_lo"] == pytest.approx(0.5 - half)
    assert result["ci_hi"] == pytest.approx(0.5 + half)


def test_the_per_query_estimator_counts_single_positive_queries():
    """Most real eligible queries hold exactly one in-pool relevant document, which makes their
    terms single-positive rank statistics; the share of them qualifies how the mean reads."""
    split = {"one": (["p"], ["n"]), "two": (["p1", "p2"], ["n"])}
    values = {"p": 5, "n": 1, "p1": 5, "p2": 6}
    result = pt.per_query_estimator(split, values.__getitem__)
    assert result["single_positive_queries"] == 1
    assert result["n_queries"] == 2


def test_mean_with_ci_has_no_interval_for_a_single_query():
    mean, sd, lo, hi = pt.mean_with_ci([0.7])
    assert mean == 0.7 and sd is None and lo is None and hi is None


# --------------------------------------------------------------------------------------------
# date handling and stratum assignment -- all four classes a document can fall into
# --------------------------------------------------------------------------------------------

DATES = {"dated": "2003-05-17", "edge-lo": "2000-01-01", "edge-hi": "2004-12-31"}
YEARS = {"dated": 2003, "edge-lo": 2000, "edge-hi": 2004, "yearonly": 2011}


def test_date_class_1_publication_date_wins_over_year():
    """A document carrying both must be placed by the finer field."""
    dates, years = {"d": "1998-10-01"}, {"d": 1998}
    assert pt.date_ordinal("d", dates, years) == datetime.date(1998, 10, 1).toordinal()
    assert pt.date_year("d", dates, years) == 1998


def test_date_class_2_year_only_falls_back_to_the_year_midpoint():
    """1 July, so a year-only document orders after the first half and before the second half of its
    own year instead of being forced to either extreme."""
    expected = datetime.date(2011, 7, 1).toordinal()
    assert pt.date_ordinal("yearonly", DATES, YEARS) == expected
    assert pt.date_year("yearonly", DATES, YEARS) == 2011


def test_date_class_3_neither_field_yields_no_date():
    assert pt.date_ordinal("nodate", DATES, YEARS) is None
    assert pt.date_year("nodate", DATES, YEARS) is None


def test_date_class_4_a_document_with_no_count_never_reaches_the_strata():
    """The fourth class is the unresolved document. It is excluded upstream by `observations`, so it
    is absent from `split` and therefore cannot appear in any stratum -- including `no-date`, which
    is for RESOLVED ids lacking a date, not for ids lacking a count."""
    pool = {"q": ["r", "n", "unresolved"]}
    relevant = {"q": {"r"}}
    counts = {"r": 9, "n": 1}  # `unresolved` deliberately absent
    _, split, _ = pt.observations(pool, relevant, counts.__contains__)
    keys, members = pt.build_strata(split, counts, {}, {})
    assert keys == [pt.NO_DATE]
    # Exactly two values are stratified -- the unresolved document contributed none. It has no
    # count, so there is no third value it could have contributed under any date class.
    assert members[pt.NO_DATE] == ([9], [1])
    assert sum(len(pos) + len(neg) for pos, neg in members.values()) == 2


def test_five_year_bins_are_cut_on_the_calendar_not_on_the_data():
    """2000-2004 is one bin: 2000, 2003 and 2004 share it; 2005 starts the next."""
    for iso, expected in (("2000-01-01", 2000), ("2003-05-17", 2000),
                          ("2004-12-31", 2000), ("2005-01-01", 2005)):
        year = pt.date_year("x", {"x": iso}, {})
        assert pt.STRATUM_WIDTH * (year // pt.STRATUM_WIDTH) == expected


def test_empty_bins_inside_the_observed_range_are_enumerated_not_omitted():
    """Observations land only in 1995-1999 and 2010-2014. The 2000s bins hold nothing, and they must
    appear in the table as empty rows rather than vanish from it."""
    split = {"q": (["old"], ["new"])}
    counts, dates = {"old": 3, "new": 1}, {"old": "1996-01-01", "new": "2012-01-01"}
    keys, _ = pt.build_strata(split, counts, dates, {})
    assert keys == [1995, 2000, 2005, 2010, pt.NO_DATE]


def test_the_no_date_stratum_is_always_enumerated_even_when_empty():
    split = {"q": (["a"], ["b"])}
    keys, members = pt.build_strata(split, {"a": 2, "b": 1}, {"a": "2010-01-01", "b": "2010-01-01"}, {})
    assert keys[-1] == pt.NO_DATE
    rows, _, _ = pt.stratum_table(keys, members)
    assert rows[-1]["stratum"] == pt.NO_DATE
    assert rows[-1]["size"] == 0 and rows[-1]["degenerate"] is True


def test_stratum_label_renders_the_closed_five_year_span():
    assert pt.stratum_label(2000) == "2000-2004"
    assert pt.stratum_label(pt.NO_DATE) == pt.NO_DATE


# --------------------------------------------------------------------------------------------
# measurement 4b -- the stratum-pooled Mann-Whitney statistic
# --------------------------------------------------------------------------------------------

def test_4b_sums_within_stratum_pairs_only_and_never_averages_stratum_aucs():
    """Two strata, deliberately unbalanced so the pooled statistic and a mean-of-stratum-AUCs
    disagree:
        1990-1994: pos [9]    vs neg [1]           -> 1.0 concordant of 1 pair
        2010-2014: pos [1, 2] vs neg [3, 4, 5, 6]  -> 0.0 concordant of 8 pairs
    Pooled = (1.0 + 0.0) / (1 + 8) = 1/9 = 0.1111. A mean of the two stratum AUCs would be 0.5 --
    an entirely different number, and the wrong one.

    Note also what is NOT counted: the 1990s positive against the 2010s negatives. Those
    cross-stratum pairs are exactly the age confound that stratifying removes."""
    split = {"q": (["p90", "p10a", "p10b"], ["n90", "n10a", "n10b", "n10c", "n10d"])}
    counts = {"p90": 9, "n90": 1, "p10a": 1, "p10b": 2,
              "n10a": 3, "n10b": 4, "n10c": 5, "n10d": 6}
    dates = {"p90": "1992-01-01", "n90": "1993-01-01",
             "p10a": "2011-01-01", "p10b": "2012-01-01", "n10a": "2010-01-01",
             "n10b": "2011-01-01", "n10c": "2012-01-01", "n10d": "2013-01-01"}
    keys, members = pt.build_strata(split, counts, dates, {})
    rows, total, pairs = pt.stratum_table(keys, members)
    assert pairs == 9 and total == pytest.approx(1.0)
    assert total / pairs == pytest.approx(1.0 / 9.0)
    unstratified = pt.auc([9, 1, 2], [1, 3, 4, 5, 6])
    assert unstratified != pytest.approx(total / pairs)


def test_a_degenerate_stratum_contributes_zero_pairs_and_is_marked_not_fatal():
    """A stratum holding only positives (or only negatives) is ordinary, not exotic -- only 283
    distinct relevant documents exist across the whole corpus. It must be marked, contribute
    nothing, and leave the pooled statistic defined."""
    split = {"q": (["p90", "p10"], ["n10"])}
    counts = {"p90": 5, "p10": 8, "n10": 2}
    dates = {"p90": "1990-01-01", "p10": "2010-01-01", "n10": "2010-01-01"}
    keys, members = pt.build_strata(split, counts, dates, {})
    rows, total, pairs = pt.stratum_table(keys, members)
    by_name = {r["stratum"]: r for r in rows}
    assert by_name["1990-1994"]["degenerate"] is True
    assert by_name["1990-1994"]["pairs"] == 0
    assert by_name["1990-1994"]["auc"] is None
    assert by_name["1990-1994"]["n_pos"] == 1 and by_name["1990-1994"]["n_neg"] == 0
    assert by_name["2010-2014"]["degenerate"] is False
    assert pairs == 1 and total == pytest.approx(1.0)


def test_every_stratum_degenerate_leaves_a_zero_pooled_pair_count():
    """This is the load-bearing sys.exit condition -- not 'some stratum came out empty'. The table
    itself still renders; it is main() that refuses to divide by it."""
    split = {"q": (["p90"], ["n10"])}
    counts = {"p90": 5, "n10": 2}
    dates = {"p90": "1990-01-01", "n10": "2010-01-01"}
    keys, members = pt.build_strata(split, counts, dates, {})
    rows, total, pairs = pt.stratum_table(keys, members)
    assert pairs == 0 and total == 0.0
    assert all(r["degenerate"] for r in rows)


def test_4b_ties_inside_a_stratum_count_one_half():
    split = {"q": (["p"], ["n"])}
    counts, dates = {"p": 4, "n": 4}, {"p": "2010-01-01", "n": "2011-01-01"}
    keys, members = pt.build_strata(split, counts, dates, {})
    _, total, pairs = pt.stratum_table(keys, members)
    assert (total, pairs) == (0.5, 1)


# --------------------------------------------------------------------------------------------
# loaders -- the format assumptions, each a sys.exit rather than a warning
# --------------------------------------------------------------------------------------------

def write(path, text):
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    return str(path)


def test_load_qrels_reads_the_three_column_beir_form(tmp_path):
    path = write(tmp_path / "q.tsv", "query-id\tcorpus-id\tscore\n1\t100\t1\n1\t101\t0\n2\t200\t1\n")
    assert pt.load_qrels(path) == {"1": {"100"}, "2": {"200"}}


def test_load_qrels_refuses_a_file_without_the_beir_header(tmp_path):
    """A headerless file parses fine row-by-row, so nothing else would notice that judgement 1 had
    been silently eaten as a header."""
    path = write(tmp_path / "q.tsv", "1\t100\t1\n2\t200\t1\n")
    with pytest.raises(SystemExit):
        pt.load_qrels(path)


def test_load_qrels_refuses_the_four_column_trec_form(tmp_path):
    path = write(tmp_path / "q.tsv", "query-id\tcorpus-id\tscore\n1\t0\t100\t1\n")
    with pytest.raises(SystemExit):
        pt.load_qrels(path)


def test_load_run_reads_six_column_trec_in_rank_order(tmp_path):
    path = write(tmp_path / "r.trec", "1 Q0 a 1 0.9 tag\n1 Q0 b 2 0.8 tag\n2 Q0 c 1 0.7 tag\n")
    assert pt.load_run(path) == {"1": ["a", "b"], "2": ["c"]}


def test_load_run_refuses_a_duplicate_document_within_one_pool(tmp_path):
    """A pool holding the same document twice would double-weight it in every pair count."""
    path = write(tmp_path / "r.trec", "1 Q0 a 1 0.9 tag\n1 Q0 a 2 0.8 tag\n")
    with pytest.raises(SystemExit):
        pt.load_run(path)


def test_load_counts_refuses_an_empty_resolved_population(tmp_path):
    path = write(tmp_path / "c.json", json.dumps({"counts": {}, "years": {}, "dates": {}, "unresolved": ["x"]}))
    with pytest.raises(SystemExit):
        pt.load_counts(path)


def test_load_counts_refuses_an_id_that_is_both_resolved_and_unresolved(tmp_path):
    """Presence is the whole basis of the include/exclude decision; an id on both lists makes it
    ambiguous."""
    path = write(tmp_path / "c.json", json.dumps(
        {"counts": {"a": 1}, "years": {}, "dates": {}, "unresolved": ["a"]}))
    with pytest.raises(SystemExit):
        pt.load_counts(path)


def test_load_counts_refuses_a_missing_key(tmp_path):
    path = write(tmp_path / "c.json", json.dumps({"counts": {"a": 1}, "years": {}, "dates": {}}))
    with pytest.raises(SystemExit):
        pt.load_counts(path)

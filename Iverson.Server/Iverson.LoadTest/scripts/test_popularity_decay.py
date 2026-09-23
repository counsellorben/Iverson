"""pytest suite for popularity_decay.py's arithmetic (spec
docs/specs/2026-09-13-popularity-signal-measurement-design.md, "Why a decayed-popularity arm is
not in this experiment"). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_popularity_decay.py -q

These pin the ARITHMETIC against hand-computed values, not against the implementation itself --
every fixture below states its expected value arithmetically (day counts worked out by calendar,
fractions written out for the discount curve) so a refactor that quietly changed the formula, the
truncation boundary, or a predicate's membership-vs-truthiness distinction fails here.

Six things are pinned, matching the task brief:
  * ComputeRecencySum equivalence -- a hand-computed multi-bucket example (DecayFieldResolver.cs:
    93-115).
  * The min(1.0, ...) cap actually fires for a future-dated bucket.
  * The 60-bucket TakeLast truncation (PopularitySignalConsumer.cs:123) drops the oldest buckets
    and keeps the newest.
  * The year-only fallback (fetch_citation_dates.py's month_of): a citing paper with `year` but no
    `publicationDate` lands in July of that year. This lives in fetch_citation_dates.py, not this
    module, but popularity_decay.py's correctness depends on that convention holding, so it is
    pinned here as an upstream assumption.
  * RecencyBoost=0 reduces exactly to `count / (count + S)`.
  * The saturation transform (reused from popularity_rerank.py, not reimplemented) and the
    absent/present predicate -- membership in `counts` / `months`, never truthiness.

No non-stdlib imports beyond pytest -- nothing needs PYTHONPATH."""
import json
import math
import os
import sys
from datetime import datetime, timezone

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import popularity_decay as pd  # noqa: E402
import popularity_rerank as pr  # noqa: E402
import fetch_citation_dates as fcd  # noqa: E402


def write(path, text):
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    return str(path)


def write_json(path, obj):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f)
    return str(path)


# --------------------------------------------------------------------------------------------
# compute_recency_sum -- DecayFieldResolver.ComputeRecencySum, hand-computed
# --------------------------------------------------------------------------------------------

def test_compute_recency_sum_multi_bucket_by_hand():
    """now = 2026-09-07 UTC. Two buckets, already truncated/sorted ascending:
        2026-06 count=3 -- bucketStart 2026-06-01. Days to 2026-09-07:
            Jun1->Jul1 30, Jul1->Aug1 31, Aug1->Sep1 31, Sep1->Sep7 6  => 98 days.
        2026-08 count=2 -- bucketStart 2026-08-01. Days to 2026-09-07:
            Aug1->Sep1 31, Sep1->Sep7 6  => 37 days.
    half_life = 90.0.
        expected = 3 * 0.5**(98/90) + 2 * 0.5**(37/90)
    """
    now = datetime(2026, 9, 7, tzinfo=timezone.utc)
    buckets = [("2026-06", 3), ("2026-08", 2)]
    expected = 3 * 0.5 ** (98 / 90) + 2 * 0.5 ** (37 / 90)
    assert pd.compute_recency_sum(buckets, now, 90.0) == pytest.approx(expected)


def test_compute_recency_sum_single_bucket_zero_age_is_full_weight():
    """A bucket dated exactly at `now`'s month start has age 0 -- weight 0.5**0 = 1.0 exactly, so
    the sum is just the raw count, unscaled."""
    now = datetime(2026, 6, 1, tzinfo=timezone.utc)
    assert pd.compute_recency_sum([("2026-06", 7)], now, 180.0) == pytest.approx(7.0)


def test_compute_recency_sum_of_empty_buckets_is_zero():
    now = datetime(2026, 9, 7, tzinfo=timezone.utc)
    assert pd.compute_recency_sum([], now, 180.0) == 0.0


# --------------------------------------------------------------------------------------------
# The min(1.0, ...) cap -- a future-dated bucket must not exceed weight 1
# --------------------------------------------------------------------------------------------

def test_future_dated_bucket_is_capped_at_weight_one_not_amplified():
    """now = 2026-01-01 UTC; bucket 2026-06 is 5 months in the FUTURE. Days from 2026-01-01 to
    2026-06-01: Jan 31 + Feb 28 (2026 not a leap year) + Mar 31 + Apr 30 + May 31 = 151 days, so
    ageDays = now - bucketStart = -151. Uncapped, 0.5**(-151/180) = 2**(151/180) ~= 1.79 > 1 --
    a genuine amplification a naive `0.5**(ageDays/halfLife)` would produce. The min(1.0, ...) cap
    must clip this to exactly 1.0, so count=5 must sum to exactly 5.0, not ~8.95."""
    now = datetime(2026, 1, 1, tzinfo=timezone.utc)
    uncapped = 0.5 ** (-151 / 180)
    assert uncapped > 1.0  # sanity: the cap is actually being exercised by this fixture
    result = pd.compute_recency_sum([("2026-06", 5)], now, 180.0)
    assert result == pytest.approx(5.0)
    assert result != pytest.approx(5.0 * uncapped)


# --------------------------------------------------------------------------------------------
# truncate_buckets -- the 60-bucket TakeLast(60) truncation
# --------------------------------------------------------------------------------------------

def _month_seq(start_year, start_month, n):
    out, y, m = [], start_year, start_month
    for _ in range(n):
        out.append(f"{y:04d}-{m:02d}")
        m += 1
        if m == 13:
            m, y = 1, y + 1
    return out


def test_truncate_buckets_keeps_the_most_recent_60_and_drops_the_oldest():
    months_list = _month_seq(2020, 1, 65)  # 65 consecutive months, oldest first
    months = {m: i + 1 for i, m in enumerate(months_list)}  # insertion order irrelevant -- sorted internally
    kept = pd.truncate_buckets(months)
    assert len(kept) == 60
    kept_months = [m for m, _count in kept]
    assert months_list[0] not in kept_months          # oldest dropped
    assert months_list[4] not in kept_months           # 5th-oldest also dropped (65 - 60 = 5 dropped)
    assert kept_months[0] == months_list[5]            # 6th-oldest is now the oldest kept
    assert kept_months[-1] == months_list[-1]          # newest always kept
    assert kept_months == sorted(kept_months)          # ascending chronological order preserved


def test_truncate_buckets_is_a_noop_at_or_under_60():
    months = {m: 1 for m in _month_seq(2020, 1, 60)}
    kept = pd.truncate_buckets(months)
    assert len(kept) == 60
    assert [m for m, _ in kept] == sorted(months)


def test_truncate_buckets_of_empty_months_is_empty():
    assert pd.truncate_buckets({}) == []


# --------------------------------------------------------------------------------------------
# The year-only fallback (fetch_citation_dates.py's month_of) -- an upstream assumption this
# module's decay computation depends on, pinned here rather than left implicit.
# --------------------------------------------------------------------------------------------

def test_month_of_falls_back_to_july_of_the_year_when_no_publication_date():
    row = {"citingPaper": {"year": 2020}}
    assert fcd.month_of(row) == "2020-07"


def test_month_of_prefers_publication_date_when_present():
    row = {"citingPaper": {"year": 2020, "publicationDate": "2020-11-03"}}
    assert fcd.month_of(row) == "2020-11"


def test_month_of_returns_none_when_neither_form_is_present():
    row = {"citingPaper": {}}
    assert fcd.month_of(row) is None


# --------------------------------------------------------------------------------------------
# effective_count -- RecencyBoost=0 reduces exactly to the lifetime count
# --------------------------------------------------------------------------------------------

def test_effective_count_at_boost_zero_is_exactly_the_count():
    """0.0 * any finite d is exactly 0.0 (IEEE-754), so count + 0.0*d must equal float(count)
    bit-for-bit, for any d including a large one -- this is what makes the RecencyBoost=0 grid
    cell a genuine reproduction of the lifetime-count AUC, not an approximation."""
    assert pd.effective_count(7.0, 0.0, 123.456) == 7.0
    assert pd.effective_count(0.0, 0.0, 999.0) == 0.0  # a resolved zero count stays zero


def test_effective_count_at_boost_zero_reduces_the_full_saturation_pipeline_exactly():
    """effective_count composed with the REUSED popularity_rerank.popularity() must equal
    count/(count+S) exactly at boost=0 -- the full pipeline the spec claims reduces exactly."""
    count, saturation = 18.0, 182.0
    effective = pd.effective_count(count, 0.0, 456.789)
    pop = pr.popularity(effective, saturation)
    assert pop == pytest.approx(count / (count + saturation))


def test_effective_count_with_positive_boost_adds_the_decay_term():
    assert pd.effective_count(10.0, 5.0, 2.0) == pytest.approx(20.0)


# --------------------------------------------------------------------------------------------
# The saturation transform, reused from popularity_rerank.py rather than reimplemented
# --------------------------------------------------------------------------------------------

def test_saturation_transform_via_effective_count_and_reused_popularity():
    """effective = 0 + 5*10 = 50; pop = 50/(50+50) = 0.5 -- hand-computed, exercising the REUSED
    pr.popularity() through the decay module's own effective_count()."""
    effective = pd.effective_count(0.0, 5.0, 10.0)
    assert effective == pytest.approx(50.0)
    assert pr.popularity(effective, 50.0) == pytest.approx(0.5)


# --------------------------------------------------------------------------------------------
# has_decay_data / build_admissible -- membership, never truthiness; the `failed` quirk
# --------------------------------------------------------------------------------------------

def test_has_decay_data_true_for_a_present_but_empty_months_entry():
    """A genuinely-fetched paper with zero citations still has decay data -- membership in
    `months`, not truthiness of the (empty) inner dict."""
    predicate = pd.has_decay_data(months={"a": {}}, failed_set=set())
    assert predicate("a") is True


def test_has_decay_data_false_when_absent_from_months():
    predicate = pd.has_decay_data(months={}, failed_set=set())
    assert predicate("a") is False


def test_has_decay_data_false_when_failed_even_though_months_has_an_empty_entry():
    """fetch_citation_dates.py's `months.setdefault(cid, {})` runs BEFORE the first fetch attempt,
    so a paper that exhausted its backoff ladder on page 0 can still show up in `months` as an
    empty dict. `failed` must override that -- this is the quirk the module docstring flags."""
    predicate = pd.has_decay_data(months={"a": {}}, failed_set={"a"})
    assert predicate("a") is False


def test_build_admissible_requires_count_and_decay_data_and_not_truncated_by_default():
    decay_available = pd.has_decay_data(months={"a": {"2020-01": 1}}, failed_set=set())
    admissible = pd.build_admissible(
        counts={"a": 5}, decay_available=decay_available, truncated_set={"a"}, include_truncated=False)
    assert admissible("a") is False  # truncated, excluded by default


def test_build_admissible_includes_truncated_when_flag_set():
    decay_available = pd.has_decay_data(months={"a": {"2020-01": 1}}, failed_set=set())
    admissible = pd.build_admissible(
        counts={"a": 5}, decay_available=decay_available, truncated_set={"a"}, include_truncated=True)
    assert admissible("a") is True


def test_build_admissible_false_without_a_resolved_count_even_with_decay_data():
    decay_available = pd.has_decay_data(months={"a": {"2020-01": 1}}, failed_set=set())
    admissible = pd.build_admissible(
        counts={}, decay_available=decay_available, truncated_set=set(), include_truncated=False)
    assert admissible("a") is False


def test_build_admissible_a_resolved_zero_count_is_present_not_absent():
    """Membership, not truthiness: count=0 must NOT be treated as absent."""
    decay_available = pd.has_decay_data(months={"a": {"2020-01": 1}}, failed_set=set())
    admissible = pd.build_admissible(
        counts={"a": 0}, decay_available=decay_available, truncated_set=set(), include_truncated=False)
    assert admissible("a") is True


# --------------------------------------------------------------------------------------------
# nDCG@10 -- hand-computed
# --------------------------------------------------------------------------------------------

def test_ndcg_at_10_hand_computed():
    """ranked = [a, b, c, d]; relevant = {b, d}. b at rank 2 -> 1/log2(3); d at rank 4 -> 1/log2(5).
    Ideal order puts both relevant docs first (ranks 1, 2): idcg = 1/log2(2) + 1/log2(3)."""
    ranked = ["a", "b", "c", "d"]
    relevant = {"b", "d"}
    dcg = 1 / math.log2(3) + 1 / math.log2(5)
    idcg = 1 / math.log2(2) + 1 / math.log2(3)
    assert pd.dcg_at_10(ranked, relevant) == pytest.approx(dcg)
    assert pd.ideal_dcg_at_10(relevant) == pytest.approx(idcg)
    assert pd.ndcg_at_10(ranked, relevant) == pytest.approx(dcg / idcg)


def test_ndcg_at_10_perfect_ranking_is_one():
    ranked = ["a", "b", "c"]
    relevant = {"a", "b"}
    assert pd.ndcg_at_10(ranked, relevant) == pytest.approx(1.0)


def test_ndcg_at_10_ignores_documents_past_rank_10():
    """The single relevant document sits at rank 11 -- past the cutoff -- so DCG contributes
    nothing and nDCG is 0.0, not undefined (idcg is still positive: the document IS judged
    relevant, it just wasn't retrieved in the top 10)."""
    ranked = [f"d{i}" for i in range(1, 12)]  # d1..d11
    relevant = {"d11"}
    assert pd.ndcg_at_10(ranked, relevant) == 0.0


def test_ndcg_at_10_returns_none_when_the_query_has_no_relevant_documents():
    assert pd.ndcg_at_10(["a", "b"], set()) is None


# --------------------------------------------------------------------------------------------
# Mandatory assertions -- each a standalone, testable sys.exit
# --------------------------------------------------------------------------------------------

def test_check_has_decay_data_exits_on_empty():
    with pytest.raises(SystemExit):
        pd.check_has_decay_data(set())


def test_check_has_decay_data_passes_and_returns_the_count():
    assert pd.check_has_decay_data({"a", "b"}) == 2


def test_check_eligible_queries_exits_on_empty():
    with pytest.raises(SystemExit):
        pd.check_eligible_queries([])


def test_check_eligible_queries_passes_and_returns_the_count():
    assert pd.check_eligible_queries(["q1", "q2"]) == 2


def test_check_boost_zero_matches_lifetime_passes_when_equal():
    grid = {(0.0, 90.0): {"mean_auc": 0.75}, (0.0, 180.0): {"mean_auc": 0.75}}
    assert pd.check_boost_zero_matches_lifetime(grid, [90.0, 180.0], 0.75) is True


def test_check_boost_zero_matches_lifetime_exits_on_mismatch():
    """The mismatch every mutant of the decay arithmetic should eventually trip: the boost=0 cell
    disagrees with the independently-computed lifetime AUC."""
    grid = {(0.0, 90.0): {"mean_auc": 0.80}}
    with pytest.raises(SystemExit):
        pd.check_boost_zero_matches_lifetime(grid, [90.0], 0.75)


def test_check_ndcg_eligible_queries_exits_on_empty():
    with pytest.raises(SystemExit):
        pd.check_ndcg_eligible_queries([])


def test_check_ndcg_eligible_queries_passes_and_returns_the_count():
    assert pd.check_ndcg_eligible_queries(["q1"]) == 1


# --------------------------------------------------------------------------------------------
# Loaders / validators
# --------------------------------------------------------------------------------------------

def test_load_dates_cache_exits_when_a_required_key_is_missing(tmp_path):
    path = write_json(tmp_path / "d.json", {"months": {}, "truncated": []})  # missing 'failed'
    with pytest.raises(SystemExit):
        pd.load_dates_cache(path)


def test_load_dates_cache_reads_the_expected_shape(tmp_path):
    path = write_json(tmp_path / "d.json", {"months": {"a": {"2020-01": 1}}, "truncated": ["a"], "failed": []})
    cache = pd.load_dates_cache(path)
    assert cache["months"] == {"a": {"2020-01": 1}}
    assert cache["truncated"] == ["a"]
    assert cache["failed"] == []


@pytest.mark.parametrize("value", [0.0, -1.0, 301.0, float("nan"), float("inf")])
def test_validate_half_lives_rejects_outside_the_shipped_bound(value):
    """PopularitySignalOptions.cs's validator rejects RecencyHalfLifeDays outside (0, 300]."""
    with pytest.raises(SystemExit):
        pd.validate_half_lives([value])


def test_validate_half_lives_accepts_the_shipped_default_and_the_upper_bound():
    pd.validate_half_lives([180.0, 300.0])  # must not raise


@pytest.mark.parametrize("value", [-1.0, float("nan"), float("inf")])
def test_validate_boosts_rejects_negative_or_non_finite(value):
    with pytest.raises(SystemExit):
        pd.validate_boosts([value])


def test_validate_boosts_accepts_zero():
    pd.validate_boosts([0.0])  # must not raise -- 0 is the built-in control


@pytest.mark.parametrize("value", [-1.0, float("nan"), float("inf")])
def test_validate_w_grid_rejects_negative_or_non_finite(value):
    with pytest.raises(SystemExit):
        pd.validate_w_grid([value])


def test_validate_w_grid_accepts_zero():
    pd.validate_w_grid([0.0])  # must not raise -- W=0 is the identity control


if __name__ == "__main__":
    sys.exit(pytest.main([__file__, "-q"]))

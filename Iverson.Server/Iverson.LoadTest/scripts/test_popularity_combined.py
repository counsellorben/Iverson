"""pytest suite for popularity_combined.py's arithmetic (spec
docs/specs/2026-09-13-popularity-signal-measurement-design.md). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_popularity_combined.py -q

These pin the ARITHMETIC against hand-computed values, not against the implementation -- every
fixture below states its expected value from the definition (the recent-rate conversion, the log-sum,
the blend, the stratum ratio, the paired t-test, Holm's step-up construction), independently of
whatever expression the implementation happens to use, so a refactor that quietly changed a formula,
swapped a sign, or mis-ordered Holm's ranks fails here.

Seven things are pinned:
  * recent_rate -- D * ln2 * 365.25 / half_life, the unit conversion from ComputeRecencySum's raw
    decayed-citation mass to citations/year;
  * per_year_score -- count / age_years, the reference single correction;
  * c1_score -- log1p(per_year) + log1p(recent_rate), the PRIMARY family's log-sum;
  * c2_score -- per_year + beta*recent_rate, INCLUDING the beta=0 identity that must reproduce
    PER_YEAR exactly (the built-in check main() asserts before printing anything);
  * c3_score -- (recent_rate+1) / (stratum_median+1), the age-controlled ratio;
  * paired -- mean/CI/t/p/up/down/tie on a small hand-worked vector;
  * holm_adjust -- monotone step-up construction, returned in ORIGINAL (not sorted) order, clipped
    at 1.0.

No non-stdlib imports beyond pytest -- nothing needs PYTHONPATH."""
import math
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import popularity_combined as pc  # noqa: E402


# --------------------------------------------------------------------------------------------
# recent_rate -- D * ln2 * 365.25 / half_life
# --------------------------------------------------------------------------------------------

def test_recent_rate_hand_computed():
    """D=3.0, half_life=180.0: 3.0 * ln2 * 365.25 / 180.0."""
    expected = 3.0 * math.log(2) * 365.25 / 180.0
    assert pc.recent_rate(3.0, 180.0) == pytest.approx(expected)
    assert pc.recent_rate(3.0, 180.0) == pytest.approx(4.219533461658667)


def test_recent_rate_of_zero_D_is_zero_at_any_half_life():
    assert pc.recent_rate(0.0, 90.0) == 0.0
    assert pc.recent_rate(0.0, 300.0) == 0.0


# --------------------------------------------------------------------------------------------
# per_year_score -- count / age_years
# --------------------------------------------------------------------------------------------

def test_per_year_score_hand_computed():
    """count=12, age_years=4.0 -> 12/4 = 3.0."""
    assert pc.per_year_score(12, 4.0) == 3.0


def test_per_year_score_non_integer_age():
    """count=7, age_years=3.5 -> 2.0 exactly."""
    assert pc.per_year_score(7, 3.5) == pytest.approx(2.0)


# --------------------------------------------------------------------------------------------
# c1_score -- log1p(per_year) + log1p(recent_rate)
# --------------------------------------------------------------------------------------------

def test_c1_score_hand_computed():
    """per_year=1.5, recent_rate=0.5 -> log1p(1.5) + log1p(0.5)."""
    expected = math.log1p(1.5) + math.log1p(0.5)
    assert pc.c1_score(1.5, 0.5) == pytest.approx(expected)
    assert pc.c1_score(1.5, 0.5) == pytest.approx(1.3217558399823195)


def test_c1_score_of_zero_and_zero_is_zero():
    """log1p(0) + log1p(0) = 0 + 0 = 0."""
    assert pc.c1_score(0.0, 0.0) == 0.0


# --------------------------------------------------------------------------------------------
# c2_score -- per_year + beta*recent_rate, INCLUDING the beta=0 identity
# --------------------------------------------------------------------------------------------

def test_c2_score_hand_computed():
    """per_year=5.0, recent_rate=2.0, beta=3.0 -> 5.0 + 3.0*2.0 = 11.0."""
    assert pc.c2_score(5.0, 2.0, 3.0) == 11.0


def test_c2_score_at_beta_zero_reproduces_per_year_exactly():
    """This is the built-in check main() asserts against the real corpus before printing anything --
    pinned here on hand-picked numbers so a refactor that broke the identity fails independently of
    any real data file."""
    per_year_value = 7.3145
    assert pc.c2_score(per_year_value, 999.0, 0.0) == per_year_value
    assert pc.c2_score(per_year_value, 0.0, 0.0) == per_year_value


# --------------------------------------------------------------------------------------------
# c3_score -- (recent_rate+1) / (stratum_median+1)
# --------------------------------------------------------------------------------------------

def test_c3_score_hand_computed():
    """recent_rate=4.0, stratum_median=1.0 -> (4+1)/(1+1) = 5/2 = 2.5."""
    assert pc.c3_score(4.0, 1.0) == 2.5


def test_c3_score_of_zero_recent_rate_and_zero_median_is_one():
    """(0+1)/(0+1) = 1.0 -- a document exactly at its stratum's (zero) median."""
    assert pc.c3_score(0.0, 0.0) == 1.0


# --------------------------------------------------------------------------------------------
# paired -- mean/CI/t/p/up/down/tie on a small hand-worked vector
# --------------------------------------------------------------------------------------------

def test_paired_hand_worked_vector():
    """a = {q1:1, q2:2, q3:3, q4:4}, b = {q1:3, q2:2, q3:5, q4:4} -> diffs [2, 0, 2, 0].
    mean = 1.0. Deviations from the mean: 1, -1, 1, -1 -> sum of squares 4 -> sample variance
    4/(4-1) = 4/3 -> sd = sqrt(4/3). se = sd/sqrt(4) = sd/2. t = mean/se. p = erfc(|t|/sqrt(2)).
    up: the two positive diffs (2, 2); down: none; tie: the two zero diffs."""
    a = {"q1": 1.0, "q2": 2.0, "q3": 3.0, "q4": 4.0}
    b = {"q1": 3.0, "q2": 2.0, "q3": 5.0, "q4": 4.0}
    r = pc.paired(a, b)
    sd = math.sqrt(4.0 / 3.0)
    se = sd / 2.0
    t = 1.0 / se
    p = math.erfc(abs(t) / math.sqrt(2))
    assert r["mean"] == pytest.approx(1.0)
    assert r["t"] == pytest.approx(t)
    assert r["p"] == pytest.approx(p)
    assert r["lo"] == pytest.approx(1.0 - pc.popularity_triage.Z95 * se)
    assert r["hi"] == pytest.approx(1.0 + pc.popularity_triage.Z95 * se)
    assert r["n"] == 4
    assert r["up"] == 2
    assert r["down"] == 0
    assert r["tie"] == 2


def test_paired_matches_precomputed_constants():
    """Same vector as above, pinned against constants computed independently (not re-derived from
    the same expressions the module uses)."""
    a = {"q1": 1.0, "q2": 2.0, "q3": 3.0, "q4": 4.0}
    b = {"q1": 3.0, "q2": 2.0, "q3": 5.0, "q4": 4.0}
    r = pc.paired(a, b)
    assert r["t"] == pytest.approx(1.7320508075688774)
    assert r["p"] == pytest.approx(0.08326451666355043)
    assert r["lo"] == pytest.approx(-0.13158574300197556)
    assert r["hi"] == pytest.approx(2.131585743001976)


# --------------------------------------------------------------------------------------------
# holm_adjust -- monotone step-up, returned in ORIGINAL order, clipped at 1.0
# --------------------------------------------------------------------------------------------

def test_holm_adjust_hand_worked_three_way():
    """p = [0.01, 0.04, 0.03], m=3. Sorted ascending: 0.01 (rank0, idx0), 0.03 (rank1, idx2),
    0.04 (rank2, idx1).
      rank0: candidate = 3*0.01 = 0.03; running max 0.03  -> adjusted[0] = 0.03
      rank1: candidate = 2*0.03 = 0.06; running max 0.06  -> adjusted[2] = 0.06
      rank2: candidate = 1*0.04 = 0.04; running max stays 0.06 (monotone step-up) -> adjusted[1] = 0.06
    Result in ORIGINAL order [idx0, idx1, idx2]: [0.03, 0.06, 0.06]."""
    result = pc.holm_adjust([0.01, 0.04, 0.03])
    assert result == pytest.approx([0.03, 0.06, 0.06])


def test_holm_adjust_returns_original_order_not_sorted_order():
    """p = [0.5, 0.01], m=2. Sorted ascending: 0.01 (rank0, idx1), 0.5 (rank1, idx0).
      rank0: candidate = 2*0.01 = 0.02 -> adjusted[1] = 0.02
      rank1: candidate = 1*0.5  = 0.5  -> running max max(0.02,0.5)=0.5 -> adjusted[0] = 0.5
    A function that returned SORTED order would give [0.02, 0.5]; the original-order result is
    [0.5, 0.02]."""
    result = pc.holm_adjust([0.5, 0.01])
    assert result == pytest.approx([0.5, 0.02])


def test_holm_adjust_clips_at_one():
    """p = [0.9, 0.9], m=2: candidates 1.8 then 0.9, running max 1.8 throughout -> both clipped to
    1.0."""
    result = pc.holm_adjust([0.9, 0.9])
    assert result == pytest.approx([1.0, 1.0])

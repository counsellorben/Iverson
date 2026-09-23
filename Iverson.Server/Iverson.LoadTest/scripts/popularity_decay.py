#!/usr/bin/env python3
"""Phase 0.5 measurement for the DECAYED relation-popularity signal (spec
docs/specs/2026-09-13-popularity-signal-measurement-design.md, "Why a decayed-popularity arm is
not in this experiment"). Answers whether the shipped time-decayed transform of a citation count
separates relevant from non-relevant documents better than the lifetime count alone, and whether
fusing it into the pipeline improves nDCG@10 over the control run.

The decayed transform is not a new idea -- it is a CONFIGURATION of the signal that already ships.
Reproducing it offline means matching the shipped computation exactly, not improvising a decay
curve of our own:

    ObjectSearchGrpcService.cs:1040-1055 (PopularityFor):
        d         = ComputeRecencySum(series, now, RecencyHalfLifeDays)
        effective = count + RecencyBoost * d
        pop       = effective / (effective + SaturationPoint)

    DecayFieldResolver.cs:93-115 (ComputeRecencySum):
        for each "YYYY-MM:n" bucket in the series:
            bucketStart = first instant of that month, UTC
            ageDays     = (now - bucketStart).TotalDays
            sum        += n * min(1.0, 0.5 ** (ageDays / halfLifeDays))
        -- min(1.0, ...) caps a future-dated bucket at weight 1, never amplifies it. Citing papers
           really do carry future publication dates in this data (see fetch_citation_dates.py), so
           this branch is live, not theoretical.

    PopularitySignalConsumer.cs:123:
        series = buckets.TakeLast(60)   -- the server only ever sees the last 60 monthly buckets,
        chronologically. This script truncates the same way before summing, or it measures a
        signal the server cannot produce.

    PopularitySignalOptions.cs:
        RecencyHalfLifeDays defaults to 180.0; the shipped validator rejects anything outside
        (0, 300] ("a longer half-life would silently lose tail contribution" against the 60-bucket
        cap). A configuration this script would sweep that the server would refuse to run is not a
        measurement of the shipped feature, so --half-lives is validated against the same bound.

The `effective` term's rank order is what AUC depends on (AUC is invariant to any monotonic
transform, and x/(x+S) is monotonic increasing in x for x>=0), so measurement 1 below is computed
directly on `effective`, without needing SaturationPoint at all. The saturation squash only matters
once a fused SCORE is needed -- measurement 2's nDCG sweep -- where popularity_rerank.py's already-
shipped-identity `popularity()`/`fuse()` functions are reused rather than reimplemented, per this
directory's own precedent (popularity_triage.py and popularity_rerank.py already import from each
other).

Coverage honesty: an in-pool document can be resolved (has a lifetime count) without carrying decay
data at all (never fetched, or exhausted its backoff ladder -- `failed` in citation-dates.json), and
a document that DOES carry decay data can have it computed from a PARTIAL, arbitrarily-ordered
subset of its citations (`truncated` -- the endpoint does not order citations by date, so a
truncated paper's D is not trustworthy). By default this script EXCLUDES truncated documents from
the AUC measurement population; --include-truncated flips that. The nDCG rerank step does NOT apply
this exclusion -- the shipped server has no way to know a series is truncated and uses whatever
partial series it was given, so reproducing "what the server would actually do" means the fusion
step uses every available D, truncated or not, falling back to D=0 (ComputeRecencySum's own
missing/absent-series behaviour) for a document with no decay data at all.

A citation-dates.json quirk worth flagging: fetch_citation_dates.py's `months.setdefault(cid, {})`
runs BEFORE the first fetch attempt for a paper, so a paper that exhausts its backoff ladder on page
0 (lands in `failed`) can still have an EMPTY `{}` entry in `months`. Presence in `months` alone is
therefore not sufficient to mean "has decay data" -- this script also excludes anything in `failed`.

Stdlib only.

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/popularity_decay.py \\
        --dates  scratchpad/popularity/citation-dates.json \\
        --counts scratchpad/popularity/citations.json \\
        --run    scifact-2048-2026-09-06/runs/sci-2048.similar.trec \\
        --qrels  /home/ben/iverson-benchmark-data/scifact-full/qrels/test.tsv \\
        --now    2026-09-07 \\
        --out-dir scratchpad/popularity

Writes, into --out-dir:
    popularity-decay.json -- every figure printed below, machine-readable.
"""
import argparse
import json
import math
import os
import sys
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import popularity_triage as pt  # noqa: E402
import popularity_rerank as pr  # noqa: E402

MAX_BUCKETS = 60          # PopularitySignalConsumer.cs:123 -- buckets.TakeLast(60).
SATURATION_DEFAULT = 50.0  # PopularitySignalOptions.cs -- SaturationPoint's shipped default.
HALFLIFE_MAX = 300.0       # PopularitySignalOptions.cs validator -- (0, 300].
NDCG_K = 10


# --------------------------------------------------------------------------------------------
# The shipped computation, reproduced exactly
# --------------------------------------------------------------------------------------------

def truncate_buckets(months):
    """Mirrors `buckets.TakeLast(60)` (PopularitySignalConsumer.cs:123). Monthly buckets sort
    chronologically under plain lexicographic order ("YYYY-MM" is zero-padded), so sorting the
    dict's items and slicing the last MAX_BUCKETS is exactly that truncation. Applied once, before
    any half-life sweep -- truncation does not depend on halfLifeDays. Returns
    [(month_str, count), ...] ascending, at most MAX_BUCKETS long."""
    ordered = sorted(months.items())
    return ordered[-MAX_BUCKETS:] if len(ordered) > MAX_BUCKETS else ordered


def _month_start_utc(month_str):
    try:
        dt = datetime.strptime(month_str, "%Y-%m")
    except ValueError:
        sys.exit(f"citation-dates.json: malformed bucket month {month_str!r}, expected 'YYYY-MM'")
    return dt.replace(tzinfo=timezone.utc)


def compute_recency_sum(buckets, now, half_life_days):
    """DecayFieldResolver.ComputeRecencySum, applied directly to an already-truncated
    [(month_str, count), ...] list instead of round-tripping through the ';'-joined payload
    string the server stores it as -- offline we already hold the parsed structure. bucketStart is
    the first instant of the month, UTC; ageDays is measured from there; weight is
    min(1.0, 0.5**(ageDays/halfLifeDays)) -- capped, never amplified, so a future-dated bucket
    counts at most as if published today rather than exceeding weight 1."""
    total = 0.0
    for month_str, count in buckets:
        bucket_start = _month_start_utc(month_str)
        age_days = (now - bucket_start).total_seconds() / 86400.0
        weight = min(1.0, 0.5 ** (age_days / half_life_days))
        total += count * weight
    return total


def effective_count(count, boost, d):
    """PopularityFor's `effective = count + RecencyBoost * d`. At boost=0 this is exactly `count`
    (float(count) + 0.0, which is float(count) bit-for-bit for any finite d), which is what makes
    the RecencyBoost=0 grid cell a genuine reproduction of the lifetime-count AUC rather than an
    approximation of it."""
    return count + boost * d


# --------------------------------------------------------------------------------------------
# Coverage / admissibility
# --------------------------------------------------------------------------------------------

def has_decay_data(months, failed_set):
    """Presence is membership in `months` AND absence from `failed` -- never truthiness, and never
    `months` membership alone. `failed` overrides an empty `{}` entry that
    fetch_citation_dates.py's `months.setdefault(cid, {})` can leave behind for a paper that
    exhausted its backoff ladder on the very first page (see module docstring)."""
    return lambda doc_id: doc_id in months and doc_id not in failed_set


def build_admissible(counts, decay_available, truncated_set, include_truncated):
    """The AUC measurement's admissibility predicate: a resolved lifetime count (membership in
    `counts`, never truthiness -- a genuine zero count is data, not absence) AND decay data AND,
    unless --include-truncated, not a truncated series (module docstring: a truncated paper's D is
    computed from a partial, arbitrarily-ordered subset and is not trustworthy)."""
    def predicate(doc_id):
        if doc_id not in counts:
            return False
        if not decay_available(doc_id):
            return False
        if not include_truncated and doc_id in truncated_set:
            return False
        return True
    return predicate


def build_decay_index(months, doc_ids, half_lives, now):
    """{doc_id: {half_life: D}} for every doc_id in `doc_ids`. Each doc's raw month buckets are
    truncated ONCE (truncation is half-life independent) and then summed once per half-life."""
    index = {}
    for doc_id in doc_ids:
        buckets = truncate_buckets(months[doc_id])
        index[doc_id] = {hl: compute_recency_sum(buckets, now, hl) for hl in half_lives}
    return index


# --------------------------------------------------------------------------------------------
# Mandatory non-zero assertions -- sys.exit, never a printed warning (beta_invariant.py's
# discipline: a check that silently asserts nothing over its data is indistinguishable from one
# that passed). Each is a standalone function so a test can call it directly, matching
# popularity_rerank.py's check_order_changed / check_absent_unchanged precedent.
# --------------------------------------------------------------------------------------------

def check_has_decay_data(has_decay_data_ids):
    """Zero documents carrying decay data means the whole measurement has nothing to compute --
    this is the population feeding both the AUC grid and the nDCG rerank fusion."""
    if not has_decay_data_ids:
        sys.exit("[popularity_decay] zero documents carry decay data -- nothing to measure")
    return len(has_decay_data_ids)


def check_eligible_queries(eligible):
    """Zero eligible queries means every per-query AUC in the grid would be undefined -- the same
    failure mode popularity_triage.py guards against for its own eligibility predicate."""
    if not eligible:
        sys.exit("[popularity_decay] no query has >=1 relevant AND >=1 non-relevant decay-admissible "
                  "document in its pool -- the AUC is undefined over every pool")
    return len(eligible)


def check_boost_zero_matches_lifetime(grid, half_lives, lifetime_mean_auc):
    """RecencyBoost=0 must reproduce the lifetime-count AUC EXACTLY, at every half-life in the
    grid: effective = count + 0*d is float(count) bit-for-bit for any finite d (0.0 times any
    finite non-negative number is exactly 0.0), so the two AUC computations run over numerically
    identical inputs. A mismatch here means the decay arithmetic is wrong even at its degenerate
    (inert) setting -- the built-in control this whole script is pinned against."""
    for hl in half_lives:
        cell = grid[(0.0, hl)]
        if not math.isclose(cell["mean_auc"], lifetime_mean_auc, rel_tol=1e-9, abs_tol=1e-12):
            sys.exit(
                "[popularity_decay] RecencyBoost=0 FAILED to reproduce the lifetime-count AUC "
                f"at half-life={hl}: got {cell['mean_auc']!r}, expected {lifetime_mean_auc!r} "
                "-- effective = count + 0*d must equal count exactly")
    return True


def check_ndcg_eligible_queries(ndcg_queries):
    """Zero queries with a judged-relevant document means nDCG@10 is undefined everywhere in the
    sweep -- nothing downstream of this check can be trusted."""
    if not ndcg_queries:
        sys.exit("[popularity_decay] zero queries have a judged-relevant document -- "
                  "nDCG@10 is undefined everywhere")
    return len(ndcg_queries)


# --------------------------------------------------------------------------------------------
# nDCG@10 -- binary relevance (this qrels file's judgements are all score=1; see report), stdlib
# --------------------------------------------------------------------------------------------

def dcg_at_10(ranked_doc_ids, relevant_set):
    total = 0.0
    for i, doc_id in enumerate(ranked_doc_ids[:NDCG_K], start=1):
        if doc_id in relevant_set:
            total += 1.0 / math.log2(i + 1)
    return total


def ideal_dcg_at_10(relevant_set):
    n = min(len(relevant_set), NDCG_K)
    return sum(1.0 / math.log2(i + 1) for i in range(1, n + 1))


def ndcg_at_10(ranked_doc_ids, relevant_set):
    """None when the query has zero judged-relevant documents at all (IDCG undefined) -- distinct
    from a query whose relevant documents simply weren't retrieved, which scores a legitimate 0.0
    (DCG=0 over a positive IDCG)."""
    idcg = ideal_dcg_at_10(relevant_set)
    if idcg == 0.0:
        return None
    return dcg_at_10(ranked_doc_ids, relevant_set) / idcg


# --------------------------------------------------------------------------------------------
# Validation
# --------------------------------------------------------------------------------------------

def validate_half_lives(half_lives):
    for hl in half_lives:
        if not math.isfinite(hl) or hl <= 0 or hl > HALFLIFE_MAX:
            sys.exit(
                f"--half-lives {hl!r} is outside (0, {HALFLIFE_MAX}] -- "
                "PopularitySignalOptions.cs's validator would refuse this RecencyHalfLifeDays at "
                "startup; a configuration the server cannot run is not a measurement of the "
                "shipped feature")


def validate_boosts(boosts):
    for b in boosts:
        if not math.isfinite(b) or b < 0:
            sys.exit(
                f"--recency-boosts {b!r} is invalid -- PopularitySignalOptions.cs's validator "
                "requires RecencyBoost finite and non-negative")


def validate_w_grid(w_grid):
    for w in w_grid:
        if not math.isfinite(w) or w < 0:
            sys.exit(f"--w-grid {w!r} is invalid -- W must be finite and non-negative (0 is the "
                      "built-in identity control)")


def load_dates_cache(path):
    with open(path, encoding="utf-8") as f:
        cache = json.load(f)
    for key in ("months", "truncated", "failed"):
        if key not in cache:
            sys.exit(f"{path}: missing required key {key!r}; expected months, truncated, failed "
                      "(fetch_citation_dates.py's cache shape)")
    return cache


# --------------------------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------------------------

def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dates", required=True, help="citation-dates.json, written by fetch_citation_dates.py")
    ap.add_argument("--counts", required=True, help="citations.json: {counts, years, dates, unresolved}")
    ap.add_argument("--run", required=True, help="the captured 6-column TREC run (control)")
    ap.add_argument("--qrels", required=True, help="BEIR 3-column header-bearing qrels TSV")
    ap.add_argument("--now", default="2026-09-07",
                     help="reference instant for decay, YYYY-MM-DD, taken as 00:00 UTC that day "
                          "(default: sci-2048's recordedAtUtc date -- the run's own clock, not today's)")
    ap.add_argument("--out-dir", required=True, help="directory popularity-decay.json is written to")
    ap.add_argument("--saturation", type=float, default=SATURATION_DEFAULT,
                     help=f"SaturationPoint (default {SATURATION_DEFAULT}, the shipped default)")
    ap.add_argument("--recency-boosts", type=float, nargs="+",
                     default=[0.0, 1.0, 2.0, 5.0, 10.0, 25.0, 100.0], help="RecencyBoost grid")
    ap.add_argument("--half-lives", type=float, nargs="+",
                     default=[30.0, 90.0, 180.0, 300.0], help="RecencyHalfLifeDays grid")
    ap.add_argument("--w-grid", type=float, nargs="+",
                     default=[0.0, 0.001, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.4, 1.0],
                     help="WPopularity grid for the nDCG@10 sweep at the best decay configuration")
    ap.add_argument("--include-truncated", action="store_true",
                     help="include documents whose citation list was cut off by the API's offset "
                          "ceiling in the AUC measurement population (default: excluded, since "
                          "their D is computed from a partial, arbitrarily-ordered subset)")
    return ap


def main():
    args = build_arg_parser().parse_args()

    pr.validate_positive_finite(args.saturation, "--saturation")
    validate_half_lives(args.half_lives)
    validate_boosts(args.recency_boosts)
    validate_w_grid(args.w_grid)
    boosts = sorted(set(args.recency_boosts) | {0.0})  # 0.0 is the mandatory control cell
    half_lives = sorted(set(args.half_lives))
    w_grid = sorted(set(args.w_grid))

    try:
        now = datetime.strptime(args.now, "%Y-%m-%d").replace(tzinfo=timezone.utc)
    except ValueError:
        sys.exit(f"--now {args.now!r} is not YYYY-MM-DD")

    dates_cache = load_dates_cache(args.dates)
    months = dates_cache["months"]
    failed_set = set(dates_cache["failed"])
    truncated_set = set(dates_cache["truncated"])

    counts, _years, _dates, _unresolved = pt.load_counts(args.counts)
    relevant = pt.load_qrels(args.qrels)
    run_rows = pr.load_run(args.run)  # {queryId: [(docId, score, tag), ...]} in rank order
    pool = {q: [doc_id for doc_id, _score, _tag in rows] for q, rows in run_rows.items()}

    decay_available = has_decay_data(months, failed_set)

    # -- coverage honesty ----------------------------------------------------------------------
    in_pool_docs = set(doc_id for docs in pool.values() for doc_id in docs)
    in_pool_with_count = {d for d in in_pool_docs if d in counts}
    has_decay_data_ids = {d for d in counts if decay_available(d)}
    with_decay_in_pool = {d for d in in_pool_with_count if d in has_decay_data_ids}
    truncated_in_pool = {d for d in with_decay_in_pool if d in truncated_set}
    failed_in_pool = {d for d in in_pool_with_count if d in failed_set}

    print("[popularity_decay] coverage")
    print(f"  in-pool documents                 {len(in_pool_docs)}")
    print(f"  in-pool with resolved count        {len(in_pool_with_count)}")
    print(f"  in-pool with decay data            {len(with_decay_in_pool)}")
    print(f"  in-pool decay data TRUNCATED       {len(truncated_in_pool)}  "
          f"({'INCLUDED IN' if args.include_truncated else 'EXCLUDED FROM'} the AUC measurement population)")
    print(f"  in-pool FAILED (exhausted ladder)  {len(failed_in_pool)}")

    check_has_decay_data(has_decay_data_ids)

    # -- admissible population + eligible queries -----------------------------------------------
    admissible = build_admissible(counts, decay_available, truncated_set, args.include_truncated)
    eligible, split, dropped = pt.observations(pool, relevant, admissible)
    print(f"\n[popularity_decay] population")
    print(f"  queries in run                     {len(pool)}")
    print(f"  in-pool observations excluded      {dropped}  (no count, no decay data, or truncated-and-excluded)")
    print(f"  eligible queries (decay-admissible) {len(eligible)}")

    check_eligible_queries(eligible)

    admissible_doc_ids = {d for pos, neg in split.values() for d in pos + neg}
    decay_index = build_decay_index(months, has_decay_data_ids, half_lives, now)

    # -- measurement 1: pool-matched AUC grid ---------------------------------------------------
    lifetime_value_of = lambda d: float(counts[d])  # noqa: E731
    lifetime = pt.per_query_estimator(split, lifetime_value_of)

    grid = {}
    for boost in boosts:
        for hl in half_lives:
            value_of = lambda d, boost=boost, hl=hl: effective_count(  # noqa: E731
                float(counts[d]), boost, decay_index[d][hl])
            grid[(boost, hl)] = pt.per_query_estimator(split, value_of)

    print("\n[popularity_decay] measurement 1 -- pool-matched AUC (mean of per-query AUCs)")
    print(f"  lifetime count   mean AUC {lifetime['mean_auc']:.4f}  "
          f"95% CI [{lifetime['ci_lo']:.4f}, {lifetime['ci_hi']:.4f}]  over {lifetime['n_queries']} queries")
    print(f"  {'boost':>7} {'half-life':>10} {'mean AUC':>9} {'95% CI':>19} {'n':>5}")
    for (boost, hl) in sorted(grid):
        cell = grid[(boost, hl)]
        ci = f"[{cell['ci_lo']:.4f}, {cell['ci_hi']:.4f}]" if cell["ci_lo"] is not None else "(n<2, no CI)"
        print(f"  {boost:>7g} {hl:>10g} {cell['mean_auc']:>9.4f} {ci:>19} {cell['n_queries']:>5}")

    # -- mandatory assertion: RecencyBoost=0 reproduces the lifetime AUC exactly ----------------
    check_boost_zero_matches_lifetime(grid, half_lives, lifetime["mean_auc"])
    print(f"\n[popularity_decay] RecencyBoost=0 control PASSED: reproduces lifetime AUC "
          f"{lifetime['mean_auc']:.4f} exactly, at every half-life in the grid")

    # -- best decay configuration + paired comparison against the lifetime count ----------------
    decay_cells = {k: v for k, v in grid.items() if k[0] > 0.0}
    best_key = max(decay_cells, key=lambda k: (decay_cells[k]["mean_auc"], -k[0], -k[1]))
    best = decay_cells[best_key]
    best_boost, best_hl = best_key

    common_queries = sorted(set(best["per_query"]) & set(lifetime["per_query"]))
    deltas = [best["per_query"][q] - lifetime["per_query"][q] for q in common_queries]
    d_mean, d_sd, d_lo, d_hi = pt.mean_with_ci(deltas)
    n = len(deltas)
    t_stat = (d_mean / (d_sd / math.sqrt(n))) if (d_sd not in (None, 0.0) and n >= 2) else None
    improved = sum(1 for d in deltas if d > 0)
    worsened = sum(1 for d in deltas if d < 0)
    tied = sum(1 for d in deltas if d == 0)

    print(f"\n[popularity_decay] best decay configuration: RecencyBoost={best_boost:g} "
          f"RecencyHalfLifeDays={best_hl:g}  mean AUC {best['mean_auc']:.4f}")
    print("[popularity_decay] measurement 2 -- paired per-query AUC delta (decay - lifetime)")
    print(f"  n queries        {n}")
    print(f"  mean delta       {d_mean:.4f}")
    if d_lo is not None:
        print(f"  95% CI           [{d_lo:.4f}, {d_hi:.4f}]")
    print(f"  t statistic      {t_stat:.4f}" if t_stat is not None else "  t statistic      (undefined, n<2 or sd=0)")
    print(f"  improved/worsened/tied   {improved} / {worsened} / {tied}")

    # -- measurement 3: nDCG@10 sweep at the best decay configuration ---------------------------
    effective_counts = {
        doc_id: effective_count(float(count), best_boost, decay_index.get(doc_id, {}).get(best_hl, 0.0))
        for doc_id, count in counts.items()
        if doc_id in in_pool_docs
    }

    ndcg_queries = sorted(q for q in run_rows if relevant.get(q))
    check_ndcg_eligible_queries(ndcg_queries)

    baseline_order = {
        q: [d for d, _s, _t in sorted(run_rows[q], key=lambda row: -row[1])] for q in ndcg_queries
    }
    baseline_ndcg = {q: ndcg_at_10(baseline_order[q], relevant[q]) for q in ndcg_queries}

    print(f"\n[popularity_decay] measurement 3 -- nDCG@10 sweep, W grid, at "
          f"RecencyBoost={best_boost:g} RecencyHalfLifeDays={best_hl:g} SaturationPoint={args.saturation:g}")
    print(f"  {len(ndcg_queries)} nDCG-eligible queries (>=1 judged-relevant document)")
    ndcg_results = {}
    print(f"  {'W':>7} {'mean delta':>11} {'95% CI':>21} {'n':>5}")
    for w in w_grid:
        reranked_pool = {}
        for q, rows in run_rows.items():
            reranked, _absent = pr.rerank_query(rows, effective_counts, set(), w, args.saturation)
            reranked_pool[q] = reranked
        new_order = {q: [d for d, _s, _t in reranked_pool[q]] for q in ndcg_queries}
        new_ndcg = {q: ndcg_at_10(new_order[q], relevant[q]) for q in ndcg_queries}
        w_deltas = [new_ndcg[q] - baseline_ndcg[q] for q in ndcg_queries]
        w_mean, w_sd, w_lo, w_hi = pt.mean_with_ci(w_deltas)
        ci = f"[{w_lo:.5f}, {w_hi:.5f}]" if w_lo is not None else "(n<2, no CI)"
        print(f"  {w:>7g} {w_mean:>11.5f} {ci:>21} {len(w_deltas):>5}")
        ndcg_results[w] = {
            "mean_delta": w_mean, "sd": w_sd, "ci_lo": w_lo, "ci_hi": w_hi,
            "n_queries": len(w_deltas), "per_query_delta": dict(zip(ndcg_queries, w_deltas)),
        }

    # -- sidecar --------------------------------------------------------------------------------
    payload = {
        "inputs": {"dates": os.path.abspath(args.dates), "counts": os.path.abspath(args.counts),
                   "run": os.path.abspath(args.run), "qrels": os.path.abspath(args.qrels),
                   "now": args.now, "saturation": args.saturation,
                   "include_truncated": args.include_truncated},
        "coverage": {
            "in_pool_documents": len(in_pool_docs), "in_pool_with_count": len(in_pool_with_count),
            "in_pool_with_decay_data": len(with_decay_in_pool),
            "in_pool_truncated": len(truncated_in_pool), "in_pool_failed": len(failed_in_pool),
        },
        "population": {"queries_in_run": len(pool), "observations_excluded": dropped,
                       "eligible_queries": len(eligible)},
        "measurement_1": {
            "lifetime": {k: v for k, v in lifetime.items() if k != "per_query"},
            "grid": [
                {"recency_boost": b, "recency_half_life_days": hl,
                 **{k: v for k, v in grid[(b, hl)].items() if k != "per_query"}}
                for (b, hl) in sorted(grid)
            ],
        },
        "best_decay_configuration": {"recency_boost": best_boost, "recency_half_life_days": best_hl,
                                      "mean_auc": best["mean_auc"]},
        "measurement_2": {"n_queries": n, "mean_delta": d_mean, "sd": d_sd, "ci_lo": d_lo, "ci_hi": d_hi,
                          "t_statistic": t_stat, "improved": improved, "worsened": worsened, "tied": tied},
        "measurement_3": {"w_grid": {str(w): {k: v for k, v in res.items() if k != "per_query_delta"}
                                     for w, res in ndcg_results.items()}},
    }
    os.makedirs(args.out_dir, exist_ok=True)
    out_path = os.path.join(args.out_dir, "popularity-decay.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2, sort_keys=True)
    print(f"\n[popularity_decay] wrote {out_path}")


if __name__ == "__main__":
    main()

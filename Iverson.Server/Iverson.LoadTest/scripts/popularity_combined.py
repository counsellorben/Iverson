#!/usr/bin/env python3
"""EXPLORATORY (spec docs/specs/2026-09-13-popularity-signal-measurement-design.md): does combining
the two corrections -- document-age normalisation and citation-recency decay -- separate relevant
from non-relevant documents, or improve ranking, better than either correction alone?

Pre-declared BEFORE any result was seen (2026-09-15):
  PRIMARY  C1@180  log1p(per_year) + log1p(recent_rate), half-life 180 d (shipped default).
           Primary question: does C1@180 beat PER_YEAR (the best single correction) on paired
           pool-matched AUC?  Secondary: does it beat LIFETIME?  And does any W improve nDCG@10?
  EXPLORATORY (Holm-adjusted across the combined families, best cell selected -> optimistic):
           C1 at half-lives 90/300; C2 blend per_year + beta*recent_rate; C3 age-controlled rate.
References: LIFETIME count; PER_YEAR = count / max(age_years, 0.5); RECENT = recent_rate@180.

recent_rate = D * ln2 * 365.25 / half_life, D = the shipped ComputeRecencySum over TakeLast(60) monthly
buckets (reused from popularity_decay.py, which is unit-tested against DecayFieldResolver.cs). Converting
D to citations/year makes it unit-coherent with per_year, which the log-sum and the blend require.
C3 = (recent_rate + 1) / (median recent_rate of the doc's 5-year publication stratum + 1).

Population: in-pool docs with a resolved count, a publication date (publicationDate or year), a fetched
citation-date history, and NOT truncated (a truncated paper's D is from an arbitrary 9,000-row slice).
Every score is evaluated on the SAME observations, so the per-query comparisons are exactly paired.
None of the combined transforms is expressible by the shipped server.

Stdlib only.

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/popularity_combined.py \\
        --run     scifact-2048-2026-09-06/runs/sci-2048.similar.trec \\
        --qrels   /home/ben/iverson-benchmark-data/scifact-full/qrels/test.tsv \\
        --counts  scratchpad/popularity/citations.json \\
        --dates   scratchpad/popularity/citation-dates.json \\
        --now     2026-09-07 \\
        --out-dir scratchpad/popularity/combined

Writes, into --out-dir:
    popularity-combined.json -- every figure printed below, machine-readable.
"""
import argparse
import json
import math
import os
import statistics
import sys
from datetime import datetime, timezone

import popularity_triage
import popularity_decay
import popularity_rerank

HALF_LIVES = (90.0, 180.0, 300.0)
BETAS = (0.0, 0.25, 0.5, 1.0, 2.0, 4.0)
W_GRID = (0.0, 0.001, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.4)
LN2 = math.log(2)


# --------------------------------------------------------------------------------------------
# Pure arithmetic -- pinned by test_popularity_combined.py against hand-computed values
# --------------------------------------------------------------------------------------------

def recent_rate(d, half_life_days):
    """Converts the shipped ComputeRecencySum output D (raw decayed-citation mass, reused from
    popularity_decay.py) into citations/year: D * ln2 * 365.25 / half_life. This is what makes the
    result unit-coherent with PER_YEAR (citations/year), which the C1 log-sum and the C2 blend both
    require -- combining two quantities in different units would not be a meaningful transform."""
    return d * LN2 * 365.25 / half_life_days


def per_year_score(count, age_years):
    """The reference single correction: lifetime count divided by document age in years."""
    return count / age_years


def c1_score(per_year_value, recent_rate_value):
    """PRIMARY family: log1p(per_year) + log1p(recent_rate) -- a log-sum so neither term's raw
    magnitude dominates the other."""
    return math.log1p(per_year_value) + math.log1p(recent_rate_value)


def c2_score(per_year_value, recent_rate_value, beta):
    """EXPLORATORY family: per_year + beta*recent_rate. At beta=0.0 this is per_year_value + 0.0,
    which reproduces PER_YEAR exactly -- the built-in check `main()` asserts before printing
    anything."""
    return per_year_value + beta * recent_rate_value


def c3_score(recent_rate_value, stratum_median):
    """EXPLORATORY family: (recent_rate + 1) / (stratum median recent_rate + 1) -- an age-controlled
    ratio, so a document is compared against the recency rate typical of its own 5-year publication
    stratum rather than against the whole corpus."""
    return (recent_rate_value + 1.0) / (stratum_median + 1.0)


def paired(a, b):
    """Per-query b - a over the shared query keys: mean, 95% CI (between-query spread), a one-sample
    t-test against 0, and the up/down/tie sign counts."""
    d = [b[q] - a[q] for q in a]
    m = statistics.mean(d)
    sd = statistics.stdev(d)
    se = sd / math.sqrt(len(d))
    t = m / se if se > 0 else float("nan")
    p = math.erfc(abs(t) / math.sqrt(2)) if se > 0 else float("nan")
    return dict(mean=m, lo=m - popularity_triage.Z95 * se, hi=m + popularity_triage.Z95 * se, t=t, p=p,
                n=len(d), up=sum(x > 0 for x in d), down=sum(x < 0 for x in d), tie=sum(x == 0 for x in d))


def holm_adjust(pvalues):
    """Holm-Bonferroni step-down correction (report.py's holm_adjust construction): sort ascending,
    adjust each by (family size - rank), take the running maximum so the sequence stays
    non-decreasing, clip at 1.0. Returns adjusted p-values in the SAME order as the input list."""
    m = len(pvalues)
    order = sorted(range(m), key=lambda i: pvalues[i])
    adjusted = [0.0] * m
    running_max = 0.0
    for rank, idx in enumerate(order):
        candidate = (m - rank) * pvalues[idx]
        running_max = max(running_max, candidate)
        adjusted[idx] = min(running_max, 1.0)
    return adjusted


def fmt(r):
    return (f"{r['mean']:+.4f} [{r['lo']:+.4f}, {r['hi']:+.4f}] t={r['t']:5.2f} p={r['p']:.4f} "
            f"n={r['n']} up/down/tie {r['up']}/{r['down']}/{r['tie']}")


# --------------------------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------------------------

def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run", required=True, help="the captured 6-column TREC run")
    ap.add_argument("--qrels", required=True, help="BEIR 3-column header-bearing qrels TSV")
    ap.add_argument("--counts", required=True, help="citations.json: {counts, years, dates, unresolved}")
    ap.add_argument("--dates", required=True, help="citation-dates.json, written by fetch_citation_dates.py")
    ap.add_argument("--now", default="2026-09-07",
                     help="reference instant, YYYY-MM-DD, taken as 00:00 UTC that day "
                          "(default: sci-2048's recordedAtUtc date -- the run's own clock)")
    ap.add_argument("--out-dir", required=True, help="directory popularity-combined.json is written to")
    return ap


def main():
    args = build_arg_parser().parse_args()
    try:
        now = datetime.strptime(args.now, "%Y-%m-%d").replace(tzinfo=timezone.utc)
    except ValueError:
        sys.exit(f"--now {args.now!r} is not YYYY-MM-DD")
    now_d = now.date()

    relevant = popularity_triage.load_qrels(args.qrels)
    pool = popularity_triage.load_run(args.run)
    counts, years, dates, _ = popularity_triage.load_counts(args.counts)
    cache = popularity_decay.load_dates_cache(args.dates)
    truncated, failed = set(cache["truncated"]), set(cache["failed"])

    in_pool = {d for docs in pool.values() for d in docs}
    adm = {d for d in in_pool if d in counts and popularity_triage.date_ordinal(d, dates, years) is not None
           and d in cache["months"] and d not in failed and d not in truncated}
    if not adm:
        sys.exit("no admissible documents")

    age = {d: max((now_d.toordinal() - popularity_triage.date_ordinal(d, dates, years)) / 365.25, 0.5)
           for d in adm}
    buckets = {d: popularity_decay.truncate_buckets(cache["months"][d]) for d in adm}
    rate = {h: {d: recent_rate(popularity_decay.compute_recency_sum(buckets[d], now, h), h) for d in adm}
            for h in HALF_LIVES}
    per_year = {d: per_year_score(counts[d], age[d]) for d in adm}
    stratum = {d: popularity_triage.STRATUM_WIDTH
               * (popularity_triage.date_year(d, dates, years) // popularity_triage.STRATUM_WIDTH)
               for d in adm}

    scores = {"LIFETIME": {d: float(counts[d]) for d in adm}, "PER_YEAR": per_year, "RECENT@180": rate[180.0]}
    for h in HALF_LIVES:
        r = rate[h]
        scores[f"C1@{int(h)}"] = {d: c1_score(per_year[d], r[d]) for d in adm}
        for b in BETAS:
            scores[f"C2@{int(h)} b={b}"] = {d: c2_score(per_year[d], r[d], b) for d in adm}
        med = {}
        for s in set(stratum.values()):
            med[s] = statistics.median(r[d] for d in adm if stratum[d] == s)
        scores[f"C3@{int(h)}"] = {d: c3_score(r[d], med[stratum[d]]) for d in adm}

    # --- per-query pool-matched AUC on identical observations ---
    eligible = []
    for q, docs in pool.items():
        pos = [d for d in docs if d in adm and d in relevant.get(q, set())]
        neg = [d for d in docs if d in adm and d not in relevant.get(q, set())]
        if pos and neg:
            eligible.append((q, pos, neg))
    if not eligible:
        sys.exit("no eligible queries")
    perq = {name: {q: popularity_triage.auc([s[d] for d in pos], [s[d] for d in neg]) for q, pos, neg in eligible}
            for name, s in scores.items()}
    mean_auc = {name: statistics.mean(v.values()) for name, v in perq.items()}

    for h in HALF_LIVES:   # built-in check: beta = 0 must reproduce PER_YEAR exactly
        if perq[f"C2@{int(h)} b=0.0"] != perq["PER_YEAR"]:
            sys.exit(f"CHECK FAILED: C2@{int(h)} b=0 does not reproduce PER_YEAR")
    print(f"population: {len(adm)} admissible in-pool docs ({len(truncated & in_pool)} truncated excluded), "
          f"{len(eligible)} eligible queries\nCHECK passed: C2 beta=0 reproduces PER_YEAR at every half-life\n")

    print("pool-matched AUC (mean of per-query AUCs, identical observations)")
    auc_table_names = (["LIFETIME", "PER_YEAR", "RECENT@180"] + [f"C1@{int(h)}" for h in HALF_LIVES]
                        + [f"C3@{int(h)}" for h in HALF_LIVES])
    for name in auc_table_names:
        m, sd, lo, hi = popularity_triage.mean_with_ci(list(perq[name].values()))
        print(f"  {name:12s} {m:.4f} [{lo:.4f}, {hi:.4f}]")
    c2best = max((n for n in scores if n.startswith("C2@") and not n.endswith("b=0.0")), key=lambda n: mean_auc[n])
    print(f"  {'best C2':12s} {mean_auc[c2best]:.4f}   ({c2best}, selected from {len(HALF_LIVES)*(len(BETAS)-1)} cells)")

    print("\nPRIMARY (pre-declared): C1@180")
    pr_py = paired(perq["PER_YEAR"], perq["C1@180"])
    pr_lt = paired(perq["LIFETIME"], perq["C1@180"])
    print(f"  vs PER_YEAR : {fmt(pr_py)}")
    print(f"  vs LIFETIME : {fmt(pr_lt)}")
    print("\nreferences")
    ref_py_lt = paired(perq["LIFETIME"], perq["PER_YEAR"])
    ref_re_lt = paired(perq["LIFETIME"], perq["RECENT@180"])
    print(f"  PER_YEAR   vs LIFETIME : {fmt(ref_py_lt)}")
    print(f"  RECENT@180 vs LIFETIME : {fmt(ref_re_lt)}")

    c1best = max((f"C1@{int(h)}" for h in HALF_LIVES), key=lambda n: mean_auc[n])
    c3best = max((f"C3@{int(h)}" for h in HALF_LIVES), key=lambda n: mean_auc[n])
    fam = [(c1best, paired(perq["PER_YEAR"], perq[c1best])), (c2best, paired(perq["PER_YEAR"], perq[c2best])),
           (c3best, paired(perq["PER_YEAR"], perq[c3best]))]
    adj = holm_adjust([r["p"] for _, r in fam])
    print("\nEXPLORATORY vs PER_YEAR (best cell per family, Holm-adjusted across the 3 families)")
    for (name, r), a in zip(fam, adj):
        print(f"  {name:14s} {fmt(r)}  holm_p={a:.4f}")

    # --- nDCG@10 sweep: fuse x/(x+S), S = median over admissible docs; non-admissible docs unchanged ---
    rows = popularity_rerank.load_run(args.run)
    nq = [q for q in rows if relevant.get(q)]

    def ndcg_for(score, S, w):
        out = {}
        for q in nq:
            fused = []
            for d, s, _ in rows[q]:
                fused.append((d, popularity_rerank.fuse(s, score[d], w, S, popularity_rerank.UNIFORM_DIVISOR)
                              if (w > 0 and d in score) else s))
            ranked = [d for d, _ in sorted(fused, key=lambda t: (-t[1], t[0]))]
            out[q] = popularity_decay.ndcg_at_10(ranked, relevant[q])
        return out

    control = ndcg_for({}, 1.0, 0.0)
    cm = statistics.mean(control.values())
    if abs(cm - 0.7450) > 5e-5:
        sys.exit(f"CHECK FAILED: control nDCG@10 {cm:.4f} != 0.7450")
    print(f"\nnDCG@10 vs control ({cm:.4f}, CHECK passed), {len(nq)} queries -- best W per transform")
    ndcg_best_rows = []
    for name in ["LIFETIME", "PER_YEAR", "RECENT@180", "C1@180", c1best, c2best, c3best]:
        s = scores[name]
        S = statistics.median(s.values())
        best = None
        for w in W_GRID[1:]:
            r = paired(control, ndcg_for(s, S, w))
            if best is None or r["mean"] > best[1]["mean"]:
                best = (w, r)
        at02 = paired(control, ndcg_for(s, S, 0.2))
        ndcg_best_rows.append((name, best[0], best[1], at02["mean"]))
        print(f"  {name:14s} best W={best[0]:<6} {fmt(best[1])}   | W=0.2: {at02['mean']:+.4f}")
    print("\nprimary C1@180 full W sweep:")
    S = statistics.median(scores["C1@180"].values())
    primary_sweep = []
    for w in W_GRID[1:]:
        r = paired(control, ndcg_for(scores["C1@180"], S, w))
        primary_sweep.append((w, r))
        print(f"  W={w:<6} {fmt(r)}")

    # --- sidecar ---------------------------------------------------------------------------------
    payload = {
        "inputs": {"run": os.path.abspath(args.run), "qrels": os.path.abspath(args.qrels),
                   "counts": os.path.abspath(args.counts), "dates": os.path.abspath(args.dates),
                   "now": args.now},
        "population": {"admissible": len(adm), "truncated_excluded": len(truncated & in_pool),
                        "eligible_queries": len(eligible)},
        "pool_matched_auc": {
            name: dict(zip(("mean", "sd", "lo", "hi"), popularity_triage.mean_with_ci(list(perq[name].values()))))
            for name in auc_table_names
        },
        "best_c2": {"name": c2best, "mean_auc": mean_auc[c2best],
                    "cells_considered": len(HALF_LIVES) * (len(BETAS) - 1)},
        "primary": {"vs_per_year": pr_py, "vs_lifetime": pr_lt},
        "references": {"per_year_vs_lifetime": ref_py_lt, "recent180_vs_lifetime": ref_re_lt},
        "exploratory": {name: {**r, "holm_p": a} for (name, r), a in zip(fam, adj)},
        "ndcg": {
            "control_mean": cm, "n_queries": len(nq),
            "best_w_per_transform": [
                {"name": name, "best_w": w, **r, "at_w_0.2_mean": at02_mean}
                for name, w, r, at02_mean in ndcg_best_rows
            ],
            "primary_full_sweep": {str(w): r for w, r in primary_sweep},
        },
    }
    os.makedirs(args.out_dir, exist_ok=True)
    out_path = os.path.join(args.out_dir, "popularity-combined.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2, sort_keys=True)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Phase 0 triage for the relation-popularity signal (spec
docs/specs/2026-09-13-popularity-signal-measurement-design.md, "Phase 0 -- triage").

Answers one question and selects nothing: does citation count separate relevant from non-relevant
documents inside the pools `SearchSimilar` actually returned? Reads a captured TREC run, the BEIR
qrels that judge it, and Task 1's completed citation cache, and reports:

    1. Measurement 1 -- AUC of citation count, relevant vs non-relevant, in TWO estimators. They
       are not two ways of computing one number; they are two different comparisons and their
       standard errors come from different places. See "The two estimators" below.
    4a. AUC of publication date alone over the same pools, in the same two forms -- the descriptive
       half of the age control. Reported in both forms precisely so it is never compared across
       forms against measurement 1.
    4b. Within-stratum AUC of citation count: ONE stratum-pooled Mann-Whitney statistic over
       within-stratum (relevant, non-relevant) pairs only, plus the per-stratum table that makes
       its composition auditable and that measurement 4c's permutation will need.

    and the corpus median citation count, which is the rule that fixes `SaturationPoint` (spec
    "Parameters": S = the corpus median). This is the run that executes that rule.

It reports numbers and draws no conclusion. The arm-structure choice belongs to the reader.

The two estimators of measurement 1
-----------------------------------
(i)  THE POOLED, RETRIEVED-CORPUS AUC. Every relevant observation is paired against every
     non-relevant observation across ALL eligible queries -- the full n_pos x n_neg cross-product,
     not only each relevant document's own pool. The overwhelming majority of those pairs therefore
     compare across queries, which is what makes this the *retrieved-corpus* comparison and what
     makes it directly comparable to the 0.6201 already on record (that figure is against random
     corpus documents -- close in kind to (i), and a weaker claim than (ii)). It carries the
     Hanley-McNeil SE, which is defined only for this form. Its CI is ANTI-CONSERVATIVE and is
     labelled as such in the output: the negative sample reuses the same distinct documents across
     overlapping pools, so the pairs are nowhere near independent.

(ii) THE MEAN OF THE PER-QUERY AUCs. One AUC computed inside each eligible query's own pool, then
     averaged, with a CI from the between-query spread -- queries, not pairs, as the independent
     units. THIS IS THE SPEC'S POOL-MATCHED MEASUREMENT 1. It is honest about the dependence, but
     most eligible queries hold exactly one in-pool relevant document, so most of its terms are
     single-positive rank statistics rather than well-populated AUCs.

Neither is a refinement of the other and neither should be quoted without its label.

Documents with no resolved citation count
-----------------------------------------
Some in-pool documents are in the cache's `unresolved` list and have no count at all. An AUC over
citation count cannot rank them, and a missing count is NOT a count of zero -- the cache holds
genuine resolved zeros, so presence is tested with `in`, never with truthiness. They are EXCLUDED,
and the exclusion happens BEFORE the eligibility predicate is applied, not after. That order is
forced: if eligibility were judged on the raw pool, a query whose only in-pool relevant document is
unresolved would count as eligible while having no positive left to rank, and estimator (ii)'s term
for it would be undefined. Restricting first makes (i) and (ii) run over exactly the same
observations. Both populations are printed -- raw pool and count-bearing -- so the two are always
reconcilable and the size of the exclusion is never invisible.

The unit of observation is the (query, document) pair, not the document: relevance is a property of
the pair, and a document popular enough to be retrieved for many queries is a separate observation
in each. Measurement 4b stratifies exactly this same observation population.

Per beta_invariant.py's discipline, a check that silently asserts nothing over its data is
indistinguishable from one that passed, so the load-bearing preconditions are sys.exit conditions
rather than printed warnings: zero resolved counts in the cache, zero eligible queries, and a
stratum-pooled pair count of zero. Note which assertion this is NOT -- "some stratum came out
empty" is ordinary here and is not an error: bins are enumerated across the observed calendar range
so that an empty bin is visible rather than absent, and under a pooled statistic a stratum with no
relevant or no non-relevant member simply contributes zero pairs instead of an undefined ratio.

Ties are frequent in citation counts and are handled the standard way throughout: a tied pair
contributes 0.5.

Stdlib only.

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/popularity_triage.py \\
        --run    <path/to/run.trec> \\
        --qrels  <path/to/qrels.tsv> \\
        --counts <path/to/citations.json> \\
        --out-dir <path/to/out-dir>

Writes, into --out-dir:
    popularity-triage.json  -- every figure printed below, machine-readable, so the gate doc's
        numbers stay traceable to the run that produced them.
"""
import argparse
import bisect
import datetime
import json
import math
import os
import statistics
import sys

Z95 = 1.959964  # two-sided normal 95% multiplier, the convention these gate docs already use.
STRATUM_WIDTH = 5  # 5-year calendar bins -- measurement 4c's fixed strata (spec, 4c).
NO_DATE = "no-date"  # resolved ids with neither publicationDate nor year form their own stratum.


def load_qrels(path):
    """Parses a BEIR 3-column, header-bearing qrels TSV: `query-id\\tcorpus-id\\tscore`. This is NOT
    the 4-column TREC form -- there is no iteration column -- and the header row is data-shaped, so
    it is skipped explicitly rather than by a parse failure. Returns {queryId: set(docId)} holding
    only the judgements with score > 0."""
    relevant = {}
    with open(path, encoding="utf-8") as f:
        header = f.readline()
        if not header:
            sys.exit(f"{path}: empty file, expected a header row")
        fields = header.rstrip("\n").split("\t")
        if fields[:3] != ["query-id", "corpus-id", "score"]:
            sys.exit(
                f"{path}:1: expected the BEIR header `query-id\\tcorpus-id\\tscore`, got {header.rstrip()!r}. "
                f"This parser skips row 1 unconditionally; if this file has no header, row 1 of the "
                f"judgements would be silently discarded.")
        for lineno, line in enumerate(f, start=2):
            line = line.rstrip("\n")
            if not line.strip():
                continue
            fields = line.split("\t")
            if len(fields) != 3:
                sys.exit(f"{path}:{lineno}: expected 3 tab-separated fields, got {len(fields)}: {line!r}")
            query_id, doc_id, score = fields
            try:
                score = int(score)
            except ValueError:
                sys.exit(f"{path}:{lineno}: non-integer score {score!r}")
            if score > 0:
                relevant.setdefault(query_id, set()).add(doc_id)
    if not relevant:
        sys.exit(f"{path}: no judgement with score > 0 -- every query would be ineligible")
    return relevant


def load_run(path):
    """Parses a 6-column TREC run: `queryId Q0 docId rank score tag`, whitespace-separated.
    Returns {queryId: [docId, ...]} in file (rank) order. Rank order is not used by any statistic
    below -- an AUC is order-free -- but duplicate documents within one query are rejected, because
    a pool holding the same document twice would double-weight it in every pair count."""
    pool = {}
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            if not line.strip():
                continue
            fields = line.split()
            if len(fields) != 6:
                sys.exit(f"{path}:{lineno}: expected 6 whitespace-separated fields, got {len(fields)}: {line!r}")
            query_id, _, doc_id = fields[0], fields[1], fields[2]
            docs = pool.setdefault(query_id, [])
            if doc_id in docs:
                sys.exit(f"{path}:{lineno}: document {doc_id!r} appears twice in query {query_id!r}'s pool")
            docs.append(doc_id)
    if not pool:
        sys.exit(f"{path}: no run rows")
    return pool


def load_counts(path):
    """Task 1's citations.json: {counts, years, dates, unresolved}. `counts` and `years` map docId
    to int, `dates` maps docId to an ISO `YYYY-MM-DD`, `unresolved` is a list of docIds Semantic
    Scholar returned nothing for. A docId absent from `counts` has NO count; it is never a zero."""
    with open(path, encoding="utf-8") as f:
        cache = json.load(f)
    for key in ("counts", "years", "dates", "unresolved"):
        if key not in cache:
            sys.exit(f"{path}: missing required key {key!r}; expected counts, years, dates, unresolved")
    counts, unresolved = cache["counts"], set(cache["unresolved"])
    if not counts:
        sys.exit(f"{path}: the resolved-count population is zero -- there is nothing to score")
    overlap = set(counts) & unresolved
    if overlap:
        sys.exit(
            f"{path}: {len(overlap)} id(s) are in BOTH `counts` and `unresolved` "
            f"(e.g. {sorted(overlap)[:3]}) -- the cache is inconsistent and presence is ambiguous")
    return counts, cache["years"], cache["dates"], unresolved


def date_ordinal(doc_id, dates, years):
    """The document's publication date as a sortable day ordinal, using measurement 4c's fixed
    `publicationDate` -> `year` fallback. A year-only document is placed at 1 July, its year's
    midpoint, so it orders after the first half and before the second half of its own year rather
    than being forced to either extreme. Returns None when the document has neither form."""
    iso = dates.get(doc_id)
    if iso is not None:
        try:
            return datetime.date.fromisoformat(iso).toordinal()
        except ValueError:
            sys.exit(f"citations.json: document {doc_id!r} has an unparseable publicationDate {iso!r}")
    year = years.get(doc_id)
    if year is not None:
        return datetime.date(int(year), 7, 1).toordinal()
    return None


def date_year(doc_id, dates, years):
    """The calendar year the strata are cut on, under the same publicationDate -> year fallback.
    Returns None when the document has neither form."""
    iso = dates.get(doc_id)
    if iso is not None:
        return int(iso[:4])
    year = years.get(doc_id)
    return int(year) if year is not None else None


def has_resolved_count(counts):
    """The admissibility predicate for every AUC over citation count. Presence is membership, NEVER
    truthiness: the cache holds genuine resolved zeros, and a count of 0 is a ranked value that must
    sort below every positive count -- not a missing one to be dropped. Named rather than inlined so
    a test can pin that distinction; `bool(counts.get(doc))` would pass every other check here."""
    return lambda doc_id: doc_id in counts


def has_resolved_date(counts, dates, years):
    """Measurement 4a's admissibility predicate: the document must carry both a count (so it belongs
    to the same analysis population as measurement 1) and a date of either form."""
    return lambda doc_id: doc_id in counts and date_ordinal(doc_id, dates, years) is not None


def concordant_pairs(pos, neg):
    """The Mann-Whitney concordant-pair sum over the full (pos x neg) cross-product: each pair
    contributes 1.0 when the positive value is larger, 0.5 when they tie, 0.0 otherwise. Returns
    (sum, number_of_pairs). An empty side yields (0.0, 0) -- zero pairs, never an undefined ratio,
    which is what lets 4b sum a degenerate stratum instead of aborting on it."""
    if not pos or not neg:
        return 0.0, 0
    ordered = sorted(neg)
    total = 0.0
    for value in pos:
        lower = bisect.bisect_left(ordered, value)
        upper = bisect.bisect_right(ordered, value)
        total += lower + 0.5 * (upper - lower)
    return total, len(pos) * len(neg)


def auc(pos, neg):
    """AUC = concordant pairs / all pairs, with ties at 0.5. None when either side is empty."""
    total, pairs = concordant_pairs(pos, neg)
    return total / pairs if pairs else None


def hanley_mcneil_se(area, n_pos, n_neg):
    """Hanley & McNeil (1982) standard error of a two-sample AUC. Defined for the POOLED estimator
    only: it models n_pos x n_neg independent pairs, which is exactly the assumption that overlapping
    retrieval pools violate, so the CI built from it is anti-conservative and is labelled so."""
    if n_pos < 1 or n_neg < 1:
        return None
    q1 = area / (2.0 - area)
    q2 = 2.0 * area * area / (1.0 + area)
    variance = (area * (1.0 - area)
                + (n_pos - 1) * (q1 - area * area)
                + (n_neg - 1) * (q2 - area * area)) / (n_pos * n_neg)
    return math.sqrt(max(variance, 0.0))


def mean_with_ci(values):
    """Mean of a per-query statistic with a 95% CI from the BETWEEN-QUERY spread -- queries, not
    pairs, as the independent units. Returns (mean, sd, lo, hi); sd and the CI are None below two
    queries."""
    mean = statistics.mean(values)
    if len(values) < 2:
        return mean, None, None, None
    sd = statistics.stdev(values)
    half = Z95 * sd / math.sqrt(len(values))
    return mean, sd, mean - half, mean + half


def observations(pool, relevant, admissible):
    """Flattens the run into the (query, document) observations that a statistic can actually use,
    then applies the eligibility predicate to what is left.

    `admissible` is the predicate for "this document carries the value being ranked". Restricting
    comes FIRST and eligibility second: a query is eligible when its ADMISSIBLE pool holds at least
    one relevant and at least one non-relevant document. Judged on the raw pool instead, a query
    whose only relevant document is inadmissible would be called eligible while having no positive
    left, and its per-query AUC would be undefined.

    Returns (eligible_query_ids, {queryId: (positive_docIds, negative_docIds)}, n_dropped), where
    n_dropped counts observations removed by `admissible` from RAW-eligible pools -- the figure that
    reconciles this population against the unrestricted one."""
    raw_eligible = eligible_queries(pool, relevant, lambda _doc: True)
    dropped = sum(1 for q in raw_eligible for d in pool[q] if not admissible(d))

    eligible = eligible_queries(pool, relevant, admissible)
    split = {}
    for query_id in eligible:
        judged = relevant.get(query_id, frozenset())
        pos, neg = [], []
        for doc_id in pool[query_id]:
            if admissible(doc_id):
                (pos if doc_id in judged else neg).append(doc_id)
        split[query_id] = (pos, neg)
    return eligible, split, dropped


def eligible_queries(pool, relevant, admissible):
    """The eligibility predicate: a query qualifies when its admissible pool holds at least one
    relevant AND at least one non-relevant document. Returned in run-file query order."""
    eligible = []
    for query_id, docs in pool.items():
        judged = relevant.get(query_id, frozenset())
        has_pos = has_neg = False
        for doc_id in docs:
            if not admissible(doc_id):
                continue
            if doc_id in judged:
                has_pos = True
            else:
                has_neg = True
            if has_pos and has_neg:
                break
        if has_pos and has_neg:
            eligible.append(query_id)
    return eligible


def pooled_estimator(split, value_of):
    """Estimator (i): the pooled two-sample AUC over the FULL cross-product of every eligible
    query's positives against every eligible query's negatives, with the Hanley-McNeil SE. Also
    returns the within-pool pair count, for contrast: the gap between the two is how much of this
    estimator compares across queries rather than inside a pool."""
    pos, neg, within_pool_pairs = [], [], 0
    for query_pos, query_neg in split.values():
        pos.extend(value_of(d) for d in query_pos)
        neg.extend(value_of(d) for d in query_neg)
        within_pool_pairs += len(query_pos) * len(query_neg)
    area = auc(pos, neg)
    if area is None:
        return None
    se = hanley_mcneil_se(area, len(pos), len(neg))
    return {
        "auc": area,
        "n_pos": len(pos),
        "n_neg": len(neg),
        "pairs": len(pos) * len(neg),
        "within_pool_pairs": within_pool_pairs,
        "se_hanley_mcneil": se,
        "ci_lo": area - Z95 * se,
        "ci_hi": area + Z95 * se,
    }


def per_query_estimator(split, value_of):
    """Estimator (ii), the spec's pool-matched measurement 1: one AUC inside each eligible query's
    own pool, then the mean, with a CI from the between-query spread. Also counts the queries whose
    pool holds exactly one relevant document -- those terms are single-positive rank statistics, not
    well-populated AUCs, and the share of them qualifies how the mean should be read."""
    per_query, single_positive = {}, 0
    for query_id, (query_pos, query_neg) in split.items():
        area = auc([value_of(d) for d in query_pos], [value_of(d) for d in query_neg])
        if area is None:
            continue
        per_query[query_id] = area
        if len(query_pos) == 1:
            single_positive += 1
    if not per_query:
        return None
    mean, sd, lo, hi = mean_with_ci(list(per_query.values()))
    return {
        "mean_auc": mean,
        "n_queries": len(per_query),
        "sd": sd,
        "ci_lo": lo,
        "ci_hi": hi,
        "single_positive_queries": single_positive,
        "per_query": per_query,
    }


def build_strata(split, counts, dates, years):
    """Measurement 4c's fixed strata, applied to the same (query, document) observations every other
    statistic here uses: 5-year calendar bins on publicationDate falling back to year, and one
    separate `no-date` stratum for resolved ids carrying neither.

    Bins are ENUMERATED across the observed calendar range rather than only where observations
    landed, so a bin with nothing in it appears in the table as an empty row instead of vanishing
    from it. Returns (ordered_stratum_keys, {key: (pos_values, neg_values)})."""
    members = {}
    seen_bins = []
    for query_pos, query_neg in split.values():
        for doc_ids, side in ((query_pos, 0), (query_neg, 1)):
            for doc_id in doc_ids:
                year = date_year(doc_id, dates, years)
                if year is None:
                    key = NO_DATE
                else:
                    key = STRATUM_WIDTH * (year // STRATUM_WIDTH)
                    seen_bins.append(key)
                members.setdefault(key, ([], []))[side].append(counts[doc_id])

    keys = []
    if seen_bins:
        for start in range(min(seen_bins), max(seen_bins) + 1, STRATUM_WIDTH):
            keys.append(start)
    keys.append(NO_DATE)
    return keys, members


def stratum_label(key):
    return key if key == NO_DATE else f"{key}-{key + STRATUM_WIDTH - 1}"


def stratum_table(keys, members):
    """Per-stratum rows plus the stratum-pooled Mann-Whitney statistic that IS measurement 4b.

    The reported 4b number is ONE statistic: concordant pairs summed over within-stratum
    (relevant, non-relevant) pairs only, divided by the number of such pairs. It is defined whatever
    any single stratum contains -- a stratum with no relevant or no non-relevant member contributes
    zero pairs rather than an undefined ratio, and is marked `degenerate` in the table instead of
    aborting the run. The table is what makes the pooled number's composition auditable, and it is
    what measurement 4c's within-stratum permutation will need."""
    rows, total, total_pairs = [], 0.0, 0
    for key in keys:
        pos, neg = members.get(key, ([], []))
        stratum_sum, pairs = concordant_pairs(pos, neg)
        total += stratum_sum
        total_pairs += pairs
        rows.append({
            "stratum": stratum_label(key),
            "size": len(pos) + len(neg),
            "n_pos": len(pos),
            "n_neg": len(neg),
            "pairs": pairs,
            "auc": (stratum_sum / pairs) if pairs else None,
            "degenerate": pairs == 0,
        })
    return rows, total, total_pairs


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run", required=True, help="the captured 6-column TREC run whose pools are scored")
    ap.add_argument("--qrels", required=True, help="BEIR 3-column header-bearing qrels TSV")
    ap.add_argument("--counts", required=True, help="Task 1's citations.json: {counts, years, dates, unresolved}")
    ap.add_argument("--out-dir", required=True, help="directory popularity-triage.json is written to")
    args = ap.parse_args()

    relevant = load_qrels(args.qrels)
    pool = load_run(args.run)
    counts, years, dates, unresolved = load_counts(args.counts)

    resolved = len(counts)
    with_date = sum(1 for d in counts if d in dates)
    year_only = sum(1 for d in counts if d not in dates and d in years)
    no_date = resolved - with_date - year_only
    corpus_counts = sorted(counts.values())
    corpus_median = statistics.median(corpus_counts)

    print("[popularity_triage] cache")
    print(f"  resolved                 {resolved}")
    print(f"  unresolved               {len(unresolved)}")
    print(f"  with publicationDate     {with_date}")
    print(f"  year only                {year_only}")
    print(f"  neither (no-date stratum){no_date:>6}")
    print(f"  corpus median count      {corpus_median}   <- the rule that fixes SaturationPoint (S)")
    print(f"  corpus count range       {corpus_counts[0]} .. {corpus_counts[-1]}")

    # -- the analysis population -------------------------------------------------------------
    # A document with no resolved count cannot enter an AUC over counts. Presence is `in counts`,
    # never truthiness: the cache holds genuine resolved zeros and they must stay in.
    has_count = has_resolved_count(counts)
    raw_eligible = eligible_queries(pool, relevant, lambda _doc: True)
    eligible, split, dropped = observations(pool, relevant, has_count)

    raw_pos = sum(1 for q in raw_eligible for d in pool[q] if d in relevant.get(q, frozenset()))
    raw_neg = sum(len(pool[q]) for q in raw_eligible) - raw_pos
    print("\n[popularity_triage] population")
    print(f"  queries in run                        {len(pool)}")
    print(f"  raw-pool eligible queries             {len(raw_eligible)}  (n_pos {raw_pos}, n_neg {raw_neg})")
    print(f"  in-pool observations with no count    {dropped}  (excluded -- absent, NOT zero)")
    print(f"  count-bearing eligible queries        {len(eligible)}  "
          f"({len(raw_eligible) - len(eligible)} raw-eligible queries lost every positive)")

    if not eligible:
        sys.exit("[popularity_triage] no query has at least one relevant AND one non-relevant "
                 "count-bearing document in its pool -- the AUC is undefined over every pool")

    count_of = counts.__getitem__

    # -- measurement 1 -----------------------------------------------------------------------
    pooled = pooled_estimator(split, count_of)
    per_query = per_query_estimator(split, count_of)
    across = pooled["pairs"] - pooled["within_pool_pairs"]
    print("\n[popularity_triage] measurement 1 -- AUC of citation count, relevant vs non-relevant")
    print("  (i) POOLED / RETRIEVED-CORPUS form -- NOT pool-matched")
    print(f"      AUC              {pooled['auc']:.4f}")
    print(f"      n_pos x n_neg    {pooled['n_pos']} x {pooled['n_neg']} = {pooled['pairs']} pairs")
    print(f"      within-pool      {pooled['within_pool_pairs']} of those "
          f"({100.0 * across / pooled['pairs']:.2f}% of pairs compare ACROSS queries)")
    print(f"      Hanley-McNeil SE {pooled['se_hanley_mcneil']:.6f}")
    print(f"      95% CI           [{pooled['ci_lo']:.4f}, {pooled['ci_hi']:.4f}]  "
          f"ANTI-CONSERVATIVE: the negatives reuse the same documents across overlapping pools")
    print("  (ii) MEAN OF PER-QUERY AUCs -- this is the spec's pool-matched measurement 1")
    print(f"      mean AUC         {per_query['mean_auc']:.4f}")
    print(f"      queries          {per_query['n_queries']}  (sd {per_query['sd']:.4f})")
    print(f"      95% CI           [{per_query['ci_lo']:.4f}, {per_query['ci_hi']:.4f}]  "
          f"from the between-query spread, NOT Hanley-McNeil")
    print(f"      single-positive  {per_query['single_positive_queries']} of {per_query['n_queries']} "
          f"queries hold exactly one in-pool relevant document")

    # -- measurement 4a ----------------------------------------------------------------------
    # Same pools, same two forms, publication date in place of citation count. Reported in both
    # forms so it is never read across forms against measurement 1.
    date_eligible, date_split, date_dropped = observations(
        pool, relevant, has_resolved_date(counts, dates, years))
    print("\n[popularity_triage] measurement 4a -- AUC of publication date alone")
    print(f"  resolved-date population: {len(date_eligible)} eligible queries; "
          f"{date_dropped} in-pool observations excluded (no count or no date of either kind)")
    date_pooled = date_per_query = None
    if date_eligible:
        date_of = lambda doc: date_ordinal(doc, dates, years)
        date_pooled = pooled_estimator(date_split, date_of)
        date_per_query = per_query_estimator(date_split, date_of)
        print(f"  (i)  pooled / retrieved-corpus  AUC {date_pooled['auc']:.4f}  "
              f"95% CI [{date_pooled['ci_lo']:.4f}, {date_pooled['ci_hi']:.4f}]  (anti-conservative)")
        print(f"       n_pos x n_neg   {date_pooled['n_pos']} x {date_pooled['n_neg']} = {date_pooled['pairs']} pairs")
        print(f"  (ii) pool-matched mean         AUC {date_per_query['mean_auc']:.4f}  "
              f"95% CI [{date_per_query['ci_lo']:.4f}, {date_per_query['ci_hi']:.4f}]  "
              f"over {date_per_query['n_queries']} queries")
        print("       AUC > 0.5 means relevant documents are NEWER than non-relevant ones.")
    else:
        print("  no query has a relevant and a non-relevant dated document in its pool -- 4a not computed")

    # -- measurement 4b ----------------------------------------------------------------------
    keys, members = build_strata(split, counts, dates, years)
    rows, pooled_sum, pooled_pairs = stratum_table(keys, members)
    if pooled_pairs == 0:
        sys.exit("[popularity_triage] the stratum-pooled pair count is zero -- no stratum holds both a "
                 "relevant and a non-relevant document, so 4b would be computed over nothing")
    auc_4b = pooled_sum / pooled_pairs
    print("\n[popularity_triage] measurement 4b -- within-stratum AUC of citation count")
    print(f"  stratum-pooled Mann-Whitney AUC   {auc_4b:.4f}")
    print(f"  within-stratum pairs              {pooled_pairs}  "
          f"(vs {pooled['pairs']} unstratified; the age confound is what the difference removes)")
    print(f"  strata enumerated                 {len(rows)}  "
          f"({sum(1 for r in rows if r['degenerate'])} degenerate, contributing zero pairs)")
    print("  per-stratum table:")
    print(f"    {'stratum':<12} {'size':>6} {'n_pos':>6} {'n_neg':>6} {'pairs':>9}  {'AUC':>7}")
    for row in rows:
        shown = f"{row['auc']:.4f}" if row["auc"] is not None else "   --  "
        mark = "  DEGENERATE" if row["degenerate"] else ""
        print(f"    {row['stratum']:<12} {row['size']:>6} {row['n_pos']:>6} {row['n_neg']:>6} "
              f"{row['pairs']:>9}  {shown:>7}{mark}")

    # -- sidecar -----------------------------------------------------------------------------
    payload = {
        "inputs": {"run": os.path.abspath(args.run), "qrels": os.path.abspath(args.qrels),
                   "counts": os.path.abspath(args.counts)},
        "cache": {"resolved": resolved, "unresolved": len(unresolved), "with_publication_date": with_date,
                  "year_only": year_only, "no_date": no_date,
                  "corpus_median_count": corpus_median,
                  "corpus_count_min": corpus_counts[0], "corpus_count_max": corpus_counts[-1]},
        "population": {"queries_in_run": len(pool), "raw_eligible_queries": len(raw_eligible),
                       "raw_n_pos": raw_pos, "raw_n_neg": raw_neg,
                       "observations_without_count": dropped,
                       "eligible_queries": len(eligible)},
        "measurement_1": {"pooled_retrieved_corpus": {k: v for k, v in pooled.items()},
                          "per_query_pool_matched": {k: v for k, v in per_query.items() if k != "per_query"}},
        "measurement_4a": {"eligible_queries": len(date_eligible),
                           "observations_excluded": date_dropped,
                           "pooled_retrieved_corpus": date_pooled,
                           "per_query_pool_matched": (
                               {k: v for k, v in date_per_query.items() if k != "per_query"}
                               if date_per_query else None)},
        "measurement_4b": {"stratum_pooled_auc": auc_4b, "within_stratum_pairs": pooled_pairs,
                           "concordant_sum": pooled_sum, "strata": rows},
    }
    os.makedirs(args.out_dir, exist_ok=True)
    out_path = os.path.join(args.out_dir, "popularity-triage.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2, sort_keys=True)
    print(f"\n[popularity_triage] wrote {out_path}")


if __name__ == "__main__":
    main()

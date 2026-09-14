#!/usr/bin/env python3
"""Phase 1 arm-independent re-ranker for the relation-popularity signal (spec
docs/specs/2026-09-13-popularity-signal-measurement-design.md, "Phase 1 -- offline screen").

Applies the shipped server's fusion identity to one already-recorded TREC run, offline, at one
(W, SaturationPoint) cell. `ResultReranker.cs:37-57` fuses signals as a weighted mean over the
signals PRESENT on a candidate: `weightTotal` starts at `WBase = 0.45`; `hasCentroid` adds
`WCentroid = 0.45` (uniform on this corpus -- Task 3 found zero object points lacking
`body_centroid`); `hasPopularity` adds `WPopularity` to the weighted sum *and* to the weight total
under one guard. `LambdaSimilar = 1.00` collapses `IResultDiversifier.Diversify` to `Take(topK)`
exactly (`ResultDiversifier.cs:74-75`), so `.similar.trec`'s score column *is* the fused score and
this identity applies to it directly, with no re-derivation from raw hits needed:

    pop = count / (count + SaturationPoint)

    pop present:  fused_new = (0.90*fused_old + W*pop) / (0.90 + W)
    pop absent:   fused_new = fused_old                              <- NOT pop=0, NOT a median

Substituting `pop = 0` for a document with no resolved count would score it 0.0 and punish it hard;
substituting a median would pull it toward the pool. Both model a server that does not exist.
Absence is membership in the counts cache, never truthiness: the corpus genuinely contains resolved
zero-count documents (`pop = 0/(0+S) = 0`), and those take the PRESENT branch.

The 0.90 divisor is likewise not universal: `ResultReranker.cs:37-38` seeds
`weightTotal = WBase = 0.45`, and only `hasCentroid` (`:40-45`) adds `WCentroid`. A document lacking
`body_centroid` fuses at 0.45, not 0.90, so documents named in `--divisor-exceptions` take instead:

    pop present:  fused_new = (0.45*fused_old + W*pop) / (0.45 + W)
    pop absent:   fused_new = fused_old

Task 3 measured this set as empty on the current corpus, but the flag is implemented and unit-tested
regardless -- it is the interface Phase 1's downstream phases depend on, not an assumption baked in
as a constant.

Two mandatory, non-zero assertions run before any output is trusted (same discipline as
beta_invariant.py and popularity_triage.py -- a check that silently asserts nothing over its data is
indistinguishable from a check that passed, so these are sys.exit conditions, not warnings):

    1. At least one query's ranked document order must differ from the input run. Zero reordered
       queries is a failure of the re-ranker, not a pass -- it is exactly what a re-ranker that
       silently did nothing looks like.
    2. The absent-popularity set must be non-empty, AND every one of its scores must be unchanged
       from the input run. This is the branch most likely to be silently wrong (substituting 0 or a
       median instead of leaving the score alone), and it is the one with no natural signal that it
       ran at all -- a bug here produces a plausible-looking wrong number, not a crash.

The (W, SaturationPoint) grid is NOT built here -- it belongs to whichever arm structure Phase 0's
triage read selects (spec "Measurement 2"), which this task's scope excludes.

Stdlib only.

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/popularity_rerank.py \\
        --run scifact-2048-2026-09-06/runs/sci-2048.similar.trec \\
        --counts scratchpad/popularity/citations.json \\
        --w 0.1958 --saturation 182 \\
        --out scratchpad/popularity/sci-2048.similar.popularity.trec \\
        --divisor-exceptions scratchpad/popularity/divisor-exceptions.txt

Then score the output with report.py (see this script's task brief for the exact invocation --
report.py needs PYTHONPATH set to the corpora repo's python-libs).
"""
import argparse
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import popularity_triage  # noqa: E402

WBASE = 0.45       # ResultReranker.cs:38 -- VectorRankingOptions.WBase, seeded unconditionally.
WCENTROID = 0.45   # ResultReranker.cs:44 -- VectorRankingOptions.WCentroid, added when hasCentroid.
UNIFORM_DIVISOR = WBASE + WCENTROID  # 0.90 -- every candidate not named in --divisor-exceptions.


def load_run(path):
    """Parses a 6-column TREC run: `queryId Q0 docId rank score tag`, whitespace-separated.
    Returns {queryId: [(docId, score, tag), ...]} in file (rank) order -- the order Step 2's
    assertion 1 compares the re-ranked order against. `Q0` is read but not kept: every row of this
    corpus's run files carries the literal `Q0` iteration column and nothing downstream uses it, so
    re-emitting the literal on write (matching this directory's own trec-writing convention in
    multivector.py and aspect_oracle.py) costs nothing and needs no round trip. `tag` is kept per
    row rather than replaced, so the output's provenance column still reads faithfully.

    A document appearing twice in one query's pool is rejected: it would be re-ranked and re-emitted
    twice, corrupting both the identity's per-candidate application and the output run's rank
    numbering (same defect load_run in popularity_triage.py guards against)."""
    pool = {}
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            if not line.strip():
                continue
            fields = line.split()
            if len(fields) != 6:
                sys.exit(f"{path}:{lineno}: expected 6 whitespace-separated fields, got {len(fields)}: {line!r}")
            query_id, _iter, doc_id, _rank, score, tag = fields
            try:
                score = float(score)
            except ValueError:
                sys.exit(f"{path}:{lineno}: non-numeric score {score!r}")
            rows = pool.setdefault(query_id, [])
            if any(existing_doc == doc_id for existing_doc, _, _ in rows):
                sys.exit(f"{path}:{lineno}: document {doc_id!r} appears twice in query {query_id!r}'s pool")
            rows.append((doc_id, score, tag))
    if not pool:
        sys.exit(f"{path}: no run rows")
    return pool


def load_divisor_exceptions(path):
    """Task 3 Step 3's `divisor-exceptions.txt`: one docId per line, the documents found to lack
    `body_centroid` and therefore fuse at 0.45 rather than 0.90 in the present branch. `path=None`
    (the flag's default) returns an empty set -- Task 3 measured this set as empty on the current
    corpus, so the default must be inert, never absent-as-an-error. Blank lines are skipped; every
    other line is taken verbatim as a docId (no whitespace stripping beyond the trailing newline
    would silently rename an id, so only `str.strip()` -- leading/trailing whitespace -- is applied,
    matching how a plain `docId\\n`-per-line file is written)."""
    if path is None:
        return set()
    if not os.path.exists(path):
        sys.exit(f"--divisor-exceptions {path}: not found")
    exceptions = set()
    with open(path, encoding="utf-8") as f:
        for line in f:
            doc_id = line.strip()
            if doc_id:
                exceptions.add(doc_id)
    return exceptions


def popularity(count, saturation):
    """`pop = count / (count + SaturationPoint)`. `count` is a resolved, non-negative citation
    count (a genuine 0 is valid input, per PopularityFor's shipped formula at RecencyBoost=0 --
    spec "The signal is a lifetime count at the shipped default"); `saturation` is validated finite
    and positive by `validate_positive_finite` before this is ever called, so the denominator can
    only be zero if `count` is also negative, which the caller never passes."""
    return count / (count + saturation)


def fuse(fused_old, count, w, saturation, divisor_base):
    """The present branch of the identity, parameterised on `divisor_base` (0.90 for the uniform
    case, 0.45 for a `--divisor-exceptions` document -- ResultReranker.cs:37-45). Not called at all
    for the absent branch: `rerank_query` copies `fused_old` through unchanged there, rather than
    routing it through this function with some sentinel `count`, so that branch can never
    accidentally pick up a `pop` term."""
    pop = popularity(count, saturation)
    return (divisor_base * fused_old + w * pop) / (divisor_base + w)


def rerank_query(rows, counts, divisor_exceptions, w, saturation):
    """Applies the identity to one query's pool. `rows` is `[(docId, oldScore, tag), ...]` in
    original rank order (as `load_run` returns per query).

    Returns (reranked, absent_pairs): `reranked` is `[(docId, newScore, tag), ...]` re-sorted
    descending by `newScore`, ties broken by original file order (Python's `sorted` is stable, and
    ties are broken the same way LINQ's `OrderByDescending` breaks them in `ResultReranker.Rerank`
    -- by the order candidates arrived in, which here is the pool's original rank order). `absent_pairs`
    is `[(docId, oldScore, newScore), ...]` for every document that took the absent branch in THIS
    query, for Step 2's assertion 2 to check across the whole run.

    The absent branch does not call `fuse` at all -- `new_score` is `old_score` itself, the same
    float object, never recomputed and never reformatted. That is what makes assertion 2's
    "unchanged" check a structural guarantee rather than a tolerance comparison, and it is exactly
    the case a mutant that substituted `pop = 0` or a median would violate."""
    reranked = []
    absent_pairs = []
    for doc_id, old_score, tag in rows:
        if doc_id in counts:
            divisor_base = 0.45 if doc_id in divisor_exceptions else UNIFORM_DIVISOR
            new_score = fuse(old_score, counts[doc_id], w, saturation, divisor_base)
        else:
            new_score = old_score
            absent_pairs.append((doc_id, old_score, new_score))
        reranked.append((doc_id, new_score, tag))
    reranked.sort(key=lambda row: -row[1])
    return reranked, absent_pairs


def check_order_changed(pool, reranked_pool):
    """Step 2 assertion 1: at least one query's ranked document ORDER (the sequence of doc ids, not
    the scores) must differ from the input run. Zero is a failure, not a pass -- it is exactly what a
    re-ranker that silently changed nothing looks like. Returns the count of queries whose order
    changed, for the printed report."""
    changed = 0
    for query_id, rows in pool.items():
        original_order = [doc_id for doc_id, _score, _tag in rows]
        new_order = [doc_id for doc_id, _score, _tag in reranked_pool[query_id]]
        if original_order != new_order:
            changed += 1
    if changed == 0:
        sys.exit(
            "[popularity_rerank] assertion 1 FAILED: 0 of "
            f"{len(pool)} queries changed ranked order -- a re-ranker that silently changed "
            "nothing looks exactly like a re-ranker that worked; zero is a failure, not a pass")
    return changed


def check_absent_unchanged(all_absent_pairs):
    """Step 2 assertion 2: the absent-popularity set must be non-empty, AND every one of its scores
    must be unchanged from the input run. Non-emptiness first -- an empty set here would make the
    second half vacuously true, hiding exactly the failure mode ("the absent branch never ran, or
    every document happened to resolve") this assertion exists to catch. `all_absent_pairs` is
    `[(queryId, docId, oldScore, newScore), ...]` across every query. Returns the count asserted."""
    if not all_absent_pairs:
        sys.exit(
            "[popularity_rerank] assertion 2 FAILED: 0 (query, document) pairs took the "
            "absent-popularity branch -- either every pooled document resolved a count (implausible "
            "given the ~6% unresolved rate) or the branch never ran; a non-empty set is required")
    for query_id, doc_id, old_score, new_score in all_absent_pairs:
        if old_score != new_score:
            sys.exit(
                "[popularity_rerank] assertion 2 FAILED: (queryId="
                f"{query_id!r}, docId={doc_id!r}) has no resolved popularity count but its score "
                f"changed from {old_score!r} to {new_score!r} -- the absent branch must leave "
                "fused_old unchanged, not substitute pop=0 or a median")
    return len(all_absent_pairs)


def write_run(path, reranked_pool, pool_query_order):
    """Writes the re-ranked run as 6-column TREC: `queryId Q0 docId rank score tag`, rank
    recomputed 1..N within each query from the new descending order, score at 6 decimal places --
    this directory's existing trec-writing convention (multivector.py's `trec_lines`,
    aspect_oracle.py's `write_trec`). Queries are written in `pool_query_order` (the input run's own
    query order), not dict iteration order, so re-running against the same input is reproducible
    byte-for-byte regardless of Python's dict-ordering guarantees changing upstream."""
    with open(path, "w", encoding="utf-8") as f:
        for query_id in pool_query_order:
            for rank, (doc_id, score, tag) in enumerate(reranked_pool[query_id], start=1):
                f.write(f"{query_id} Q0 {doc_id} {rank} {score:.6f} {tag}\n")


def validate_positive_finite(value, flag_name):
    """Guards --w and --saturation. `saturation` sits in `popularity`'s denominator and `w` sits in
    both branches of `fuse`'s divisor; a non-finite or non-positive value there produces a
    plausible-looking wrong number (NaN propagates silently through float arithmetic, and
    `saturation <= 0` can divide by zero or invert the curve) rather than a crash close to the
    mistake. Mirrors beta_invariant.py's `validate_max_beta` guard."""
    if not math.isfinite(value) or value <= 0:
        sys.exit(f"{flag_name} {value!r} is invalid -- it must be finite and strictly positive")


def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run", required=True, help="the captured 6-column TREC run to re-rank (e.g. sci-2048.similar.trec)")
    ap.add_argument("--counts", required=True, help="Task 1's citations.json: {counts, years, dates, unresolved}")
    ap.add_argument("--w", type=float, required=True, help="WPopularity for this cell -- any finite positive value")
    ap.add_argument("--saturation", type=float, required=True, help="SaturationPoint for this cell -- any finite positive value")
    ap.add_argument("--out", required=True, help="path the re-ranked TREC run is written to")
    ap.add_argument("--divisor-exceptions", default=None, metavar="FILE",
                     help="text file of docIds (one per line) that lack body_centroid and fuse at "
                          "0.45 rather than 0.90 in the present branch (Task 3 Step 3's "
                          "divisor-exceptions.txt); omit for an empty set")
    return ap


def main():
    args = build_arg_parser().parse_args()
    validate_positive_finite(args.w, "--w")
    validate_positive_finite(args.saturation, "--saturation")

    pool = load_run(args.run)
    counts, _years, _dates, unresolved = popularity_triage.load_counts(args.counts)
    divisor_exceptions = load_divisor_exceptions(args.divisor_exceptions)

    pool_query_order = list(pool.keys())
    reranked_pool = {}
    all_absent_pairs = []
    for query_id, rows in pool.items():
        reranked, absent_pairs = rerank_query(rows, counts, divisor_exceptions, args.w, args.saturation)
        reranked_pool[query_id] = reranked
        all_absent_pairs.extend((query_id, doc_id, old, new) for doc_id, old, new in absent_pairs)

    total_rows = sum(len(rows) for rows in pool.values())
    present_rows = total_rows - len(all_absent_pairs)
    print("[popularity_rerank] inputs")
    print(f"  run                  {args.run}  ({len(pool)} queries, {total_rows} rows)")
    print(f"  counts               {args.counts}  ({len(counts)} resolved, {len(unresolved)} unresolved)")
    print(f"  divisor-exceptions   {args.divisor_exceptions or '(none)'}  ({len(divisor_exceptions)} docIds)")
    print(f"  W                    {args.w}")
    print(f"  SaturationPoint      {args.saturation}")
    print(f"  present / absent     {present_rows} / {len(all_absent_pairs)} rows")

    changed = check_order_changed(pool, reranked_pool)
    print(f"\n[popularity_rerank] assertion 1 PASSED: {changed} / {len(pool)} queries changed ranked order")

    asserted = check_absent_unchanged(all_absent_pairs)
    print(f"[popularity_rerank] assertion 2 PASSED: {asserted} absent-popularity (query, document) "
          f"pairs, every score unchanged from the input run")

    write_run(args.out, reranked_pool, pool_query_order)
    print(f"\n[popularity_rerank] wrote {args.out}")


if __name__ == "__main__":
    main()

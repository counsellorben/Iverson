#!/usr/bin/env python3
"""Phase 2 precondition gate for the chunk-coverage beta sweep (spec
docs/specs/2026-09-09-chunk-coverage-phase2-design.md §4).

Before the six-arm beta sweep's nDCG@10 deltas may be interpreted, three things must be true of the
arms that produced them. A check that silently asserts nothing over its data is indistinguishable
from a check that passed, so each of the three below reports a mandatory non-zero count and fails
loudly -- sys.exit, not a printed warning -- the moment it is not satisfied:

    1. Single-chunk pairs are identical. A (query, document) pair with exactly one pooled chunk has
       no tail (DocumentRanking's `descending.Skip(1).Take(3).Sum()` is 0 over an empty slice), so
       its score is `descending[0] + beta*0` at every beta -- identical, exactly, string for
       string, at beta=0 and beta=parity. Zero pairs asserted is a failure of the precondition, not
       a pass.
    2. At least one multi-chunk pair differs between beta=0 and beta=parity. This is the positive
       control: it is what a self-comparison of one file against itself, or an arm that silently
       failed to apply beta, both look like. Zero differences is a failure.
    3. The sidecars carry the ladder. `benchmark-aggregate` records `beta` in each arm's
       <label>.meta.json and nothing currently reads it back. This is the only one of the three
       checks that catches a mistyped non-zero beta -- checks 1 and 2 are beta-blind to anything
       but a mistype to zero, since a single-chunk score does not depend on beta at all and any
       other wrong non-zero beta still moves every multi-chunk pair. The multiset of sidecar betas
       must equal --ladder exactly, and none may exceed --max-beta (default 0.035800, the parity
       endpoint past which the tail term degenerates into a count of tail chunks -- spec §2).

If any check fails, the sweep is not interpreted at all (spec §4).

Stdlib only.

    python3 beta_invariant.py \\
        --hits fs2048-pool.chunks.hits.tsv --keymap keymap.json \\
        --scores-zero fs2048-b0.scores.tsv --scores-parity fs2048-b35800.scores.tsv \\
        --sidecar fs2048-b0.meta.json fs2048-b3387.meta.json fs2048-b6107.meta.json \\
                  fs2048-b11012.meta.json fs2048-b19855.meta.json fs2048-b35800.meta.json \\
        --ladder 0 0.003387 0.006107 0.011012 0.019855 0.035800
"""
import argparse
import json
import math
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tail_stats  # noqa: E402


def load_scores(path):
    """Parses a <label>.scores.tsv file (`benchmark-aggregate --scores-path`): header
    `queryId\\tdocId\\tscore`, TAB-separated, one row per (query, document) pair, an untruncated
    full-precision score (`MaxPassageAggregator.Aggregate` called with `limit=int.MaxValue` -- spec
    §3, unlike the run file's top-50 truncation).

    Returns {(queryId, docId): scoreString}. The score is kept as its EXACT source string, never
    parsed to float and never re-serialized -- the two checks that consume it compare score strings
    for equality/inequality, and spec §4 is explicit that this needs no float tolerance because the
    scores are shortest-round-trippable: parsing and reformatting could only lose that property, not
    improve on it."""
    scores = {}
    with open(path, encoding="utf-8") as f:
        header = f.readline()
        if not header:
            sys.exit(f"{path}: empty file, expected a header row")
        for lineno, line in enumerate(f, start=2):
            line = line.rstrip("\n")
            if not line:
                continue
            fields = line.split("\t")
            if len(fields) != 3:
                sys.exit(f"{path}:{lineno}: expected 3 tab-separated fields, got {len(fields)}: {line!r}")
            query_id, doc_id, score = fields
            try:
                float(score)
            except ValueError:
                sys.exit(f"{path}:{lineno}: non-numeric score {score!r}")
            key = (query_id, doc_id)
            if key in scores:
                sys.exit(f"{path}:{lineno}: duplicate row for (queryId={query_id!r}, docId={doc_id!r})")
            scores[key] = score
    return scores


def load_sidecar_beta(path):
    """Reads back the `beta` a benchmark-aggregate replay recorded in its <label>.meta.json sidecar
    (`BenchmarkAggregateScenario.cs:249`) -- the only field this script reads; nothing else in the
    sidecar (composite, reranker, queryCount, ...) is this check's business."""
    with open(path, encoding="utf-8") as f:
        try:
            sidecar = json.load(f)
        except json.JSONDecodeError as e:
            sys.exit(f"{path}: not valid JSON ({e})")
    if "beta" not in sidecar:
        sys.exit(f"{path}: sidecar has no \"beta\" field")
    beta = sidecar["beta"]
    if isinstance(beta, bool) or not isinstance(beta, (int, float)):
        sys.exit(f"{path}: \"beta\" field is not numeric: {beta!r}")
    return float(beta)


def find_single_and_multi_chunk_pairs(by_doc):
    """Splits the (queryId, docId) keys of `by_doc` (as returned by `tail_stats.resolve_by_doc` --
    the same dump-plus-keymap derivation tail_stats.py performs, spec §4) into pairs with exactly
    one pooled chunk and pairs with two or more. Returns (single_keys, multi_keys)."""
    single = [key for key, doc_scores in by_doc.items() if len(doc_scores) == 1]
    multi = [key for key, doc_scores in by_doc.items() if len(doc_scores) >= 2]
    return single, multi


def check_single_chunk_pairs_identical(single_keys, scores_zero, scores_parity):
    """spec §4 check 1. A pair not present in both scores files is skipped rather than asserted --
    it contributes to neither the pass nor the fail, and the mandatory non-zero count below is what
    stops that silently degenerating into a check that runs over nothing. Fails loudly, naming the
    pair, on the first single-chunk pair whose scores differ; fails loudly, naming the zero count,
    if no pair was assertable at all. Returns the number of pairs asserted identical."""
    asserted = 0
    for key in single_keys:
        if key not in scores_zero or key not in scores_parity:
            continue
        z = scores_zero[key]
        p = scores_parity[key]
        asserted += 1
        if z != p:
            query_id, doc_id = key
            sys.exit(
                f"[beta_invariant] check 1 FAILED: single-chunk pair (queryId={query_id!r}, "
                f"docId={doc_id!r}) has different scores at beta=0 ({z!r}) and beta=parity ({p!r}) "
                f"-- a single-chunk pair has no tail and must score identically at every beta")
    if asserted == 0:
        sys.exit(
            "[beta_invariant] check 1 FAILED: 0 single-chunk pairs asserted -- a count of zero is "
            "a failure of the precondition, not a pass (spec §4)")
    return asserted


def check_multi_chunk_pairs_differ(multi_keys, scores_zero, scores_parity):
    """spec §4 check 2, the positive control. Counts multi-chunk pairs whose beta=0 and
    beta=parity score strings differ; a pair not present in both scores files is skipped, same as
    check 1. Zero differences is a failure -- it is exactly what a self-comparison of one file
    against itself, or an arm that silently failed to apply beta, both look like, and it is the
    only thing distinguishing that outcome from a legitimate pass. Returns the differing count."""
    differing = 0
    for key in multi_keys:
        if key not in scores_zero or key not in scores_parity:
            continue
        if scores_zero[key] != scores_parity[key]:
            differing += 1
    if differing == 0:
        sys.exit(
            "[beta_invariant] check 2 FAILED: 0 multi-chunk pairs differ between beta=0 and "
            "beta=parity -- this is the positive control (spec §4); a zero here means the two "
            "files are the same run compared to itself, or beta was silently not applied")
    return differing


def check_sidecars_carry_the_ladder(betas_by_path, ladder, max_beta):
    """spec §4 check 3 -- the only one of the three that catches a mistyped non-zero beta,
    because a single-chunk score does not depend on beta at all (check 1 is beta-blind by
    construction) and any other wrong non-zero beta still moves every multi-chunk pair (so check 2
    cannot distinguish a right beta from a wrong non-zero one).

    `betas_by_path`: {sidecarPath: beta}, as loaded by `load_sidecar_beta`. Two conditions, checked
    in this order so a value that is both over the bound AND off the ladder is reported for the
    bound (the more specific, more actionable diagnosis): (a) no beta may exceed `max_beta`; (b) the
    multiset of betas must equal the multiset of `ladder` exactly -- order-independent, but every
    value and every duplicate must match. Returns the number of betas checked."""
    for path, beta in betas_by_path.items():
        if beta > max_beta:
            sys.exit(
                f"[beta_invariant] check 3 FAILED: {path} carries beta={beta!r}, which exceeds "
                f"--max-beta {max_beta!r} -- past this bound the tail term degenerates into a "
                f"count of tail chunks (spec §2)")

    got = Counter(betas_by_path.values())
    want = Counter(ladder)
    if got != want:
        sys.exit(
            "[beta_invariant] check 3 FAILED: sidecar beta values do not match --ladder exactly\n"
            f"  sidecars ({len(betas_by_path)}): {sorted(betas_by_path.values())}\n"
            f"  ladder   ({len(ladder)}): {sorted(ladder)}")
    return len(betas_by_path)


def validate_max_beta(max_beta):
    """Guards --max-beta itself, before any of the three checks run. Check 3's bound comparison is
    `beta > max_beta`, and that comparison fails OPEN for a non-finite max_beta:
    `0.358 > float("nan")` and `0.358 > float("inf")` are both False, so a NaN or +Inf --max-beta
    would silently let through the exact out-of-range arm (ten times parity) check 3 exists to
    catch -- and check 3 is the ONLY one of the three checks that can catch it at all, since check 1
    is beta-blind by construction and check 2 passes for any non-zero beta. Mirrors the equivalent
    guard the gated C# applies to the same quantity (`BenchmarkAggregateScenario.cs:70`:
    `!double.IsFinite(flags.Beta) || flags.Beta < 0`)."""
    if not math.isfinite(max_beta) or max_beta < 0:
        sys.exit(
            f"[beta_invariant] --max-beta {max_beta!r} is invalid -- it must be finite and "
            f"non-negative, or check 3's bound comparison fails open")


def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--hits", required=True, help="the pool dump, <label>.chunks.hits.tsv (same format tail_stats.py reads)")
    ap.add_argument("--keymap", required=True, help="keymap.json: flat {parentKey: docId}")
    ap.add_argument("--scores-zero", required=True, help="the beta=0 arm's <label>.scores.tsv (--scores-path output)")
    ap.add_argument("--scores-parity", required=True, help="the beta=parity arm's <label>.scores.tsv (--scores-path output)")
    ap.add_argument("--max-beta", type=float, default=0.035800,
                     help="hard bound no sidecar beta may exceed (default 0.035800, the parity endpoint; spec §2)")
    ap.add_argument("--sidecar", nargs="+", required=True, metavar="PATH",
                     help="one or more <label>.meta.json sidecars, one per ladder arm")
    ap.add_argument("--ladder", nargs="+", type=float, required=True, metavar="BETA",
                     help="the full beta ladder, including 0, in any order")
    return ap


def main():
    args = build_arg_parser().parse_args()
    validate_max_beta(args.max_beta)

    keymap = tail_stats.load_keymap(args.keymap)
    hits = tail_stats.load_hits(args.hits)
    by_doc, unresolved = tail_stats.resolve_by_doc(hits, keymap)
    if unresolved:
        print(f"[beta_invariant] {unresolved} dump row(s) had a parentKey absent from the key map "
              f"-- excluded from every check below")

    single_keys, multi_keys = find_single_and_multi_chunk_pairs(by_doc)

    scores_zero = load_scores(args.scores_zero)
    scores_parity = load_scores(args.scores_parity)

    asserted = check_single_chunk_pairs_identical(single_keys, scores_zero, scores_parity)
    differing = check_multi_chunk_pairs_differ(multi_keys, scores_zero, scores_parity)

    betas_by_path = {path: load_sidecar_beta(path) for path in args.sidecar}
    n_betas = check_sidecars_carry_the_ladder(betas_by_path, args.ladder, args.max_beta)

    print(f"[beta_invariant] check 1 PASSED: {asserted} single-chunk pairs identical at beta=0 and beta=parity")
    print(f"[beta_invariant] check 2 PASSED: {differing} multi-chunk pairs differ between beta=0 and beta=parity")
    print(f"[beta_invariant] check 3 PASSED: {n_betas} sidecar beta values match --ladder, none exceeding {args.max_beta}")


if __name__ == "__main__":
    main()

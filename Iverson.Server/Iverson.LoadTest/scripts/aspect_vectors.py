#!/usr/bin/env python3
"""Screens family 2: does the spread of a document's matched chunk vectors, relative to the
query, proxy the number of distinct query aspects that document covers? Three candidate signals
-- residual_spread, greedy_cover and effective_rank -- are measured against the per-document
aspect count over the 2,733 relevant (query, document) pairs run B retrieved, with n_chunks as
the pre-registered null. Nothing here builds a ranking term and nothing here is restored: restore
is an operator step. See docs/specs/2026-09-13-family2-vector-aspect-screen-design.md.

Chunk identity is not in the hit dump -- it is reconstructed by recomputing each chunk's fused
score and matching it to a recorded one, and that reconstruction IS the faithfulness check (§2.7):
it exercises the query prefix, the model identity, the chunk vectors, the parent_id payload
lookup, the centroid vectors and the fusion weights in a single comparison. Any unmatched or
ambiguous row aborts the run before a statistic is written.

Needs a running Qdrant (both snapshots restored) and TEI. Run with:

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \\
        python3 Iverson.Server/Iverson.LoadTest/scripts/aspect_vectors.py \\
            --run <corpora>/chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.trec \\
            --hits <corpora>/chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.hits.tsv \\
            --queries <corpora>/freshstack-2048-2026-09-07/beir/queries.jsonl \\
            --nugget-qrels <corpora>/freshstack-2048-2026-09-07/qrels.nugget.trec \\
            --key-map <corpora>/freshstack-2048-2026-09-07/keymap.json \\
            --out-dir <corpora>/family2-vector-screen-2026-09-13

Writes, into --out-dir (which is NOT a git repository -- the verdict document transcribes from it
and must say so):
    pair-signals.tsv  -- one row per relevant (query, document) pair: queryId, docId, aspects,
        n_chunks, residual_spread, greedy_cover once per tau, effective_rank. The rho(tau) curve
        is reproducible from these rows alone.
    faithfulness.txt  -- the §2.7 reconstruction: rows checked, unmatched, ambiguous, the maximum
        residual over matched rows, and the verdict. Written whether it passes or fails.
    screen.txt        -- rho per candidate per population, the query-clustered bootstrap CI on
        each difference against n_chunks, Holm-adjusted p, and the rho(tau) curve.
"""
import argparse
import json
import os
import sys

import numpy as np
from scipy import stats as st

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import aspect_oracle  # noqa: E402  (load_run, load_nuggets -- the same files, the same parsers)
import ingest         # noqa: E402
import multivector    # noqa: E402  (scroll, group_rows, collection_info)
import report         # noqa: E402  (holm_adjust)
import tail_stats     # noqa: E402  (load_hits, load_keymap)

TAUS                = (0.80, 0.85, 0.90, 0.95)  # spec §2.2: the curve is published whatever the result
TAU_PRIMARY         = 0.90                      # the pre-registered value
BOOTSTRAP_RESAMPLES = 10_000                    # report.py:114's convention
BOOTSTRAP_SEED      = 20260913                  # report.py:113's convention: a fixed date-shaped integer
MATCH_TOLERANCE     = 5e-7                      # §3.1 pick: 0 ambiguous rows on the screened population,
                                                #      whose minimum within-group gap is 8.94e-07 (15 ULP)
RESIDUAL_EPS        = 1e-9
CENTROID_VECTOR_NAME = "body_centroid"          # P33; multivector.py carries only the chunk-side name
W_BASE              = 0.45                      # V15: VectorRankingOptions.cs:14-16, the weights this dump
W_CENTROID          = 0.45                      #      was produced at. Step 4 of Task 3 is the only check.
MODEL_ID            = "BAAI/bge-base-en-v1.5"   # V18: the model both snapshots were embedded with; selects
                                                #      the query prefix through query_prefix_for
EMBED_URL           = ingest.DEFAULT_EMBED_URL  # http://localhost:8091, the compose tei-embed port

CHUNKS_COLLECTION = "benchmark_documents_chunks_tenant_bypass"
OBJECT_COLLECTION = "benchmark_documents_tenant_bypass"
VECTOR_DIMENSIONS = 768      # E2/E3
EXPECTED_CHUNK_POINTS = 18_622   # E1
EXPECTED_OBJECT_POINTS = 6_000   # E1
EXPECTED_DISTANCE = "Cosine"     # E5

# The pre-registered §2.3 population. NOT preconditions -- the E-list in §3.1 is closed -- but a
# silent drift here would rescope the whole screen, so the observed counts are compared against
# these and the comparison is written into both output files rather than only printed.
EXPECTED_PAIRS = 2_733
EXPECTED_QUERIES = 610
EXPECTED_ROWS = 9_184

# Fixed before the run (Global Constraint 3): Holm's family is these three, in this order.
CANDIDATE_NAMES = ("residual_spread", f"greedy_cover(tau={TAU_PRIMARY:.2f})", "effective_rank")


# ── The three signals, over a unit-normalised C ─────────────────────────────────────────

def residual_spread(C, qhat):
    """Mean pairwise cosine DISTANCE among the query-orthogonal residuals. 0 at n = 1."""
    if len(C) < 2:
        return 0.0
    R = C - np.outer(C @ qhat, qhat)
    norms = np.linalg.norm(R, axis=1)
    if not np.all(norms > RESIDUAL_EPS):
        sys.exit("residual_spread: a chunk vector is parallel to the query; NaN would poison every statistic")
    R = R / norms[:, None]
    iu = np.triu_indices(len(C), 1)
    return float(np.mean(1.0 - (R @ R.T)[iu]))


def greedy_cover(C, scores, tau):
    """Walk chunks in descending score; add one when its max cosine to the cover is below tau. 1 at n = 1.

    The sort is kind="stable" so an exact score tie is broken by pool order rather than by
    numpy's introsort pivot: the signal is pre-registered and must be reproducible."""
    cover = []
    for i in np.argsort(-np.asarray(scores, dtype=np.float64), kind="stable"):
        if not cover or max(float(C[i] @ C[j]) for j in cover) < tau:
            cover.append(i)
    return len(cover)


def effective_rank(C):
    """Participation ratio of the Gram matrix: (sum lambda)^2 / sum lambda^2. 1 at n = 1."""
    lam = np.clip(np.linalg.eigvalsh(C @ C.T), 0.0, None)
    return float(lam.sum() ** 2 / (lam ** 2).sum())


# ── The §2.7 reconstruction ─────────────────────────────────────────────────────────────

def fused(qhat, chunk_vec, centroid_vec, w_base, w_centroid):
    """Every operand is unit-normalised by the caller -- q̂, the chunk vector and the parent centroid
    alike -- for the same reason C is: Qdrant's Cosine normalisation is not relied on."""
    return (w_base * float(qhat @ chunk_vec) + w_centroid * float(qhat @ centroid_vec)) / (w_base + w_centroid)


def match_pool(recorded_scores, cand_vectors, centroid_vec, qhat, w_base, w_centroid):
    """Match each recorded score to the unique chunk of this parent that reproduces it.
    Returns (matched_indices, n_unmatched, n_ambiguous). C is the matched set."""
    recomputed = [fused(qhat, v, centroid_vec, w_base, w_centroid) for v in cand_vectors]
    matched, unmatched, ambiguous = [], 0, 0
    for s in recorded_scores:
        hits = [i for i, r in enumerate(recomputed) if abs(r - s) <= MATCH_TOLERANCE]
        if len(hits) == 0:
            unmatched += 1
        elif len(hits) > 1:
            ambiguous += 1
        else:
            matched.append(hits[0])
    return matched, unmatched, ambiguous


def pair_row(query_id, doc_id, parent_key, aspects, qhat, candidates, centroid, recorded):
    """One (query, document) pair: the §2.7 reconstruction, then the three signals over the
    matched set. This is the seam between the verified reconstruction and the verified
    statistics, which is why it is a function rather than the body of main()'s loop.

    `candidates` is the parent's WHOLE chunk set; `recorded` is the fused score of every hit this
    query's pool recorded for that parent, in dump order. `matched[k]` is the chunk that
    reproduces `recorded[k]`, so C's rows and `recorded` name the same chunks in the same order --
    which is what lets greedy_cover walk the pool in recorded-score order -- and `n_chunks` counts
    C, the pool's chunks, never `candidates` (Global Constraint 7).

    Returns a result dict: `row` (None unless the pair produced a signal row), the three
    faithfulness counters, `max_residual` over this pair's matched rows (None when it has none),
    and at most one of `recon_failure` / `signal_failure` as a message for the caller to collect."""
    matched, n_unmatched, n_ambiguous = match_pool(
        recorded, candidates, centroid, qhat, W_BASE, W_CENTROID)
    # Two recorded rows can each match ONE chunk uniquely -- their scores need only lie within a
    # tolerance window of each other -- and match_pool's own counters cannot see it. Unchecked, C
    # would carry that vector twice and n_chunks would overcount, so it falsifies the
    # reconstruction exactly as an unmatched row does.
    n_duplicated = len(matched) - len(set(matched))
    result = {"row": None, "n_unmatched": n_unmatched, "n_ambiguous": n_ambiguous,
              "n_duplicated": n_duplicated, "max_residual": None,
              "recon_failure": None, "signal_failure": None}

    if n_unmatched or n_ambiguous or n_duplicated:
        # match_pool's indices only align 1:1 with `recorded` when nothing failed, so a failing
        # pair contributes no residual and no signal row. The run aborts once faithfulness.txt is
        # written, so neither is ever needed.
        result["recon_failure"] = (
            f"{query_id} / {doc_id} (parent {parent_key}): {len(recorded)} recorded row(s), "
            f"{n_unmatched} unmatched, {n_ambiguous} ambiguous, {n_duplicated} duplicate assignment(s)")
        return result

    if matched:
        result["max_residual"] = max(
            abs(fused(qhat, candidates[i], centroid, W_BASE, W_CENTROID) - s)
            for s, i in zip(recorded, matched))

    C = candidates[matched]
    row = {
        "query_id": query_id,
        "doc_id": doc_id,
        "aspects": aspects,
        "n_chunks": len(matched),      # chunks IN THE POOL -- the set C is drawn from
        "residual_spread": residual_spread(C, qhat),
        "covers": {tau: greedy_cover(C, recorded, tau) for tau in TAUS},
        "effective_rank": effective_rank(C),
    }
    non_finite = [k for k in ("residual_spread", "effective_rank") if not np.isfinite(row[k])]
    if non_finite:
        # Collected rather than exited on, so faithfulness.txt is still written: a non-finite
        # signal over a cleanly reconstructed pool is a different diagnosis from a broken
        # reconstruction, and the operator needs to see which of the two happened.
        result["signal_failure"] = (f"{query_id} / {doc_id}: non-finite {non_finite} over "
                                    f"{len(matched)} chunk(s)")
        return result
    result["row"] = row
    return result


def unit(vec, what):
    """Explicit unit-normalisation. Qdrant's Cosine normalisation is not relied on, here or in C."""
    arr = np.asarray(vec, dtype=np.float64)
    norm = float(np.linalg.norm(arr))
    if not np.isfinite(norm) or norm <= RESIDUAL_EPS:
        sys.exit(f"{what}: zero-length or non-finite vector (norm {norm!r}); every downstream cosine is undefined")
    return arr / norm


# ── Preconditions (§3.1 E1-E6) ──────────────────────────────────────────────────────────

def vector_params(info, name):
    """The named vector's {size, distance} block, or None when the collection has no such vector.
    Qdrant returns config.params.vectors as a name -> params map for named-vector collections."""
    vectors = (info or {}).get("config", {}).get("params", {}).get("vectors")
    if not isinstance(vectors, dict):
        return None
    params = vectors.get(name)
    return params if isinstance(params, dict) else None


def precondition_failures(chunk_info, object_info, chunk_point, object_point, keymap_keys):
    """E1-E6 as a list of human-readable failures ([] means every precondition holds). Pure, so
    the whole E-list is exercised offline; main() supplies the two collection infos and one
    probe point per collection, each scrolled WITH vectors (which is E6)."""
    failures = []

    if chunk_info is None:
        return [f"E1: collection '{CHUNKS_COLLECTION}' does not exist"] + (
            [] if object_info is not None else [f"E1: collection '{OBJECT_COLLECTION}' does not exist"])
    if object_info is None:
        return [f"E1: collection '{OBJECT_COLLECTION}' does not exist"]

    # E1 -- scale
    for info, name, expected in ((chunk_info, CHUNKS_COLLECTION, EXPECTED_CHUNK_POINTS),
                                 (object_info, OBJECT_COLLECTION, EXPECTED_OBJECT_POINTS)):
        actual = info.get("points_count")
        if actual != expected:
            failures.append(f"E1: {name} reports {actual} points, expected {expected}")

    # E2/E3 -- geometry; E5 -- metric
    for info, name, vector_name in ((chunk_info, CHUNKS_COLLECTION, multivector.CHUNK_VECTOR_NAME),
                                    (object_info, OBJECT_COLLECTION, CENTROID_VECTOR_NAME)):
        params = vector_params(info, vector_name)
        if params is None:
            failures.append(f"E2/E3: {name} has no vector named '{vector_name}'")
            continue
        if params.get("size") != VECTOR_DIMENSIONS:
            failures.append(f"E2/E3: {name}.{vector_name} is {params.get('size')}-dimensional, "
                            f"expected {VECTOR_DIMENSIONS}")
        if params.get("distance") != EXPECTED_DISTANCE:
            failures.append(f"E5: {name}.{vector_name} distance is {params.get('distance')!r}, "
                            f"expected {EXPECTED_DISTANCE!r}")

    # E6 -- the scroll API returns vectors, for both collections
    for point, name, vector_name in ((chunk_point, CHUNKS_COLLECTION, multivector.CHUNK_VECTOR_NAME),
                                     (object_point, OBJECT_COLLECTION, CENTROID_VECTOR_NAME)):
        if point is None:
            failures.append(f"E6: scroll of {name} returned no points")
            continue
        vector = (point.get("vector") or {}).get(vector_name)
        if vector is None:
            failures.append(f"E6: scroll of {name} returned no '{vector_name}' vector for point {point.get('id')!r}")
        elif len(vector) != VECTOR_DIMENSIONS:
            failures.append(f"E6: scroll of {name} returned a {len(vector)}-component '{vector_name}' "
                            f"for point {point.get('id')!r}, expected {VECTOR_DIMENSIONS}")

    # E4 -- chunk payloads carry parent_id, and its value form is a key of keymap.json
    if chunk_point is not None:
        parent_id = (chunk_point.get("payload") or {}).get("parent_id")
        if parent_id is None:
            failures.append(f"E4: chunk point {chunk_point.get('id')!r} carries no 'parent_id' payload field")
        elif parent_id not in keymap_keys:
            failures.append(f"E4: chunk point {chunk_point.get('id')!r} has parent_id {parent_id!r}, "
                            f"which is not a key of the key map")

    return failures


def probe_point(collection, payload_fields):
    """The first point of a vectors-and-payload scroll, or None on an empty collection. E6 is
    exactly "this returns a vector", so the probe must ask for one."""
    for point in multivector.scroll(collection, True, payload_fields):
        return point
    return None


# ── Population ──────────────────────────────────────────────────────────────────────────

def invert_keymap(keymap):
    """docId -> parentKey. keymap.json is a flat {parentKey: docId} object; two parent keys
    sharing a docId would make the pair -> parent join ambiguous, so that aborts rather than
    silently picking one."""
    inverse = {}
    for parent_key, doc_id in keymap.items():
        if doc_id in inverse:
            sys.exit(f"key map: docId {doc_id!r} is claimed by two parent keys "
                     f"({inverse[doc_id]!r} and {parent_key!r}); the pair -> parent join is ambiguous")
        inverse[doc_id] = parent_key
    return inverse


def build_population(run, nuggets):
    """The §2.3 primary population: every (query, document) pair among run B's top 50 that the
    nugget qrels judge relevant, in run-file order. Relevance is nugget membership -- V24: the
    document qrels and the nugget qrels induce the identical relevance set on this corpus.
    Returns [(queryId, docId, aspect_count), ...]."""
    pairs = []
    for query_id, docs in run.items():
        query_nuggets = nuggets.get(query_id, {})
        for doc_id in docs:
            if doc_id in query_nuggets:
                pairs.append((query_id, doc_id, len(query_nuggets[doc_id])))
    return pairs


def hits_by_group(hit_rows, wanted_groups):
    """{(queryId, parentKey): [score, ...]} in dump order, restricted to the population's groups
    (spec §2.7: 2,733 groups and 9,184 rows, not the dump's 172,704 groups)."""
    grouped = {}
    for query_id, parent_key, score in hit_rows:
        key = (query_id, parent_key)
        if key in wanted_groups:
            grouped.setdefault(key, []).append(score)
    return grouped


# ── Statistics ──────────────────────────────────────────────────────────────────────────

def spearman_against_first(matrix):
    """Spearman rho of every column after the first against column 0, as a 1-D array. One
    rankdata pass over the whole matrix per call, which is why the bootstrap can afford 10,000
    resamples of four series."""
    statistic = np.asarray(st.spearmanr(matrix).statistic, dtype=np.float64)
    if statistic.ndim == 0:      # scipy returns a scalar for exactly two columns
        return np.array([float(statistic)])
    return np.asarray(statistic[0, 1:], dtype=np.float64)


def pairs_by_query(query_ids):
    """(unique query ids in first-appearance order, {query id: index array of its pairs}). The
    resampling unit is the query (Global Constraint 4) -- pairs within a query share a query
    vector and are not independent -- so the bootstrap needs each query's pairs as one block."""
    order, blocks = [], {}
    for i, query_id in enumerate(query_ids):
        if query_id not in blocks:
            order.append(query_id)
            blocks[query_id] = []
        blocks[query_id].append(i)
    return order, {q: np.asarray(idx, dtype=np.int64) for q, idx in blocks.items()}


def resample_pair_indices(unique_ids, blocks, rng):
    """One query-clustered resample: draw len(unique_ids) QUERY ids with replacement and
    concatenate every pair of each drawn query. Never draws a pair."""
    drawn = rng.integers(0, len(unique_ids), size=len(unique_ids))
    return np.concatenate([blocks[unique_ids[d]] for d in drawn])


def bootstrap_differences(matrix, query_ids, n_resamples=BOOTSTRAP_RESAMPLES, seed=BOOTSTRAP_SEED):
    """matrix columns are [aspects, candidate..., n_chunks]. Returns (rho draws, difference
    draws) with one row per resample; the difference is rho_candidate - rho_n_chunks within
    the SAME resample, which is the statistic §2.4's pass rule is written against."""
    unique_ids, blocks = pairs_by_query(query_ids)
    rng = np.random.default_rng(seed)
    draws = np.empty((n_resamples, matrix.shape[1] - 1), dtype=np.float64)
    for b in range(n_resamples):
        draws[b] = spearman_against_first(matrix[resample_pair_indices(unique_ids, blocks, rng)])
    return draws, draws[:, :-1] - draws[:, [-1]]


def bootstrap_p(diff_samples):
    """Two-sided p from the resample distribution of the difference: twice the smaller tail
    mass on either side of zero. The (count + 1) / (B + 1) form keeps p strictly positive --
    10,000 resamples cannot evidence a p below 1e-4, and printing 0.0 would claim they can."""
    b = len(diff_samples)
    at_most = int(np.count_nonzero(np.asarray(diff_samples) <= 0.0))
    at_least = int(np.count_nonzero(np.asarray(diff_samples) >= 0.0))
    return min(1.0, 2.0 * (min(at_most, at_least) + 1) / (b + 1))


def holm_family(pvalues):
    """The three candidates' p-values, Holm-adjusted as ONE family of 3 (Global Constraint 3).
    Delegates to report.holm_adjust -- there is no local Holm here, deliberately."""
    if len(pvalues) != len(CANDIDATE_NAMES):
        sys.exit(f"holm_family: the pre-registered family size is {len(CANDIDATE_NAMES)}, got {len(pvalues)}")
    return report.holm_adjust(list(pvalues))


def signal_matrix(rows, tau):
    """[aspects, residual_spread, greedy_cover(tau), effective_rank, n_chunks] -- column 0 the
    target, the last column the null, the middle three the candidates in CANDIDATE_NAMES order."""
    return np.array([[r["aspects"], r["residual_spread"], r["covers"][tau], r["effective_rank"], r["n_chunks"]]
                     for r in rows], dtype=np.float64)


def analyse(rows, tau, n_resamples=BOOTSTRAP_RESAMPLES, seed=BOOTSTRAP_SEED):
    """rho per candidate, the bootstrap CI and p on each difference against n_chunks, for one
    population at one tau. Holm is applied by the caller, to the primary population only."""
    matrix = signal_matrix(rows, tau)
    query_ids = [r["query_id"] for r in rows]
    point = spearman_against_first(matrix)
    draws, diffs = bootstrap_differences(matrix, query_ids, n_resamples, seed)
    if not np.all(np.isfinite(draws)):
        sys.exit(f"bootstrap: {int(np.count_nonzero(~np.isfinite(draws)))} non-finite rho value(s) among "
                 f"{draws.size} draws -- a resample had a zero-variance column; the population is degenerate "
                 f"and no CI from it is meaningful")
    return {
        "n_pairs": len(rows),
        "n_queries": len(set(query_ids)),
        "rho": {name: float(point[i]) for i, name in enumerate(CANDIDATE_NAMES)},
        "rho_null": float(point[-1]),
        "diff": {name: float(point[i] - point[-1]) for i, name in enumerate(CANDIDATE_NAMES)},
        "ci": {name: tuple(float(x) for x in np.percentile(diffs[:, i], (2.5, 97.5)))
               for i, name in enumerate(CANDIDATE_NAMES)},
        "p": {name: bootstrap_p(diffs[:, i]) for i, name in enumerate(CANDIDATE_NAMES)},
    }


def rho_curve(rows):
    """rho of greedy_cover against the aspect count at each pre-registered tau. Published
    whatever the result (§2.2) -- tau steered an outcome on this project once already."""
    return {tau: float(spearman_against_first(signal_matrix(rows, tau))[1]) for tau in TAUS}


def verdict_line(result, name, adjusted_p):
    """§2.4's pass rule, applied to one candidate: rho above the null, a 95 % CI on the
    difference that excludes zero, and Holm-adjusted p < 0.05."""
    low, high = result["ci"][name]
    passed = result["diff"][name] > 0.0 and low > 0.0 and adjusted_p < 0.05
    return "PASS" if passed else "FAIL"


# ── Output ──────────────────────────────────────────────────────────────────────────────

def population_note(n_pairs, n_queries, n_rows):
    """The observed §2.3 counts against the pre-registered ones. Recorded in both output files
    because a drift here rescopes the screen without changing a single line of its arithmetic."""
    observed = (n_pairs, n_queries, n_rows)
    expected = (EXPECTED_PAIRS, EXPECTED_QUERIES, EXPECTED_ROWS)
    line = (f"population: {n_pairs} pairs / {n_queries} queries / {n_rows} recorded rows "
            f"(pre-registered: {EXPECTED_PAIRS} / {EXPECTED_QUERIES} / {EXPECTED_ROWS})")
    if observed != expected:
        return line + "\n*** WARNING: the screened population is NOT the pre-registered one ***"
    return line


def write_faithfulness(path, checked, unmatched, ambiguous, duplicated, max_residual,
                       n_pairs, n_queries, failures):
    with open(path, "w", encoding="utf-8") as f:
        f.write("§2.7 reconstruction / faithfulness check\n")
        f.write("=" * 72 + "\n\n")
        f.write(population_note(n_pairs, n_queries, checked) + "\n")
        f.write(f"weights: W_base {W_BASE} / W_centroid {W_CENTROID}; "
                f"model {MODEL_ID}; match tolerance {MATCH_TOLERANCE:.3e}\n\n")
        f.write(f"{'rows checked':<34}: {checked}\n")
        f.write(f"{'rows with no match':<34}: {unmatched}\n")
        f.write(f"{'rows with an ambiguous match':<34}: {ambiguous}\n")
        f.write(f"{'rows with a duplicate match':<34}: {duplicated}\n")
        f.write(f"{'maximum residual over matched rows':<34}: "
                f"{'n/a' if max_residual is None else f'{max_residual:.6e}'}\n\n")
        f.write(f"verdict: {'PASS' if not failures else 'FAIL'}\n")
        for failure in failures[:20]:
            f.write(f"  - {failure}\n")
        if len(failures) > 20:
            f.write(f"  - ... and {len(failures) - 20} more\n")
        f.write("\nThe unmatched and ambiguous counts are the falsifying statistics: each says a\n"
                "recorded score could not be assigned to exactly one of its parent's chunks, which\n"
                "impugns the query prefix, the model, the fusion weights, the parent_id lookup or\n"
                "the restored collections together, without distinguishing them (§5). The maximum\n"
                "residual is NOT a falsifying statistic -- it is a selection artifact of the\n"
                "matching, bounded by the match tolerance by construction, because a row whose\n"
                "residual exceeded it would have been counted as unmatched instead. A duplicate\n"
                "match -- two recorded rows assigned to one chunk, which match_pool's own counters\n"
                "cannot see because each row matched uniquely -- falsifies the reconstruction the\n"
                "same way an unmatched row does, and is counted beside them.\n")


def write_pair_signals(path, rows):
    header = (["queryId", "docId", "aspects", "n_chunks", "residual_spread"]
              + [f"greedy_cover_tau{tau:.2f}" for tau in TAUS] + ["effective_rank"])
    with open(path, "w", encoding="utf-8") as f:
        f.write("\t".join(header) + "\n")
        for r in rows:
            f.write("\t".join([
                r["query_id"], r["doc_id"], str(r["aspects"]), str(r["n_chunks"]),
                f"{r['residual_spread']:.6f}",
                *[str(r["covers"][tau]) for tau in TAUS],
                f"{r['effective_rank']:.6f}",
            ]) + "\n")


def write_screen(path, primary, sensitivity, adjusted, curves, n_rows):
    with open(path, "w", encoding="utf-8") as f:
        f.write("family 2 vector-aspect screen\n")
        f.write("=" * 72 + "\n\n")
        f.write(population_note(primary["n_pairs"], primary["n_queries"], n_rows) + "\n")
        f.write(f"sensitivity population (>= 2 aspects and >= 2 chunks): "
                f"{sensitivity['n_pairs']} pairs / {sensitivity['n_queries']} queries\n")
        f.write(f"bootstrap: {BOOTSTRAP_RESAMPLES} resamples of QUERIES (never pairs), seed {BOOTSTRAP_SEED}\n")
        f.write(f"primary tau: {TAU_PRIMARY:.2f}; Holm family size {len(CANDIDATE_NAMES)}, "
                f"fixed before the run\n\n")

        for label, result, adj in (("PRIMARY (decides)", primary, adjusted),
                                   ("SENSITIVITY (descriptive)", sensitivity, None)):
            f.write(f"-- {label}: {result['n_pairs']} pairs, {result['n_queries']} queries "
                    f"--------------------\n")
            f.write(f"   null  n_chunks             rho = {result['rho_null']:+.4f}\n")
            for i, name in enumerate(CANDIDATE_NAMES):
                low, high = result["ci"][name]
                f.write(f"   {name:<26} rho = {result['rho'][name]:+.4f}  "
                        f"diff = {result['diff'][name]:+.4f}  "
                        f"95% CI [{low:+.4f}, {high:+.4f}]  p = {result['p'][name]:.4f}")
                if adj is None:
                    f.write("   (not in the pre-registered family)\n")
                else:
                    f.write(f"  Holm p = {adj[i]:.4f}  {verdict_line(result, name, adj[i])}\n")
            f.write("\n")

        f.write("-- rho(tau) for greedy_cover, published whatever the result (§2.2) ------------\n")
        for label, curve in curves.items():
            f.write(f"   {label:<26}" + "  ".join(f"tau {tau:.2f}: {curve[tau]:+.4f}" for tau in TAUS) + "\n")
        f.write("\nA candidate passes iff its rho exceeds n_chunks' rho, the 95 % CI on the\n"
                "difference excludes zero, and its Holm-adjusted p is below 0.05, on the PRIMARY\n"
                "population. The sensitivity population is reported alongside and decides nothing.\n"
                "A pass says a signal exists, not that a term built on it would move a metric: the\n"
                "aspect-coverage gate puts a realised term at about +0.002 against an MDE of 0.0097.\n")


# ── Main ────────────────────────────────────────────────────────────────────────────────

def load_queries(path):
    """{queryId: text} from BEIR queries.jsonl."""
    queries = {}
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            query_id, text = row.get("_id"), row.get("text")
            if not query_id or not text:
                sys.exit(f"{path}:{lineno}: query row has an empty _id or text: {row!r}")
            queries[query_id] = text
    return queries


def main():
    parser = argparse.ArgumentParser(
        description="Screen chunk-vector spread signals against per-document query-aspect counts.")
    parser.add_argument("--run", required=True, help="run B: the TREC file of the top 50 documents per query")
    parser.add_argument("--hits", required=True, help="<label>.chunks.hits.tsv, the chunk-hit dump")
    parser.add_argument("--queries", required=True, help="BEIR queries.jsonl")
    parser.add_argument("--nugget-qrels", required=True, help="TREC nugget (aspect) qrels file")
    parser.add_argument("--key-map", required=True, help="keymap.json: flat {parentKey: docId}")
    parser.add_argument("--out-dir", required=True, help="directory to write the three outputs into")
    args = parser.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)

    run = aspect_oracle.load_run(args.run)
    nuggets = aspect_oracle.load_nuggets(args.nugget_qrels)
    keymap = tail_stats.load_keymap(args.key_map)
    doc_to_parent = invert_keymap(keymap)
    query_texts = load_queries(args.queries)

    pairs = build_population(run, nuggets)
    if not pairs:
        sys.exit("the population is empty: no document of run B is judged relevant by the nugget qrels")
    missing_docs = sorted({d for _, d, _ in pairs if d not in doc_to_parent})
    if missing_docs:
        sys.exit(f"{len(missing_docs)} population docId(s) are absent from the key map, e.g. {missing_docs[:5]}")
    missing_queries = sorted({q for q, _, _ in pairs if q not in query_texts})
    if missing_queries:
        sys.exit(f"{len(missing_queries)} population query id(s) are absent from {args.queries}, "
                 f"e.g. {missing_queries[:5]}")

    groups = [(q, doc_to_parent[d]) for q, d, _ in pairs]
    hit_groups = hits_by_group(tail_stats.load_hits(args.hits), set(groups))
    empty = [g for g in groups if g not in hit_groups]
    if empty:
        sys.exit(f"{len(empty)} population (query, parent) group(s) have no row in {args.hits}, "
                 f"e.g. {empty[:3]} -- §2.3 records that all {EXPECTED_PAIRS} have at least one chunk "
                 f"in the pool, so this is a mismatched input, not an empty signal")
    n_rows = sum(len(hit_groups[g]) for g in groups)
    print(f"[aspect_vectors] {population_note(len(pairs), len({q for q, _, _ in pairs}), n_rows)}")

    # -- §3.1 preconditions, before anything is embedded or scrolled in bulk ----------------
    chunk_info = multivector.collection_info(CHUNKS_COLLECTION)
    object_info = multivector.collection_info(OBJECT_COLLECTION)
    failures = precondition_failures(
        chunk_info, object_info,
        probe_point(CHUNKS_COLLECTION, ["parent_id", "chunk_index"]) if chunk_info else None,
        probe_point(OBJECT_COLLECTION, ["key", "docId"]) if object_info else None,
        set(keymap))
    if failures:
        sys.exit("execution-time precondition failure(s):\n  - " + "\n  - ".join(failures))
    print(f"[aspect_vectors] preconditions E1-E6 hold: {chunk_info['points_count']} chunk points, "
          f"{object_info['points_count']} object points")

    # -- vectors ---------------------------------------------------------------------------
    needed_parents = {p for _, p in groups}
    raw_groups = multivector.group_rows(multivector.scroll(CHUNKS_COLLECTION, True, ["parent_id", "chunk_index"]))
    unmapped = sorted(set(raw_groups) - set(keymap))
    if unmapped:
        sys.exit(f"E4: {len(unmapped)} chunk parent_id value(s) are not keys of the key map, "
                 f"e.g. {unmapped[:5]}")
    absent = sorted(needed_parents - set(raw_groups))
    if absent:
        sys.exit(f"{len(absent)} population parent(s) have no chunk in {CHUNKS_COLLECTION}, e.g. {absent[:5]}")
    chunk_vectors = {key: np.vstack([unit(v, f"chunk of parent {key}") for v in raw_groups[key]])
                     for key in needed_parents}
    del raw_groups
    print(f"[aspect_vectors] scrolled chunk vectors for {len(chunk_vectors)} parents")

    centroids = {}
    for point in multivector.scroll(OBJECT_COLLECTION, True, ["key", "docId"]):
        key = point["payload"]["key"]
        if key in needed_parents:
            vector = (point.get("vector") or {}).get(CENTROID_VECTOR_NAME)
            if vector is None:
                # E6 probes the first point of each collection; this is the same check over the
                # points that actually carry the screen, reported rather than raised as a KeyError.
                sys.exit(f"object point {point['id']!r} (parent {key}) carries no "
                         f"'{CENTROID_VECTOR_NAME}' vector; its fused score cannot be reproduced")
            centroids[key] = unit(vector, f"centroid of parent {key}")
    missing_centroids = sorted(needed_parents - set(centroids))
    if missing_centroids:
        sys.exit(f"{len(missing_centroids)} population parent(s) have no object point carrying "
                 f"'{CENTROID_VECTOR_NAME}', e.g. {missing_centroids[:5]}")
    print(f"[aspect_vectors] scrolled centroids for {len(centroids)} parents")

    query_prefix = ingest.query_prefix_for(MODEL_ID)
    print(f"[aspect_vectors] query prefix for {MODEL_ID}: {query_prefix!r}")
    query_vectors = {}
    query_order = sorted({q for q, _, _ in pairs})
    for i, query_id in enumerate(query_order, start=1):
        query_vectors[query_id] = unit(
            ingest.embed(query_texts[query_id], MODEL_ID, query_prefix, EMBED_URL), f"query {query_id}")
        if i % 50 == 0 or i == len(query_order):
            print(f"[aspect_vectors] embedded {i}/{len(query_order)} queries")

    # -- reconstruction and signals, one pass ------------------------------------------------
    rows = []
    checked = unmatched_total = ambiguous_total = duplicated_total = 0
    max_residual = None
    recon_failures, signal_failures = [], []
    for (query_id, doc_id, aspects), group in zip(pairs, groups):
        recorded = hit_groups[group]
        checked += len(recorded)
        result = pair_row(query_id, doc_id, group[1], aspects, query_vectors[query_id],
                          chunk_vectors[group[1]], centroids[group[1]], recorded)
        unmatched_total += result["n_unmatched"]
        ambiguous_total += result["n_ambiguous"]
        duplicated_total += result["n_duplicated"]
        if result["max_residual"] is not None:
            max_residual = (result["max_residual"] if max_residual is None
                            else max(max_residual, result["max_residual"]))
        if result["recon_failure"]:
            recon_failures.append(result["recon_failure"])
        elif result["signal_failure"]:
            signal_failures.append(result["signal_failure"])
        else:
            rows.append(result["row"])

    faithfulness_path = os.path.join(args.out_dir, "faithfulness.txt")
    write_faithfulness(faithfulness_path, checked, unmatched_total, ambiguous_total, duplicated_total,
                       max_residual, len(pairs), len(query_order), recon_failures)
    print(f"[aspect_vectors] wrote {faithfulness_path}")
    if recon_failures:
        sys.exit(f"§2.7 reconstruction FAILED: {unmatched_total} unmatched, {ambiguous_total} ambiguous "
                 f"and {duplicated_total} duplicate row(s) over {len(recon_failures)} group(s) -- see "
                 f"{faithfulness_path}. No statistic has been written.")
    if signal_failures:
        sys.exit(f"non-finite signal value(s) on {len(signal_failures)} pair(s), e.g. "
                 f"{signal_failures[:3]} -- every statistic downstream would be poisoned. "
                 f"The reconstruction itself passed; see {faithfulness_path}.")

    # -- statistics ---------------------------------------------------------------------------
    sensitivity_rows = [r for r in rows if r["aspects"] >= 2 and r["n_chunks"] >= 2]
    if len(sensitivity_rows) < 2:
        sys.exit(f"the sensitivity population holds {len(sensitivity_rows)} pair(s); no correlation is defined")
    primary = analyse(rows, TAU_PRIMARY)
    sensitivity = analyse(sensitivity_rows, TAU_PRIMARY)
    adjusted = holm_family([primary["p"][name] for name in CANDIDATE_NAMES])
    curves = {"primary": rho_curve(rows), "sensitivity": rho_curve(sensitivity_rows)}

    write_pair_signals(os.path.join(args.out_dir, "pair-signals.tsv"), rows)
    write_screen(os.path.join(args.out_dir, "screen.txt"), primary, sensitivity, adjusted, curves, checked)
    print(f"[aspect_vectors] wrote {len(rows)} pair rows and the screen to {args.out_dir}")
    for i, name in enumerate(CANDIDATE_NAMES):
        low, high = primary["ci"][name]
        print(f"[aspect_vectors] {name}: rho {primary['rho'][name]:+.4f} vs null "
              f"{primary['rho_null']:+.4f}, diff {primary['diff'][name]:+.4f} "
              f"95% CI [{low:+.4f}, {high:+.4f}], Holm p {adjusted[i]:.4f} "
              f"{verdict_line(primary, name, adjusted[i])}")


if __name__ == "__main__":
    main()

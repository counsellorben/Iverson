#!/usr/bin/env python3
"""Structural checks and ir_measures scoring for TREC run files, plus an ingest-throughput
report read from ingest.py's stats sidecar.

Four things, in order, per invocation:

    1. Structural checks on each run file -- rows, distinct queries, count of non-zero
       scores, duplicate doc ids within a query, and qrels query coverage (how many of the
       qrels file's queries this run actually has rows for). These are exactly the
       malformations a TREC scorer either rejects outright or silently collapses, so they
       are surfaced before any score is trusted -- coverage included: a query present in
       qrels but absent from the run contributes zero and drags every aggregate down without
       raising an error, so a 300-query run scored against a 40-query qrels file (or vice
       versa) must be visible here, not discovered by a suspiciously low score later.

    2. ir_measures scoring -- nDCG@10, R@50, AP against a qrels file, one line per run, plus
       the run's build composite (read from its <config-label>.meta.json sidecar, written by
       BenchmarkQueryScenario -- see "Build identity" below) so a comparison across runs from
       different binaries cannot pass unnoticed. With --nugget-qrels, alpha_nDCG@10 (pyndeval)
       is added too, scored against the nugget/subtopic qrels instead of the query-level
       qrels -- see freshstack_nugget_qrels.py for producing that file from FreshStack's
       per-topic subtopic judgments. A *.chunks.trec run also prints its chunk-diversity
       means (meanDistinctParentsAt10/50) when a <label>.chunks.diversity.json sidecar,
       written by benchmark-query, sits beside it.

    3. Ingest throughput, read from --stats-path (ingest.py's <key-map-path>.stats.json
       sidecar, if given and present): documents, chunks, embed calls, embeds saved by the
       reuse gate, docs/hour, seconds/embed, and the headline -- measured seconds/document
       against the ~34s/document of the full gRPC/Kafka pipeline (see ingest.py's module
       docstring). Throughput is derived from the sidecar's elapsed_seconds (the SUM of
       each invocation's own working time), never from started_at/finished_at: those two
       fields span first-start to last-finish and, after a --resume across a gap, include
       idle time between invocations that elapsed_seconds does not. The span is still
       printed, clearly labelled, for reference. A sidecar written before elapsed_seconds
       existed falls back to the span for throughput, with a printed notice that the figure
       may include idle time.

    4. Paired statistics against --baseline, if given: for each of the measures above --
       three, or four with --nugget-qrels, since alpha_nDCG@10 gets its own compare block
       like every other measure -- every other discovered run is compared to the baseline
       over the intersection of their query sets -- delta, paired t-test, a seeded sign-flip
       permutation test, a 95% CI and Cohen's d_z on the differences, the minimum detectable
       effect at 80% power, the count of queries whose value actually changed, and a
       Holm-corrected p-value across the runs compared within that measure. A SciFact
       centroid recall claim was once retracted after a paired t-test on a distribution that
       was 98.3% exact zeros -- only 5 of 300 queries had changed at all, which a sign-flip
       permutation test caught and the t-test's own assumptions did not protect against.
       Below 10% changed queries this section prints a banner saying so and naming the
       permutation p as the one to trust. With `--pair RUN=BASELINE` (repeatable) the family
       is exactly the declared pairs, each checked for pool invariance first; `--baseline`
       keeps the discover-everything behaviour.

Build identity: BenchmarkQueryScenario writes one <config-label>.meta.json sidecar per
invocation, alongside its two run files, carrying a "composite" key -- a hash over every
Iverson.* assembly's MVID, identifying exactly what code produced the run. A run file's
sidecar is found by stripping the trailing .trec, then a trailing .similar or .chunks if
present, then appending .meta.json: runs/reference.chunks.trec and runs/reference.similar.trec
both resolve to runs/reference.meta.json. A run with no sidecar (every run file predating this
work) reports "build: unknown" and is excluded from the mismatch check rather than silently
counted as agreeing.

Run-file discovery: --run may be passed more than once, and each value is either a run file
directly or a directory, in which case every *.trec file inside it (sorted) is scored. This
reads more naturally than a separate --run-dir flag: "score these paths" stays one concept
whether a path names a file or a directory of files, and multiple --run flags can still mix
individual files with directories in one invocation.

Not stdlib-only, unlike this directory's other scripts: ir_measures, numpy, scipy and (for
--nugget-qrels) pyndeval are the third-party imports this project permits (P3 of the parent
plan; numpy/scipy back the paired statistics in section 4; pyndeval is ir_measures' provider
for alpha_nDCG and is what the ImportError guard below sends the reader to install). They are
reached through PYTHONPATH, never a site-packages install -- this box is PEP 668
externally-managed with no working venv:

    python3 -m pip install --target /path/to/libs ir_measures numpy scipy pyndeval

    PYTHONPATH=/path/to/libs python3 \\
        Iverson.Server/Iverson.LoadTest/scripts/report.py \\
        --run /path/to/runs-dir --qrels /path/to/qrels.trec

    # Individual files instead of a directory, and the ingest stats sidecar:
    PYTHONPATH=/path/to/libs python3 \\
        Iverson.Server/Iverson.LoadTest/scripts/report.py \\
        --run /path/to/baseline.chunks.trec --run /path/to/baseline.similar.trec \\
        --qrels /path/to/qrels.trec --stats-path /path/to/keymap.json.stats.json

    # Paired statistics against a reference run:
    PYTHONPATH=/path/to/libs python3 \\
        Iverson.Server/Iverson.LoadTest/scripts/report.py \\
        --run /path/to/runs-dir --qrels /path/to/qrels.trec \\
        --baseline /path/to/runs-dir/reference.chunks.trec

CRITICAL: score with measure OBJECTS, never ir_measures.parse_measure's string form
("nDCG@10"). parse_measure calls ast.Num, removed in Python 3.12, and raises AttributeError
on this box's 3.14. Import the measures and build the list directly:

    from ir_measures import nDCG, R, AP
    ir_measures.calc_aggregate([nDCG@10, R@50, AP], qrels, run)
"""

import argparse
import glob
import json
import os
import sys
from datetime import datetime

FULL_PIPELINE_SECONDS_PER_DOCUMENT = 34.0

# Paired-statistics section (--baseline). The permutation seed is printed alongside every
# result so the sign-flip p-value is reproducible; 10,000 resamples because an exact
# enumeration is 2^n and impossible at the 300-1,400 query scale these runs are.
PERMUTATION_SEED = 20260831
PERMUTATION_RESAMPLES = 10_000
HOLM_ALPHA = 0.05
CHANGED_QUERY_WARNING_FRACTION = 0.10

# Mirrors BenchmarkQueryScenario.cs:42-43 -- the two fixed suffixes a run file's sidecar
# derivation strips before appending .meta.json.
SIMILAR_RUN_SUFFIX = ".similar"
CHUNKS_RUN_SUFFIX = ".chunks"


# ── Run-file discovery ─────────────────────────────────────────────────────────────────

def resolve_run_paths(run_args, qrels_path):
    """Expand each --run value into a sorted list of *.trec files if it names a directory,
    or keep it as-is if it names a file. Order: directory expansions are sorted so output is
    reproducible across a machine's directory-listing order; the top-level --run values keep
    the order given on the command line.

    A directory of run files routinely holds its own qrels file alongside them (this
    script's own verification directory does: qrels-small.trec sits next to
    baseline.chunks.trec) -- and qrels use the same *.trec extension while carrying a
    different column count (query_id, iteration, doc_id, relevance -- 4 fields, not the run
    format's 6). A naive glob would hand that file to ir_measures as a run and it would
    raise an unhandled ValueError unpacking the columns. The qrels path is therefore always
    excluded from run discovery, whether it was swept in from a directory or (a user error,
    but still handled) passed directly via --run."""
    qrels_abspath = os.path.abspath(qrels_path)
    paths = []
    excluded_qrels = False
    for value in run_args:
        if os.path.isdir(value):
            found = sorted(glob.glob(os.path.join(value, "*.trec")))
            if not found:
                sys.exit(f"--run {value}: directory contains no *.trec files")
            for path in found:
                if os.path.abspath(path) == qrels_abspath:
                    excluded_qrels = True
                    continue
                paths.append(path)
        elif os.path.isfile(value):
            if os.path.abspath(value) == qrels_abspath:
                excluded_qrels = True
                continue
            paths.append(value)
        else:
            sys.exit(f"--run {value}: not a file or directory")
    if excluded_qrels:
        print(f"[report] excluded {qrels_path} from run discovery (it is the --qrels file)")
    return paths


# ── Build identity ──────────────────────────────────────────────────────────────────────

def sidecar_path_for(run_path):
    """Derive a run file's build-identity sidecar path: strip the trailing .trec, then a
    trailing .similar or .chunks if present, then append .meta.json. So
    runs/reference.chunks.trec and runs/reference.similar.trec both resolve to
    runs/reference.meta.json. The two suffixes are fixed constants at
    BenchmarkQueryScenario.cs:42-43, so this rule is closed -- a run file ending in neither
    has no sidecar by construction."""
    base = run_path
    if base.endswith(".trec"):
        base = base[: -len(".trec")]
    for suffix in (SIMILAR_RUN_SUFFIX, CHUNKS_RUN_SUFFIX):
        if base.endswith(suffix):
            base = base[: -len(suffix)]
            break
    return base + ".meta.json"


def load_build_composite(run_path):
    """The run's build composite from its sidecar, or None if the sidecar does not exist --
    every run file predating this work, since BenchmarkQueryScenario only started writing
    sidecars in the change that introduced them. None means "unknown", not "no build": the
    caller must not treat it as agreeing with anything.

    A sidecar that exists but is not valid JSON (a truncated or corrupted write) is treated
    the same as a missing one, not raised: this is called after scoring has completed, so an
    uncaught JSONDecodeError here would discard the scoring work rather than just leave the
    build column unknown."""
    sidecar = sidecar_path_for(run_path)
    if not os.path.exists(sidecar):
        return None
    with open(sidecar, encoding="utf-8") as f:
        try:
            data = json.load(f)
        except json.JSONDecodeError:
            return None
    return data.get("composite")


def diversity_sidecar_for(run_path):
    """A .chunks.trec run's chunk-diversity sidecar path, or None for any other run --
    Task 2's benchmark-query only writes <label>.chunks.diversity.json beside a *.chunks.trec
    run, never beside a .similar.trec one. Derived from sidecar_path_for's own
    <label>.meta.json (same run-name-to-label stripping, single source of truth) by swapping
    its suffix, rather than re-deriving the label independently."""
    if not run_path.endswith(CHUNKS_RUN_SUFFIX + ".trec"):
        return None
    meta_path = sidecar_path_for(run_path)
    return meta_path[: -len(".meta.json")] + CHUNKS_RUN_SUFFIX + ".diversity.json"


def load_diversity_means(run_path):
    """(meanDistinctParentsAt10, meanDistinctParentsAt50) from the run's diversity sidecar, or
    None when the run is not a .chunks.trec run, the sidecar does not exist, is not valid
    JSON, or is missing either key -- treated the same as load_build_composite treats a
    missing/corrupt build sidecar: this runs after scoring, so a bad sidecar must not discard
    the scoring work, and must not raise either. A sidecar present but missing one of the two
    keys returns None rather than a partial (value, None) tuple: print_scores prints both
    lines together or neither, never one line with a formatted None in it."""
    sidecar = diversity_sidecar_for(run_path)
    if sidecar is None or not os.path.exists(sidecar):
        return None
    with open(sidecar, encoding="utf-8") as f:
        try:
            data = json.load(f)
        except json.JSONDecodeError:
            return None
    mean_at_10 = data.get("meanDistinctParentsAt10")
    mean_at_50 = data.get("meanDistinctParentsAt50")
    if mean_at_10 is None or mean_at_50 is None:
        return None
    return mean_at_10, mean_at_50


# ── Step 1: structural checks ──────────────────────────────────────────────────────────

def structural_check(path, qrels_query_ids=None):
    """Rows, distinct queries, non-zero scores, duplicate (query_id, doc_id) pairs, and (when
    qrels_query_ids is given) qrels query coverage -- parsed from the raw whitespace-delimited
    columns rather than through ir_measures.read_trec_run, which returns whatever list of
    ScoredDoc rows it read without flagging a doc id repeated under one query. A repeated doc
    id is exactly the malformation a TREC scorer either rejects (pytrec_eval raises) or
    silently collapses (a dict-keyed reader keeps only the last occurrence) -- either way, the
    run file does not mean what it looks like it means, and that has to be visible before its
    score is trusted. A query missing from the run entirely is the same kind of problem by a
    different mechanism: it contributes zero to every aggregate with no error from
    ir_measures, so coverage against the qrels file's own query set is checked here too.

    Standard TREC run columns: query_id, iter, doc_id, rank, score, tag. Malformed lines
    (fewer than 6 whitespace-separated fields) are counted and reported rather than raising,
    since a partially-malformed file is still worth reporting the rest of."""
    rows = 0
    malformed = 0
    query_ids = set()
    nonzero_scores = 0
    seen = {}  # (query_id, doc_id) -> count
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            fields = line.split()
            if len(fields) < 6:
                malformed += 1
                continue
            query_id, _iter, doc_id, _rank, score, _tag = fields[:6]
            rows += 1
            query_ids.add(query_id)
            key = (query_id, doc_id)
            seen[key] = seen.get(key, 0) + 1
            try:
                if float(score) != 0.0:
                    nonzero_scores += 1
            except ValueError:
                sys.exit(f"{path}: non-numeric score {score!r} on a row for query {query_id}")

    duplicates = {k: c for k, c in seen.items() if c > 1}
    duplicate_rows = sum(c - 1 for c in duplicates.values())

    qrels_total = None
    qrels_covered = None
    if qrels_query_ids is not None:
        qrels_total = len(qrels_query_ids)
        qrels_covered = len(query_ids & qrels_query_ids)

    return {
        "rows": rows,
        "malformed_lines": malformed,
        "distinct_queries": len(query_ids),
        "nonzero_scores": nonzero_scores,
        "duplicate_pairs": len(duplicates),
        "duplicate_rows": duplicate_rows,
        "duplicate_examples": sorted(duplicates)[:5],
        "qrels_total": qrels_total,
        "qrels_covered": qrels_covered,
    }


def print_structural_check(path, check):
    print(f"\n[structural] {path}")
    print(f"  rows                 {check['rows']:,}")
    if check["malformed_lines"]:
        print(f"  malformed lines      {check['malformed_lines']:,} (fewer than 6 fields; skipped)")
    print(f"  distinct queries     {check['distinct_queries']:,}")
    print(f"  non-zero scores      {check['nonzero_scores']:,} / {check['rows']:,}")
    if check["qrels_total"] is not None:
        print(f"  qrels queries        {check['qrels_total']:,}")
        print(f"  covered by this run  {check['qrels_covered']:,} / {check['qrels_total']:,}")
    if check["duplicate_pairs"]:
        print(
            f"  duplicate doc ids    {check['duplicate_pairs']:,} (query, doc) pair(s), "
            f"{check['duplicate_rows']:,} extra row(s) beyond the first -- "
            f"e.g. {check['duplicate_examples']}"
        )
    else:
        print("  duplicate doc ids    none")


def check_nugget_qrels_structure(nugget_qrels_path, nugget_qrels, qrels_path):
    """Refuse a --nugget-qrels file with no subtopic structure -- the same class of
    malformation section 1's structural_check exists to catch, just on the qrels side rather
    than the run side. read_trec_qrels exposes the iteration column
    (Qrel(query_id=..., doc_id=..., relevance=..., iteration='s1')); a genuine nugget file
    carries a distinct iteration value per subtopic, while a query-level qrels file (the kind
    --qrels expects) carries the same iteration value ("0") on every row. --qrels and
    --nugget-qrels are one path segment apart in a typical run directory (qrels.trec vs
    qrels.nugget.trec) and this script has no other way to tell them apart before alpha_nDCG
    silently degenerates to one subtopic per query and prints a plausible-looking wrong
    number."""
    iterations = {row.iteration for row in nugget_qrels}
    if len(iterations) <= 1:
        sys.exit(
            f"--nugget-qrels {nugget_qrels_path}: every row shares one iteration value "
            f"({sorted(iterations)!r}) -- this looks like a query-level qrels file, not a "
            f"nugget/subtopic file. alpha_nDCG needs more than one subtopic per query in the "
            f"iteration column. Check that --qrels ({qrels_path}) and --nugget-qrels were not "
            "swapped."
        )


def qrels_for(measure, qrels, nugget_qrels):
    """alpha_nDCG (pyndeval, added to `measures` only when --nugget-qrels is given) scores
    against the nugget/subtopic qrels; every other measure scores against the ordinary
    query-level qrels. Single source of truth for that routing, shared by score_run (which
    must materialise its run ONCE and group measures by this function's own return value --
    see score_run's docstring) and the per-query / paired-statistics sites, which each call
    read_trec_run afresh per invocation and so carry no consumed-generator risk."""
    return nugget_qrels if str(measure).startswith("alpha_nDCG") else qrels


# ── Step 2: scoring ─────────────────────────────────────────────────────────────────────

def score_run(qrels, run_path, measures, nugget_qrels=None):
    """Score one run against every measure in `measures`, against ONE materialised run.

    ir_measures.read_trec_run returns a generator; a second calc_aggregate over the same
    object scores an empty run as 0.0 with no error (the trap this module's --baseline
    section already documents for the baseline run, run_paired_statistics below). Routing
    "by measure" here without materialising first is exactly that defect, so the run is read
    into a list once and calc_aggregate is called once per qrels group (qrels_for) -- base
    measures against qrels, and alpha_nDCG@10, when present, against nugget_qrels -- with the
    two results dicts merged into the one print_scores reads."""
    import ir_measures

    run = list(ir_measures.read_trec_run(run_path))
    base_measures = [m for m in measures if qrels_for(m, qrels, nugget_qrels) is qrels]
    results = dict(ir_measures.calc_aggregate(base_measures, qrels, run)) if base_measures else {}
    nugget_measures = [m for m in measures if qrels_for(m, qrels, nugget_qrels) is not qrels]
    if nugget_measures:
        results.update(ir_measures.calc_aggregate(nugget_measures, nugget_qrels, run))
    return results


def print_scores(path, measures, results, composite=None, diversity=None):
    print(f"\n[scores] {path}")
    print(f"  {'build':10s} {composite if composite is not None else 'unknown'}")
    for measure in measures:
        print(f"  {str(measure):10s} {results[measure]:.4f}")
    if diversity is not None:
        mean_at_10, mean_at_50 = diversity
        print(f"  distinct parents @10  {mean_at_10:.2f}")
        print(f"  distinct parents @50  {mean_at_50:.2f}")


# ── Step 3: ingest stats sidecar ───────────────────────────────────────────────────────

def load_stats(path):
    if not path:
        return None
    if not os.path.exists(path):
        print(f"\n[stats] {path} not found -- skipping ingest-throughput report")
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def print_stats(path, stats):
    """Throughput (docs/hour, seconds/document, seconds/embed, the headline multiplier) is
    derived from elapsed_seconds -- the SUM of each invocation's own (finish - start),
    working time only. started_at/finished_at span first-start to last-finish instead, and
    after a --resume across a gap (a crash, or just stopping for the night) that span
    includes the idle time between invocations: document/embed counters stay correctly
    cumulative while the span balloons, silently inflating seconds/document if used for
    throughput. They are printed here purely as a labelled span, never used for arithmetic.

    Older sidecars (written before elapsed_seconds existed) fall back to the span for
    throughput, with a one-line notice that the figure may include idle time."""
    print(f"\n[stats] {path}")
    documents = stats.get("documents", 0)
    chunks = stats.get("chunks", 0)
    embed_calls = stats.get("embed_calls", 0)
    embeds_saved = stats.get("embeds_saved", 0)
    print(f"  documents            {documents:,}")
    print(f"  chunks               {chunks:,}")
    print(f"  embed calls          {embed_calls:,} ({embeds_saved:,} saved by the reuse gate)")

    started_at = stats.get("started_at")
    finished_at = stats.get("finished_at")
    if started_at and finished_at:
        print(f"  first-start -> last-finish span   {started_at} -> {finished_at}")

    elapsed_seconds = stats.get("elapsed_seconds")
    used_fallback = False
    if elapsed_seconds is None:
        used_fallback = True
        if not started_at or not finished_at:
            print(
                "  working time         unavailable (no elapsed_seconds, and "
                "started_at/finished_at missing from sidecar)"
            )
            return
        started = datetime.fromisoformat(started_at)
        finished = datetime.fromisoformat(finished_at)
        elapsed_seconds = (finished - started).total_seconds()
        print(
            "  [note] this sidecar predates elapsed_seconds -- falling back to the "
            "first-start -> last-finish span, which may include idle time across a --resume"
        )

    print(f"  working time          {elapsed_seconds:,.1f}s")

    if elapsed_seconds <= 0:
        print("  docs/hour            unavailable (non-positive working time)")
        return
    if documents == 0:
        print("  docs/hour            unavailable (zero documents)")
        return

    docs_per_hour = documents / (elapsed_seconds / 3600.0)
    seconds_per_document = elapsed_seconds / documents
    print(f"  docs/hour            {docs_per_hour:,.1f}")
    print(f"  seconds/document     {seconds_per_document:.3f}" + ("  (may include idle time)" if used_fallback else ""))
    if embed_calls > 0:
        print(f"  seconds/embed        {elapsed_seconds / embed_calls:.3f}")

    speedup = FULL_PIPELINE_SECONDS_PER_DOCUMENT / seconds_per_document
    print(
        f"\n  headline: {seconds_per_document:.3f}s/document measured vs "
        f"{FULL_PIPELINE_SECONDS_PER_DOCUMENT:.0f}s/document for the full gRPC/Kafka pipeline "
        f"-- {speedup:.1f}x"
    )


# ── Step 4: paired statistics against --baseline ───────────────────────────────────────

def holm_adjust(pvalues):
    """Holm-Bonferroni step-down correction. Sort ascending, adjust each by (family size -
    rank), then take the running maximum so the adjusted sequence stays non-decreasing --
    the standard Holm construction. Returns adjusted p-values in the SAME order as the input
    list (not sorted), each capped at 1.0. An empty input returns an empty list."""
    m = len(pvalues)
    if m == 0:
        return []
    order = sorted(range(m), key=lambda i: pvalues[i])
    adjusted = [None] * m
    running_max = 0.0
    for rank, idx in enumerate(order):  # rank 0 == smallest p-value
        candidate = (m - rank) * pvalues[idx]
        running_max = max(running_max, candidate)
        adjusted[idx] = min(running_max, 1.0)
    return adjusted


def per_query_values(qrels, run_path, measure, nugget_qrels=None):
    """{query_id: value} for one measure over one run, from ir_measures.iter_calc -- built as
    a measure OBJECT by the caller (see this module's CRITICAL docstring note), never via
    parse_measure. nugget_qrels, when given, is used instead of qrels for alpha_nDCG (see
    qrels_for) -- this function calls read_trec_run afresh each time, so there is no
    consumed-generator risk in routing per-call like this, unlike score_run."""
    import ir_measures

    run = ir_measures.read_trec_run(run_path)
    measure_qrels = qrels_for(measure, qrels, nugget_qrels)
    return {metric.query_id: metric.value for metric in ir_measures.iter_calc([measure], measure_qrels, run)}


def paired_comparison(baseline_values, run_path, qrels, measure, nugget_qrels=None):
    """Pair baseline_values (already computed once per measure, by the caller) against
    run_path's own per-query values for the same measure, over the intersection of their
    query id sets. Returns None when the intersection is empty -- nothing to pair -- rather
    than raising; the caller reports that explicitly instead of crashing the whole section
    over one degenerate run.

    Every quantity below is a comparison, and per this module's fail-fast-on-finiteness
    convention a comparison against NaN is simply False (Python's own float semantics): a
    zero-variance or single-query pairing yields NaN statistics rather than a crash, and
    "significant" downstream naturally evaluates to False for them instead of raising."""
    import numpy as np
    from scipy import stats as st

    run_values = per_query_values(qrels, run_path, measure, nugget_qrels)
    common_ids = sorted(set(baseline_values) & set(run_values))
    n = len(common_ids)
    if n == 0:
        return None

    baseline_arr = np.array([baseline_values[q] for q in common_ids])
    run_arr = np.array([run_values[q] for q in common_ids])
    diffs = run_arr - baseline_arr

    delta = float(np.mean(diffs))
    sd = float(np.std(diffs, ddof=1)) if n > 1 else float("nan")
    se = sd / np.sqrt(n) if n > 1 else float("nan")

    t_result = st.ttest_rel(run_arr, baseline_arr)
    t_stat = float(t_result.statistic)
    t_p = float(t_result.pvalue)

    # Sign-flip permutation test on the paired differences (permutation_type="samples" with a
    # single sample performs exactly this -- scipy's own "sign test" example). Seeded so the
    # printed p-value is reproducible; 10,000 resamples because an exact 2^n enumeration is
    # infeasible at this query-count scale.
    perm_result = st.permutation_test(
        (diffs,),
        lambda x, axis: np.mean(x, axis=axis),
        permutation_type="samples",
        vectorized=True,
        n_resamples=PERMUTATION_RESAMPLES,
        random_state=PERMUTATION_SEED,
        alternative="two-sided",
    )
    perm_p = float(perm_result.pvalue)

    df = n - 1
    t_crit = float(st.t.ppf(0.975, df)) if df > 0 else float("nan")
    half_width = t_crit * se
    ci = (delta - half_width, delta + half_width)

    # Guards only sd == 0.0 (falsy), which would otherwise raise ZeroDivisionError -- the
    # likeliest degenerate input, two identical runs. sd == NaN is truthy in Python (NaN is
    # truthy for every float value except 0.0), so that case takes the delta / sd branch, not
    # this guard; it lands on the same float("nan") result anyway, via IEEE 754 float division
    # propagating NaN rather than raising.
    d_z = delta / sd if sd else float("nan")

    z_975 = st.norm.ppf(0.975)
    z_80 = st.norm.ppf(0.80)
    mde = (z_975 + z_80) * sd / np.sqrt(n)

    changed = int(np.count_nonzero(diffs))

    return {
        "n": n,
        "baseline_ids": len(baseline_values),
        "run_ids": len(run_values),
        "delta": delta,
        "t_stat": t_stat,
        "t_p": t_p,
        "perm_p": perm_p,
        "ci": ci,
        "d_z": d_z,
        "mde": mde,
        "changed": changed,
    }


def print_compare_block(baseline_path, run_path, measure, comp, holm_p_adj, family_size,
                         baseline_composite, run_composite):
    print(
        f"\n[compare] {os.path.basename(run_path)}  vs  {os.path.basename(baseline_path)}"
        f"        ({measure})"
    )

    if baseline_composite is None or run_composite is None:
        print("  !! BUILD UNKNOWN: at least one of these runs has no build sidecar.")
        print("     Build agreement cannot be confirmed.")
    elif baseline_composite != run_composite:
        print("  !! BUILD MISMATCH: these runs came from different binaries.")
        print("     Any comparison between them is confounded.")

    if comp["n"] != comp["baseline_ids"] or comp["n"] != comp["run_ids"]:
        print(
            f"  query sets differ    baseline={comp['baseline_ids']:,}  "
            f"run={comp['run_ids']:,}  intersection={comp['n']:,}"
        )

    print(f"  delta            {comp['delta']:+.4f}")
    print(f"  paired t         t = {comp['t_stat']:.2f}   p = {comp['t_p']:.4f}")
    print(
        f"  permutation      p = {comp['perm_p']:.4f}   "
        f"({PERMUTATION_RESAMPLES:,} sign flips, seed {PERMUTATION_SEED})"
    )
    print(f"  95% CI           [{comp['ci'][0]:+.4f}, {comp['ci'][1]:+.4f}]")
    print(f"  Cohen's d_z      {comp['d_z']:.3f}")
    print(f"  MDE @ 80% power  {comp['mde']:.4f}")

    changed_fraction = comp["changed"] / comp["n"] if comp["n"] else float("nan")
    print(
        f"  queries changed  {comp['changed']:,} / {comp['n']:,}  "
        f"({changed_fraction * 100:.1f}%)"
    )

    significant = holm_p_adj < HOLM_ALPHA  # False for NaN, by Python's own float semantics
    verdict = "significant" if significant else "not significant"
    print(f"  Holm ({family_size} tests)   p_adj = {holm_p_adj:.4f}   {verdict}")

    if changed_fraction < CHANGED_QUERY_WARNING_FRACTION:
        print(
            f"  !! FEW QUERIES CHANGED: only {changed_fraction * 100:.1f}% of paired queries "
            "differ from baseline. The paired t-test's assumptions are likely violated --"
        )
        print("     read the permutation p above, not the paired t p.")


def run_paired_statistics(qrels, run_paths, baseline_path, measures, nugget_qrels=None):
    """The --baseline section. Excludes the baseline from the comparison set by absolute
    path -- resolve_run_paths itself stays untouched, since it also feeds the structural-check
    and scoring loops, which must still cover the baseline's own file. Without this exclusion
    here, the obvious `--baseline runs/reference.chunks.trec --run runs/` invocation compares
    the baseline against itself: t = nan, and the Holm family silently inflates by one,
    weakening every other comparison's corrected p-value.

    nugget_qrels, when given, lets alpha_nDCG@10 (present in `measures` only with
    --nugget-qrels) route to the nugget qrels via qrels_for instead of qrels -- see
    per_query_values and score_run's docstrings for the same routing rule."""
    import ir_measures

    baseline_abspath = os.path.abspath(baseline_path)
    compare_paths = []
    for path in run_paths:
        if os.path.abspath(path) == baseline_abspath:
            print(f"[baseline] excluded {path} (it is the --baseline file)")
            continue
        compare_paths.append(path)

    if not compare_paths:
        sys.exit("--baseline excludes every discovered run -- nothing left to compare")

    baseline_composite = load_build_composite(baseline_path)
    # read_trec_run returns a GENERATOR. Bound once and re-iterated per measure below, the first
    # measure consumed it and every later measure paired against an EMPTY baseline -- which
    # ir_measures scores as 0.0 per query without error, so R@50 and AP deltas came back as each
    # run's own aggregate (spec A34). Materialise it once; the compared runs are re-read per
    # measure by per_query_values and were never affected.
    baseline_run = list(ir_measures.read_trec_run(baseline_path))

    for measure in measures:
        baseline_values = {
            metric.query_id: metric.value
            for metric in ir_measures.iter_calc([measure], qrels_for(measure, qrels, nugget_qrels), baseline_run)
        }

        comparisons = [
            (run_path, paired_comparison(baseline_values, run_path, qrels, measure, nugget_qrels))
            for run_path in compare_paths
        ]

        # Holm corrects across the runs compared WITHIN this one measure, never pooled across
        # measures -- a sweep asks "which arms differ on nDCG@10", and pooling three measures
        # into one family would over-correct each of them. It corrects the permutation
        # p-values (the ones this section's own banner tells the reader to trust), not the
        # paired-t p-values.
        valid = [(path, comp) for path, comp in comparisons if comp is not None]
        holm_adjusted = holm_adjust([comp["perm_p"] for _, comp in valid])
        holm_by_path = dict(zip((path for path, _ in valid), holm_adjusted))
        family_size = len(valid)

        for run_path, comp in comparisons:
            if comp is None:
                print(
                    f"\n[compare] {os.path.basename(run_path)}  vs  "
                    f"{os.path.basename(baseline_path)}        ({measure})"
                )
                print("  !! NO OVERLAPPING QUERIES -- cannot compute paired statistics")
                continue
            run_composite = load_build_composite(run_path)
            print_compare_block(
                baseline_path, run_path, measure, comp, holm_by_path[run_path], family_size,
                baseline_composite, run_composite,
            )


# ── Step 4b: declared-pair statistics (--pair RUN=BASELINE) ────────────────────────────

POOL_MIN_REORDERED_FRACTION = 0.25   # spec §3.3: differs-from-control, per-query ranked doc-id sequence


def parse_pairs(values):
    """Each --pair value is RUN=BASELINE (split on the FIRST '='; paths may not contain '=').
    Both must exist and differ. Returns [(run_path, baseline_path)] in the order given, which
    is also the order the family is printed in."""
    pairs = []
    for value in values:
        if "=" not in value:
            sys.exit(f"--pair {value}: expected RUN=BASELINE")
        run_path, baseline_path = value.split("=", 1)
        for p in (run_path, baseline_path):
            if not os.path.isfile(p):
                sys.exit(f"--pair {value}: {p} is not a file")
        if os.path.abspath(run_path) == os.path.abspath(baseline_path):
            sys.exit(f"--pair {value}: a run cannot be its own baseline")
        pairs.append((run_path, baseline_path))
    return pairs


def ranked_doc_ids(run_path):
    """{query_id: [doc_id, ...]} in FILE order -- TrecRunWriter writes rank order positionally
    (spec A25), so file order is the ranked sequence. read_trec_run is a generator; consumed once here."""
    import ir_measures

    sequences = {}
    for row in ir_measures.read_trec_run(run_path):
        sequences.setdefault(row.query_id, []).append(row.doc_id)
    return sequences


def check_pool(run_path, baseline_path):
    """Spec §7.1.2, both halves, per pair. Reranking rescores the documents the control already
    selected, so (1) every query's doc-id SET must equal the control's -- if any differs the pool
    changed and the arm is invalid -- and (2) the ranked SEQUENCE must differ for at least
    POOL_MIN_REORDERED_FRACTION of the queries, or the reranker did not run (a silent fallback
    would otherwise score as a null result and read as 'reranking does not help'). Both fail
    loud via sys.exit; nothing downstream may run on an invalid arm. Prints one [pool] line."""
    run_seq = ranked_doc_ids(run_path)
    base_seq = ranked_doc_ids(baseline_path)
    common = sorted(set(run_seq) & set(base_seq))
    if not common:
        sys.exit(f"[pool] {os.path.basename(run_path)} vs {os.path.basename(baseline_path)}: "
                 "no overlapping queries")
    set_changed = [q for q in common if set(run_seq[q]) != set(base_seq[q])]
    reordered = [q for q in common if run_seq[q] != base_seq[q]]
    fraction = len(reordered) / len(common)
    print(
        f"[pool] {os.path.basename(run_path)}  vs  {os.path.basename(baseline_path)}: "
        f"{len(common):,} queries, set changed on {len(set_changed):,}, "
        f"sequence differs on {len(reordered):,} ({fraction * 100:.1f}%)"
    )
    if set_changed:
        sys.exit(
            f"[pool] ARM INVALID: pool changed -- the document set differs from "
            f"{os.path.basename(baseline_path)} on {len(set_changed)} of {len(common)} queries "
            f"(first: {set_changed[0]}). Reranking cannot change which documents are in the pool "
            "(spec §7.1.2); this arm was produced by a different pool and must not be scored."
        )
    if fraction < POOL_MIN_REORDERED_FRACTION:
        sys.exit(
            f"[pool] ARM INVALID: ranked sequence differs from {os.path.basename(baseline_path)} "
            f"on only {len(reordered)} of {len(common)} queries ({fraction * 100:.1f}%); at least "
            f"{POOL_MIN_REORDERED_FRACTION * 100:.0f}% is required (spec §3.3). The reranker "
            "most likely did not run."
        )


def run_pair_statistics(qrels, pairs, measures, nugget_qrels=None):
    """Like run_paired_statistics, but the family is EXACTLY the declared pairs -- each run
    against its own baseline -- and Holm runs once per measure over those pairs and nothing
    else (spec §7.2: A1-A0, A2-A0, A3-A0'; one construction at m = 3). Every pair passes
    check_pool before any statistic is computed. Baseline per-query values are materialised
    once per distinct baseline per measure (Task 1's fix applies here by construction).

    nugget_qrels, when given, lets alpha_nDCG@10 (present in `measures` only with
    --nugget-qrels) route to the nugget qrels via qrels_for instead of qrels -- the same
    routing rule run_paired_statistics, per_query_values and score_run already apply. Without
    it, this function's own baseline_values computation and its call into paired_comparison
    both pass the query-level qrels for every measure, including alpha_nDCG@10."""
    import ir_measures

    for run_path, baseline_path in pairs:
        check_pool(run_path, baseline_path)

    baseline_runs = {
        p: list(ir_measures.read_trec_run(p)) for p in {b for _, b in pairs}
    }
    composites = {p: load_build_composite(p) for pair in pairs for p in pair}

    for measure in measures:
        measure_qrels = qrels_for(measure, qrels, nugget_qrels)
        baseline_values = {
            p: {m.query_id: m.value for m in ir_measures.iter_calc([measure], measure_qrels, run)}
            for p, run in baseline_runs.items()
        }
        comparisons = [
            (run_path, baseline_path,
             paired_comparison(baseline_values[baseline_path], run_path, qrels, measure, nugget_qrels))
            for run_path, baseline_path in pairs
        ]
        valid = [(r, b, c) for r, b, c in comparisons if c is not None]
        holm_adjusted = holm_adjust([c["perm_p"] for _, _, c in valid])
        # Keyed by (run_path, baseline_path), not run_path alone: a run can appear in more than one
        # declared pair (same run, two different baselines), and a run-path-only key would collapse
        # those to one dict entry -- the earlier pair's block would then print the later pair's
        # adjusted p, silently (spec M2/R12).
        holm_by_pair = dict(zip(((r, b) for r, b, _ in valid), holm_adjusted))
        family_size = len(valid)

        for run_path, baseline_path, comp in comparisons:
            if comp is None:
                print(f"\n[compare] {os.path.basename(run_path)}  vs  "
                      f"{os.path.basename(baseline_path)}        ({measure})")
                print("  !! NO OVERLAPPING QUERIES -- cannot compute paired statistics")
                continue
            print_compare_block(
                baseline_path, run_path, measure, comp, holm_by_pair[(run_path, baseline_path)],
                family_size, composites[baseline_path], composites[run_path],
            )


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument(
        "--run", action="append", required=True,
        help="a TREC run file, or a directory of *.trec files; may be passed more than once",
    )
    ap.add_argument("--qrels", required=True, help="TREC qrels file")
    ap.add_argument(
        "--nugget-qrels", default=None,
        help="TREC qrels with the nugget/subtopic id in the iteration column; adds "
             "alpha_nDCG@10 (pyndeval) to every scored run",
    )
    ap.add_argument(
        "--stats-path", default=None,
        help="ingest.py's <key-map-path>.stats.json sidecar; omit to skip the throughput report",
    )
    ap.add_argument(
        "--baseline", default=None,
        help=(
            "a TREC run file to compare every other discovered run against (paired t-test, "
            "sign-flip permutation test, Holm-corrected across the runs within each measure); "
            "omit to skip the paired-statistics section. Excluded from the comparison set "
            "itself -- comparing it against itself is refused implicitly by omission, not by "
            "an error"
        ),
    )
    ap.add_argument(
        "--pair", action="append", default=None, metavar="RUN=BASELINE",
        help=(
            "a run and the baseline it is paired with; repeatable. The Holm family is exactly the "
            "pairs given, so arms with different controls (A3 vs A0' at a different lambda) can share "
            "one correction (spec 7.2). Each pair must pass the pool checks (identical per-query "
            "document sets; ranked order differs on >= 25%% of queries) or the report exits non-zero. "
            "Mutually exclusive with --baseline"
        ),
    )
    args = ap.parse_args()

    # Imported here rather than at module scope, matching freshstack_to_jsonl.py's pattern
    # for its own non-stdlib import: `--help` must work even when PYTHONPATH is not yet set
    # up, since printing the install instructions above is exactly what a user without
    # ir_measures installed needs from this script first.
    try:
        import ir_measures
        from ir_measures import AP, R, nDCG
    except ImportError as e:
        sys.exit(
            f"could not import ir_measures ({e}). It must be reached via PYTHONPATH, not "
            "site-packages -- see this script's module docstring for the install command."
        )

    measures = [nDCG @ 10, R @ 50, AP]

    if not os.path.exists(args.qrels):
        sys.exit(f"--qrels {args.qrels}: not found")
    if args.baseline and not os.path.exists(args.baseline):
        sys.exit(f"--baseline {args.baseline}: not found")
    if args.nugget_qrels and not os.path.exists(args.nugget_qrels):
        sys.exit(f"--nugget-qrels {args.nugget_qrels}: not found")

    if args.pair and args.baseline:
        sys.exit("--pair and --baseline are mutually exclusive: --pair declares the family explicitly")

    # Validated up front, before the (potentially long) scoring section below: a typo'd --pair path
    # then fails immediately rather than after a full re-score.
    pairs = parse_pairs(args.pair) if args.pair else None

    run_paths = resolve_run_paths(args.run, args.qrels)
    if not run_paths:
        sys.exit("no run files resolved from --run (after excluding --qrels)")

    # Read once, ahead of the structural-check loop rather than after it, so query coverage
    # against the qrels file can be part of the structural section itself -- the section
    # whose whole purpose is surfacing problems before any score is trusted.
    qrels = list(ir_measures.read_trec_qrels(args.qrels))
    qrels_query_ids = {row.query_id for row in qrels}

    # alpha_nDCG@10 (pyndeval) is added to `measures` only when --nugget-qrels is given, and
    # is scored against nugget_qrels instead of qrels -- see qrels_for and score_run's own
    # docstring for the single-materialisation rule this requires.
    nugget_qrels = None
    if args.nugget_qrels:
        from ir_measures import alpha_nDCG

        nugget_qrels = list(ir_measures.read_trec_qrels(args.nugget_qrels))
        check_nugget_qrels_structure(args.nugget_qrels, nugget_qrels, args.qrels)
        measures.append(alpha_nDCG @ 10)

    for path in run_paths:
        print_structural_check(path, structural_check(path, qrels_query_ids))

    for path in run_paths:
        results = score_run(qrels, path, measures, nugget_qrels)
        print_scores(path, measures, results, load_build_composite(path), load_diversity_means(path))

    stats = load_stats(args.stats_path)
    if stats is not None:
        print_stats(args.stats_path, stats)

    if args.baseline:
        run_paired_statistics(qrels, run_paths, args.baseline, measures, nugget_qrels)

    if pairs:
        run_pair_statistics(qrels, pairs, measures, nugget_qrels)


if __name__ == "__main__":
    main()

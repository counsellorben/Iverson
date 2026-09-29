#!/usr/bin/env python3
"""Reciprocal rank fusion of the MatchPattern leg with the SearchChunks baseline, plus the INERT and
degeneracy checks that decide which arms report.py may score (spec
docs/specs/2026-09-28-matchpattern-rrf-test-design.md, "Phase 3" and "Degeneracy guard").

Fully offline. Reads the baseline `<label>.chunks.trec` and its `<label>.meta.json` sidecar, and the
files pattern_leg.py wrote under OUT/pattern/ (calibration.tsv, scores-<t>.tsv, mp-<t>-<score>.trec,
build.json). Writes, per arm (t in p50/p75/p90, score in run_len/run_sum -- six arms):

    OUT/runs/fused-<t>-<score>.chunks.trec   the fused run
    OUT/runs/fused-<t>-<score>.meta.json     its build sidecar
    OUT/fused/fused-<t>-<score>.rrf.tsv      query_id, doc_id, fused_rank, rrf

and, across the arms, OUT/fused/inert.json and OUT/fused/guard.json. Finally it prints the
`--pair RUN=BASELINE` arguments for every non-INERT arm, ready for report.py.

Fusion. k = 60, fixed in advance and never tuned:

    rrf = 1/(60 + rank_baseline) + 1/(60 + rank_pattern)

The second term is omitted, not zeroed or floored, for a candidate with no pattern rank (no run
above theta). Only the baseline's 50 candidates are fused, so every fused run has the baseline's
document set -- report.check_pool requires exactly that, and it is asserted here before writing.

Why the written score is `51 - fused_rank` and not the RRF value: swapped ranks tie EXACTLY
(1/63 + 1/67 == 1/67 + 1/63), and ir_measures breaks score ties by doc id, not by file order (spec
verified assumption 18). The fused order breaks RRF ties by baseline rank, which is unique per
query, so the order is strict -- but only a strictly decreasing written score makes ir_measures
score that order rather than a doc-id order. The RRF values themselves go to the .rrf.tsv sidecar.
`51 - rank` is teacher_rerank.py's convention: ranks 1..50 map to 50..1, never 0.000000.

Build identity. The fused sidecar is the baseline sidecar with `composite` replaced by the
composite pattern_leg.py MEASURED (build.json), so report.py's build check compares measured values
rather than a copied constant. Every before/after check in build.json must agree with each other,
with build.json's baseline_composite and with the baseline sidecar, and the calibration pass plus
every theta pass must be present, or this script refuses to write anything. The sidecar is named by
report.py's own rule (report.sidecar_path_for) and read back through report.load_build_composite,
so a naming drift fails here rather than as a quiet "BUILD UNKNOWN" in the report.

INERT. report.check_pool is the authority: an arm whose ranked sequence differs from the baseline
on fewer than 25% of queries fails it. check_pool reports that by sys.exit, which would end this
run too, so it is called inside `except SystemExit` -- report.py is not modified. The document-set
half of check_pool is asserted by this script before check_pool runs, so a SystemExit from it can
only be the reorder-fraction half; the fraction is recomputed with report.ranked_doc_ids and
report.POOL_MIN_REORDERED_FRACTION, and if the two disagree about the arm this script aborts.

Degeneracy guard. Spearman rho (scipy.stats.spearmanr) over every (query, candidate) pair of the
baseline, between the arm's primary score and (a) the candidate's chunk count and (b) its
max-chunk similarity, both from calibration.tsv. A candidate with no run takes a floor strictly
below every matched score in the arm, min(matched) - 1 (spec: user decision, CDR round 1 §3.1).
|rho| >= 0.95 against either marks the arm DEGENERATE. A non-finite rho (a constant input) aborts:
per this directory's finiteness-first convention it must not read as "not degenerate".

Not stdlib-only: scipy (the guard) and ir_measures (report.check_pool) are reached through
PYTHONPATH, exactly as report.py's are:

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 \\
        Iverson.Server/Iverson.LoadTest/scripts/rrf_fuse.py \\
        --out /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-<date> \\
        --baseline /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-<date>/runs/<label>.chunks.trec
"""
import argparse
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import report  # noqa: E402  (sidecar naming, check_pool, ranked_doc_ids -- reused, never copied)

RRF_K = 60                      # spec Phase 3: fixed in advance, not tuned
WRITTEN_SCORE_BASE = 51         # written score = 51 - fused_rank (teacher_rerank.py's convention)
MAX_CANDIDATES = WRITTEN_SCORE_BASE - 1   # more than 50 would drive the written score to <= 0
DEGENERATE_ABS_RHO = 0.95       # spec "Degeneracy guard": |rho| >= 0.95 against either input
THETAS = ("p50", "p75", "p90")
SCORES = ("run_len", "run_sum")
CALIBRATION_PASS = "calibration"
RUN_SUFFIX = report.CHUNKS_RUN_SUFFIX + ".trec"


def arm_label(theta, score):
    return f"fused-{theta}-{score}"


# ── Readers ─────────────────────────────────────────────────────────────────────────────

def load_ranked_run(path):
    """{query_id: [doc_id, ...]} in file order, and the query order of the file. The rank column
    must equal the 1-based position within its query: file order IS the ranked order for
    report.check_pool (report.ranked_doc_ids reads positionally), so a file whose rank column
    disagrees means something upstream sorted, filtered or concatenated it. A doc id repeated
    within a query is refused for the same reason report.structural_check counts it."""
    runs = {}
    order = []
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            if not line.strip():
                continue
            fields = line.split()
            if len(fields) != 6:
                sys.exit(f"{path}:{lineno}: expected 6 whitespace-separated fields, got {len(fields)}: {line!r}")
            query_id, _iter, doc_id, rank, _score, _tag = fields
            docs = runs.get(query_id)
            if docs is None:
                docs = runs[query_id] = []
                order.append(query_id)
            if doc_id in docs:
                sys.exit(f"{path}:{lineno}: document {doc_id!r} appears twice in query {query_id!r}")
            if rank != str(len(docs) + 1):
                sys.exit(f"{path}:{lineno}: rank {rank} is not position {len(docs) + 1} in query {query_id!r}; "
                         "file order must be rank order")
            docs.append(doc_id)
    if not runs:
        sys.exit(f"{path}: no run rows")
    return runs, order


def read_tsv(path, required_columns):
    """Rows of a header-first, tab-separated file as dicts. Missing file or missing column exits."""
    if not os.path.exists(path):
        sys.exit(f"{path}: not found")
    with open(path, encoding="utf-8") as f:
        header = f.readline().rstrip("\n").split("\t")
        missing = [c for c in required_columns if c not in header]
        if missing:
            sys.exit(f"{path}: header {header} lacks column(s) {missing}")
        rows = []
        for lineno, line in enumerate(f, start=2):
            if not line.strip():
                continue
            values = line.rstrip("\n").split("\t")
            if len(values) != len(header):
                sys.exit(f"{path}:{lineno}: {len(values)} fields, header has {len(header)}")
            rows.append(dict(zip(header, values)))
    return rows


def parse_float(value, where):
    try:
        number = float(value)
    except ValueError:
        sys.exit(f"{where}: {value!r} is not a number")
    if not math.isfinite(number):
        sys.exit(f"{where}: {value!r} is not finite")
    return number


def load_calibration(path):
    """{(query_id, doc_id): (chunk_count, max_s)} from calibration.tsv. An empty `s` is refused:
    pattern_leg.py never writes one (a NULL similarity aborts it), so one here means a hand-edited
    or foreign file."""
    stats = {}
    for i, row in enumerate(read_tsv(path, ("query_id", "doc_id", "chunk_index", "s")), start=2):
        s = parse_float(row["s"], f"{path}:{i} s")
        key = (row["query_id"], row["doc_id"])
        count, best = stats.get(key, (0, -math.inf))
        stats[key] = (count + 1, max(best, s))
    return stats


def load_scores(path):
    """{(query_id, doc_id): {"run_len": float, "run_sum": float}} from scores-<t>.tsv -- each column
    is that arm's PRIMARY score for the candidate (see pattern_leg.write_scores)."""
    scores = {}
    for i, row in enumerate(read_tsv(path, ("query_id", "doc_id") + SCORES), start=2):
        key = (row["query_id"], row["doc_id"])
        if key in scores:
            sys.exit(f"{path}:{i}: ({key[0]}, {key[1]}) appears twice")
        scores[key] = {name: parse_float(row[name], f"{path}:{i} {name}") for name in SCORES}
    return scores


# ── Build identity ──────────────────────────────────────────────────────────────────────

def measured_composite(build, baseline_composite, required_passes):
    """The one composite pattern_leg.py measured, or exit. Every before and after in every check,
    and build.json's own baseline_composite, must equal the baseline sidecar's composite; and every
    pass in `required_passes` (calibration plus each theta fused here) must have been checked."""
    if not baseline_composite:
        sys.exit("the baseline sidecar has no composite; a fused run cannot be attributed to a build")
    if build.get("baseline_composite") != baseline_composite:
        sys.exit(f"build.json baseline_composite {build.get('baseline_composite')!r} != baseline sidecar "
                 f"composite {baseline_composite!r}")
    checks = build.get("checks") or []
    seen = {c.get("pass") for c in checks}
    missing = [p for p in required_passes if p not in seen]
    if missing:
        sys.exit(f"build.json has no /build check for pass(es) {missing}; refusing to fuse unverified output")
    for check in checks:
        for side in ("before", "after"):
            if check.get(side) != baseline_composite:
                sys.exit(f"build.json: pass {check.get('pass')!r} {side} composite {check.get(side)!r} != "
                         f"baseline {baseline_composite!r} -- the pattern leg ran on a different build")
    return baseline_composite


def write_sidecar(run_path, baseline_sidecar, composite, label):
    """The baseline sidecar's JSON with composite replaced by the MEASURED one and configLabel set
    to the fused label, at report.sidecar_path_for(run_path) -- then read back through
    report.load_build_composite, so a sidecar report.py cannot find fails here."""
    sidecar = dict(baseline_sidecar)
    sidecar["composite"] = composite
    sidecar["configLabel"] = label
    path = report.sidecar_path_for(run_path)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(sidecar, f, indent=2)
        f.write("\n")
    if report.load_build_composite(run_path) != composite:
        sys.exit(f"{path}: report.load_build_composite({run_path}) does not read back {composite!r}")
    return path


# ── Fusion ─────────────────────────────────────────────────────────────────────────────

def rrf_score(baseline_rank, pattern_rank):
    """1/(k + rank_b) + 1/(k + rank_p); the second term is absent when pattern_rank is None."""
    score = 1.0 / (RRF_K + baseline_rank)
    if pattern_rank is not None:
        score += 1.0 / (RRF_K + pattern_rank)
    return score


def fuse_query(baseline_docs, pattern_docs):
    """[(doc_id, rrf), ...] in fused order: rrf descending, ties by baseline rank ascending (unique
    per query, so the order is strict). `pattern_docs` is the pattern leg's ranked list for this
    query (matched candidates only); every one must be a baseline candidate."""
    baseline_rank = {doc: i for i, doc in enumerate(baseline_docs, start=1)}
    stray = [d for d in pattern_docs if d not in baseline_rank]
    if stray:
        sys.exit(f"pattern leg ranks {len(stray)} document(s) that are not baseline candidates, e.g. {stray[:3]}")
    pattern_rank = {doc: i for i, doc in enumerate(pattern_docs, start=1)}
    fused = [(doc, rrf_score(baseline_rank[doc], pattern_rank.get(doc))) for doc in baseline_docs]
    fused.sort(key=lambda pair: (-pair[1], baseline_rank[pair[0]]))
    if {d for d, _ in fused} != set(baseline_docs) or len(fused) != len(baseline_docs):
        sys.exit("fused document set differs from the baseline's")  # unreachable by construction; kept loud
    return fused


def fuse_run(baseline, baseline_order, pattern):
    """{query_id: [(doc_id, rrf), ...]} for every baseline query. A query the pattern leg has no row
    for fuses with every second term absent (no candidate had a run)."""
    stray = sorted(set(pattern) - set(baseline))
    if stray:
        sys.exit(f"pattern leg has {len(stray)} query id(s) absent from the baseline, e.g. {stray[:3]}")
    return {q: fuse_query(baseline[q], pattern.get(q, [])) for q in baseline_order}


def write_fused_run(path, fused, query_order, tag):
    """6-column TREC, written score 51 - fused_rank (strictly decreasing within a query)."""
    with open(path, "w", encoding="utf-8") as f:
        for query_id in query_order:
            for rank, (doc_id, _rrf) in enumerate(fused[query_id], start=1):
                f.write(f"{query_id} Q0 {doc_id} {rank} {WRITTEN_SCORE_BASE - rank:.6f} {tag}\n")


def write_rrf_tsv(path, fused, query_order):
    with open(path, "w", encoding="utf-8") as f:
        f.write("query_id\tdoc_id\tfused_rank\trrf\n")
        for query_id in query_order:
            for rank, (doc_id, rrf) in enumerate(fused[query_id], start=1):
                f.write(f"{query_id}\t{doc_id}\t{rank}\t{rrf!r}\n")


# ── INERT (report.check_pool, without its sys.exit ending this run) ─────────────────────

def classify_inert(run_path, baseline_path):
    """{"reordered_fraction", "inert"} for one fused arm, decided by report.check_pool itself.

    check_pool exits on either half of its rule. The document-set half is asserted here first, so a
    SystemExit afterwards can only be the reorder-fraction half, which is exactly INERT. The
    fraction is recomputed from report.ranked_doc_ids for the record, and the two verdicts must
    agree or the run aborts -- a disagreement means report.py's rule moved under this script."""
    run_seq = report.ranked_doc_ids(run_path)
    base_seq = report.ranked_doc_ids(baseline_path)
    common = sorted(set(run_seq) & set(base_seq))
    if not common:
        sys.exit(f"{run_path}: no query in common with {baseline_path}")
    set_changed = [q for q in common if set(run_seq[q]) != set(base_seq[q])]
    if set_changed:
        sys.exit(f"{run_path}: document set differs from the baseline on {len(set_changed)} queries "
                 f"(first: {set_changed[0]}) -- fusion must only reorder")
    fraction = sum(run_seq[q] != base_seq[q] for q in common) / len(common)
    try:
        report.check_pool(run_path, baseline_path)
        failed = False
    except SystemExit as exit_:
        print(f"  (check_pool exited: {exit_.code})")
        failed = True
    expected = fraction < report.POOL_MIN_REORDERED_FRACTION
    if failed != expected:
        sys.exit(f"{run_path}: report.check_pool {'failed' if failed else 'passed'} but the reorder fraction "
                 f"{fraction:.4f} says inert={expected}; report.py's pool rule has changed under this script")
    return {"reordered_fraction": fraction, "inert": failed}


# ── Degeneracy guard ────────────────────────────────────────────────────────────────────

def guard_vectors(baseline, baseline_order, arm_scores, calibration):
    """(primary, chunk_count, max_sim) as three parallel lists over every baseline (query, candidate)
    pair. `arm_scores` is {(query_id, doc_id): primary} for matched candidates only; the rest take
    the floor min(matched) - 1, strictly below every matched score. A candidate absent from
    calibration aborts: its chunk count and max similarity are the guard's inputs."""
    stray = [k for k in arm_scores if k[0] not in baseline or k[1] not in baseline[k[0]]]
    if stray:
        sys.exit(f"{len(stray)} scored (query, doc) pair(s) are not baseline candidates, e.g. {stray[:3]}")
    if not arm_scores:
        sys.exit("the arm has no matched candidate at all; the guard is undefined (every score is the floor)")
    floor = min(arm_scores.values()) - 1
    primary, counts, max_sims = [], [], []
    missing = []
    for query_id in baseline_order:
        for doc_id in baseline[query_id]:
            key = (query_id, doc_id)
            if key not in calibration:
                missing.append(key)
                continue
            count, best = calibration[key]
            primary.append(arm_scores.get(key, floor))
            counts.append(count)
            max_sims.append(best)
    if missing:
        sys.exit(f"{len(missing)} baseline candidate(s) have no calibration row, e.g. {missing[:3]}")
    return primary, counts, max_sims


def spearman(x, y, what):
    try:
        from scipy.stats import spearmanr
    except ImportError as e:
        sys.exit(f"could not import scipy ({e}); set PYTHONPATH to the corpora repo's python-libs "
                 "(see this script's docstring)")
    rho = float(spearmanr(x, y).statistic)
    if not math.isfinite(rho):
        sys.exit(f"guard: Spearman rho against {what} is {rho!r} (a constant input); a non-finite rho must "
                 "not read as 'not degenerate'")
    return rho


def degeneracy(primary, counts, max_sims):
    rho_count = spearman(primary, counts, "chunk count")
    rho_sim = spearman(primary, max_sims, "max-chunk similarity")
    return {
        "rho_chunk_count": rho_count,
        "rho_max_sim": rho_sim,
        "degenerate": abs(rho_count) >= DEGENERATE_ABS_RHO or abs(rho_sim) >= DEGENERATE_ABS_RHO,
    }


# ── CLI ─────────────────────────────────────────────────────────────────────────────────

def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True, help="the benchmark's OUT directory (holds runs/ and pattern/)")
    ap.add_argument("--baseline", required=True,
                    help="the re-recorded baseline <label>.chunks.trec (its <label>.meta.json must sit beside it)")
    return ap


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    out = args.out
    pattern_dir = os.path.join(out, "pattern")
    runs_dir = os.path.join(out, "runs")
    fused_dir = os.path.join(out, "fused")

    if not args.baseline.endswith(RUN_SUFFIX):
        sys.exit(f"--baseline {args.baseline}: expected a <label>{RUN_SUFFIX} run")
    baseline_sidecar_path = report.sidecar_path_for(args.baseline)
    if not os.path.exists(baseline_sidecar_path):
        sys.exit(f"{baseline_sidecar_path}: the baseline's sidecar is missing")
    with open(baseline_sidecar_path, encoding="utf-8") as f:
        baseline_sidecar = json.load(f)
    with open(os.path.join(pattern_dir, "build.json"), encoding="utf-8") as f:
        build = json.load(f)
    composite = measured_composite(build, report.load_build_composite(args.baseline),
                                   (CALIBRATION_PASS,) + THETAS)

    baseline, baseline_order = load_ranked_run(args.baseline)
    oversized = [q for q in baseline_order if len(baseline[q]) > MAX_CANDIDATES]
    if oversized:
        sys.exit(f"{len(oversized)} baseline queries hold more than {MAX_CANDIDATES} candidates, e.g. {oversized[:3]}")
    calibration = load_calibration(os.path.join(pattern_dir, "calibration.tsv"))

    os.makedirs(runs_dir, exist_ok=True)
    os.makedirs(fused_dir, exist_ok=True)
    inert, guard, pairs = {}, {}, []
    for theta in THETAS:
        scores = load_scores(os.path.join(pattern_dir, f"scores-{theta}.tsv"))
        for score in SCORES:
            label = arm_label(theta, score)
            mp_path = os.path.join(pattern_dir, f"mp-{theta}-{score}.trec")
            pattern, _ = load_ranked_run(mp_path) if os.path.getsize(mp_path) else ({}, [])
            mp_pairs = {(q, d) for q, docs in pattern.items() for d in docs}
            if mp_pairs != set(scores):
                sys.exit(f"{mp_path}: its (query, doc) pairs differ from scores-{theta}.tsv's "
                         f"({len(mp_pairs)} vs {len(scores)})")

            fused = fuse_run(baseline, baseline_order, pattern)
            run_path = os.path.join(runs_dir, label + RUN_SUFFIX)
            write_fused_run(run_path, fused, baseline_order, label)
            write_rrf_tsv(os.path.join(fused_dir, label + ".rrf.tsv"), fused, baseline_order)
            write_sidecar(run_path, baseline_sidecar, composite, label)

            print(f"\n[rrf_fuse] {label}")
            inert[label] = classify_inert(run_path, args.baseline)
            primary = {key: values[score] for key, values in scores.items()}
            guard[label] = degeneracy(*guard_vectors(baseline, baseline_order, primary, calibration))
            print(f"  reordered {inert[label]['reordered_fraction'] * 100:.1f}%  "
                  f"{'INERT' if inert[label]['inert'] else 'active'}   "
                  f"rho(count) {guard[label]['rho_chunk_count']:+.4f}  rho(max_sim) {guard[label]['rho_max_sim']:+.4f}  "
                  f"{'DEGENERATE' if guard[label]['degenerate'] else 'ok'}")
            if not inert[label]["inert"]:
                pairs.append(f"{run_path}={args.baseline}")

    for name, data in (("inert.json", inert), ("guard.json", guard)):
        with open(os.path.join(fused_dir, name), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
            f.write("\n")

    print(f"\n[rrf_fuse] composite {composite}; {len(pairs)} of {len(inert)} arms are not INERT")
    print("[rrf_fuse] report.py --pair arguments:")
    for pair in pairs:
        print(f"  --pair {pair}")


if __name__ == "__main__":
    main()

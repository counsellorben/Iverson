#!/usr/bin/env python3
"""Fetch Semantic Scholar citationCount (plus year and publicationDate) for every SciFact
corpus id, cached to disk. See docs/specs/2026-09-13-popularity-signal-measurement-design.md.

SciFact's BEIR `_id` IS the Semantic Scholar CorpusId (verified: id 4983 returns the exact
title corpus.jsonl records). Unauthenticated batch is aggressively rate-limited, so this is
resumable: every successful batch is merged into the cache file immediately, and a re-run
skips ids already present. Unresolved ids are recorded separately -- an id that resolves to
null is a real property of the data (the paper left the index), not a fetch failure, and the
two must not be conflated when reporting coverage.

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/fetch_citations.py \\
        --corpus <path/to/corpus.jsonl> \\
        --cache  <path/to/citations.json>

Writes, to --cache (atomically, resumable across runs):
    counts      -- {corpus id: citationCount} for every resolved id.
    years       -- {corpus id: year} for resolved ids that carry a year.
    dates       -- {corpus id: publicationDate} for resolved ids that carry a publicationDate
                    (month- or day-granular; strictly finer than year where present).
    unresolved  -- [corpus id, ...] for ids that returned no row (paper left the index).
"""
import argparse, json, os, sys, time, urllib.request, urllib.error

# `year` is fetched alongside the count because citation count is AGE-CONFOUNDED: older papers
# accrue citations regardless of merit. The shipped signal has no time decay (PopularityFor is a
# pure function of the count) and BenchmarkDocument has no date property, so WDecay is inert here
# -- the experiment therefore cannot control for age through any shipped mechanism, and must carry
# age as a measured covariate instead. Fetching it in the same pass avoids a second full crawl.
URL    = ('https://api.semanticscholar.org/graph/v1/paper/batch'
          '?fields=citationCount,year,publicationDate')

# An API key does NOT raise the ceiling -- S2 documents the introductory keyed limit as 1 RPS,
# while unauthenticated callers share a 1000 RPS global pool. The win is that the keyed 1 RPS is
# DEDICATED, so the 429 contention that dominates the unauthenticated path disappears. Hence the
# shorter inter-batch pace when a key is present: the constraint becomes our own rate, not others'.
API_KEY   = os.environ.get('S2_API_KEY', '').strip()
BATCH_GAP = 1.2 if API_KEY else 3.0

def load_cache(cache_path):
    if os.path.exists(cache_path):
        with open(cache_path) as f: return json.load(f)
    return {"counts": {}, "years": {}, "dates": {}, "unresolved": []}

def save(c, cache_path):
    tmp = cache_path + '.tmp'
    with open(tmp, 'w') as f: json.dump(c, f)
    os.replace(tmp, cache_path)   # atomic: a kill mid-write must not truncate the cache

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--corpus', required=True)
    parser.add_argument('--cache', required=True)
    args = parser.parse_args()

    ids = [json.loads(l)['_id'] for l in open(args.corpus)]
    cache = load_cache(args.cache)
    known = set(cache['counts']) | set(cache['unresolved'])
    todo = [i for i in ids if i not in known]
    print(f'corpus {len(ids)}  cached {len(known)}  to fetch {len(todo)}  '
          f'auth={"key" if API_KEY else "anonymous"}', flush=True)

    for start in range(0, len(todo), 100):
        batch = todo[start:start+100]
        ok = False
        for backoff in (0, 5, 15, 30, 60, 120, 240):
            if backoff: time.sleep(backoff)
            try:
                headers = {'Content-Type': 'application/json'}
                if API_KEY: headers['x-api-key'] = API_KEY
                req = urllib.request.Request(
                    URL, data=json.dumps({'ids': ['CorpusId:'+x for x in batch]}).encode(),
                    headers=headers)
                rows = json.load(urllib.request.urlopen(req, timeout=120))
                for k, row in zip(batch, rows):
                    if row and row.get('citationCount') is not None:
                        cache['counts'][k] = row['citationCount']
                        # year is separately nullable: a resolved paper with no year is still a
                        # usable count, so it must not be demoted to unresolved. The age covariate
                        # simply has a smaller n than the count, and that gap is reported.
                        if row.get('year') is not None:
                            cache['years'][k] = row['year']
                        # publicationDate is month-granular (occasionally day-granular) and is
                        # strictly finer than year; year stays as the fallback for rows lacking it.
                        if row.get('publicationDate'):
                            cache['dates'][k] = row['publicationDate']
                    else:
                        cache['unresolved'].append(k)
                ok = True
                break
            except urllib.error.HTTPError as e:
                if e.code != 429: raise
            except (urllib.error.URLError, TimeoutError):
                pass
        if not ok:
            save(cache, args.cache)
            sys.exit(f'batch at {start} exhausted all 7 backoff attempts; cache saved, re-run to resume')
        save(cache, args.cache)
        print(f'  {start+len(batch)}/{len(todo)}  resolved={len(cache["counts"])} '
              f'years={len(cache["years"])} dates={len(cache["dates"])} '
              f'unresolved={len(cache["unresolved"])}', flush=True)
        time.sleep(BATCH_GAP)

    # Completeness: every corpus id must land in exactly one of counts/unresolved. This is the
    # assertion that currently exists only as a manual plan step -- a coverage ratio alone
    # (len(counts)/len(ids)) cannot distinguish a complete fetch from a partial one, since ids that
    # are legitimately unresolved (the paper left the index) depress the ratio on a complete fetch
    # exactly as a stalled-out partial fetch would.
    accounted = set(cache['counts']) | set(cache['unresolved'])
    missing = [i for i in ids if i not in accounted]
    extra_unresolved = [i for i in cache['unresolved'] if i in cache['counts']]
    if missing or extra_unresolved:
        sys.exit(
            f'completeness check FAILED: {len(missing)} corpus id(s) in neither counts nor '
            f'unresolved, {len(extra_unresolved)} id(s) in both -- fetch is not complete')

    print(f'DONE resolved={len(cache["counts"])} unresolved={len(cache["unresolved"])} '
          f'coverage={len(cache["counts"])/len(ids):.4f}', flush=True)


if __name__ == "__main__":
    main()

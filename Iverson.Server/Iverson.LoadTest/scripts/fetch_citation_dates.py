#!/usr/bin/env python3
"""Fetch the publication date of every CITING paper, per SciFact corpus id, as a monthly
histogram. See docs/specs/2026-09-13-popularity-signal-measurement-design.md ("Why a decayed-
popularity arm is not in this experiment" -- this script is what removes that obstacle).

fetch_citations.py gets each paper's citation COUNT. The decayed arm needs each citation's
DATE, because the shipped signal's decay term is a half-life sum over a monthly bucket series:
PopularityFor computes `effective = count + RecencyBoost * D` where D comes from
DecayFieldResolver.ComputeRecencySum over "<relation>CountBuckets", and the consumer writes
that series as the LAST 60 monthly buckets of a DateHistogram (PopularitySignalConsumer.cs:123,
`buckets.TakeLast(60)`). So a decayed arm is a CONFIGURATION of the shipped feature, and
reproducing it offline requires the citing dates this script fetches.

Unauthenticated access to the citations endpoint is contention-bound, not rate-bound: 429s
arrive stochastically and on different ids between runs, so the backoff ladder -- not the pace
-- is what carries the fetch. This is resumable at PAGE granularity, not merely paper
granularity: a paper with 75,298 citations is 76 pages, and losing that progress to one
exhausted ladder would be expensive. Every page is merged and saved immediately.

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/fetch_citation_dates.py \\
        --run    <path/to/run.trec> \\
        --counts <path/to/citations.json> \\
        --cache  <path/to/citation-dates.json>

--run restricts the fetch to documents that actually appear in a retrieved pool; every other
corpus document is irrelevant to the measurement and would be paid for at the same price.

Writes, to --cache (atomically, resumable across runs):
    months      -- {corpus id: {"YYYY-MM": n_citations_that_month}} for each fetched paper.
    next        -- {corpus id: next page offset} while a paper is INCOMPLETE; the id is absent
                    from this map once its pagination has run out. Presence here is what makes
                    resume page-granular.
    seen        -- {corpus id: n_citation_rows_ingested} -- reconciles against the known count.
    truncated   -- [corpus id, ...] the API refused to page further (offset ceiling). Their D is
                    computed from a PARTIAL, arbitrarily-ordered subset and must be reported, not
                    silently mixed in: the endpoint does not order citations by date.
    failed      -- [corpus id, ...] that exhausted the backoff ladder on their first page.
"""
import argparse, json, os, sys, time, urllib.request, urllib.error

URL = ('https://api.semanticscholar.org/graph/v1/paper/CorpusId:{cid}/citations'
       '?fields=publicationDate,year&limit={limit}&offset={offset}')

PAGE = 1000          # the endpoint's maximum; verified to return up to 1000 rows per call.
API_KEY = os.environ.get('S2_API_KEY', '').strip()
GAP = 1.2 if API_KEY else 3.0
LADDER = (0, 5, 15, 30, 60, 120, 240)


def load_cache(path):
    if os.path.exists(path):
        with open(path) as f:
            return json.load(f)
    return {"months": {}, "next": {}, "seen": {}, "truncated": [], "failed": []}


def save(c, path):
    tmp = path + '.tmp'
    with open(tmp, 'w') as f:
        json.dump(c, f)
    os.replace(tmp, path)     # atomic: a kill mid-write must not truncate the cache


def month_of(row):
    """A citing paper contributes to the bucket of its publication date.

    publicationDate is month- or day-granular and is preferred. `year` is the fallback, placed at
    the year's midpoint (July) -- the same convention measurement 4a uses for year-only documents,
    so the two analyses agree on how a year-only record is dated. A citing paper with neither date
    cannot be bucketed at all and is counted in `seen` but contributes to no month; dropping it
    silently would inflate the apparent recency of papers whose citations are poorly dated.
    """
    cp = row.get('citingPaper') or {}
    d = cp.get('publicationDate')
    if d and len(d) >= 7:
        return d[:7]
    y = cp.get('year')
    return f'{y:04d}-07' if isinstance(y, int) else None


def fetch_page(cid, offset):
    """Returns (rows, next_offset, status). status is 'ok', 'truncated', or 'exhausted'."""
    headers = {'User-Agent': 'iverson-benchmark/1.0'}
    if API_KEY:
        headers['x-api-key'] = API_KEY
    for backoff in LADDER:
        if backoff:
            time.sleep(backoff)
        try:
            req = urllib.request.Request(
                URL.format(cid=cid, limit=PAGE, offset=offset), headers=headers)
            body = json.load(urllib.request.urlopen(req, timeout=120))
            return body.get('data') or [], body.get('next'), 'ok'
        except urllib.error.HTTPError as e:
            if e.code == 429:
                continue
            # The endpoint refuses offsets past its ceiling with a 400. That is a hard stop for
            # this paper, not a transient failure -- retrying the ladder against it would burn
            # ~7 minutes to arrive at the same refusal.
            if e.code == 400 and offset > 0:
                return [], None, 'truncated'
            raise
        except (urllib.error.URLError, TimeoutError):
            continue
    return [], None, 'exhausted'


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--run', required=True)
    p.add_argument('--counts', required=True)
    p.add_argument('--cache', required=True)
    args = p.parse_args()

    with open(args.counts) as f:
        counts = json.load(f)['counts']
    in_pool = set()
    for line in open(args.run):
        f6 = line.split()
        if len(f6) >= 6:
            in_pool.add(f6[2])
    # Only in-pool documents that have a count can enter the measurement at all: the AUC and the
    # re-rank both key on the count, so a document without one is already excluded upstream.
    targets = sorted(d for d in in_pool if d in counts)

    cache = load_cache(args.cache)
    done = {d for d in cache['months'] if d not in cache['next']}
    todo = [d for d in targets if d not in done and d not in cache['failed']]
    total_pages = sum(max(1, -(-min(counts[d], 10000) // PAGE)) for d in todo)
    print(f'in-pool targets {len(targets)}  done {len(done)}  to fetch {len(todo)}  '
          f'~{total_pages} pages  auth={"key" if API_KEY else "anonymous"}', flush=True)

    t0 = time.time()
    pages = 0
    for i, cid in enumerate(todo):
        months = cache['months'].setdefault(cid, {})
        offset = cache['next'].get(cid, 0)
        while True:
            rows, nxt, status = fetch_page(cid, offset)
            pages += 1
            if status == 'exhausted':
                if offset == 0:
                    cache['failed'].append(cid)
                else:
                    cache['next'][cid] = offset      # keep the partial; resume here next run
                save(cache, args.cache)
                break
            for r in rows:
                m = month_of(r)
                if m:
                    months[m] = months.get(m, 0) + 1
            cache['seen'][cid] = cache['seen'].get(cid, 0) + len(rows)
            if status == 'truncated':
                if cid not in cache['truncated']:
                    cache['truncated'].append(cid)
                cache['next'].pop(cid, None)
                save(cache, args.cache)
                break
            if nxt is None:
                cache['next'].pop(cid, None)
                save(cache, args.cache)
                break
            offset = nxt
            cache['next'][cid] = offset
            save(cache, args.cache)
            time.sleep(GAP)
        if (i + 1) % 25 == 0 or i + 1 == len(todo):
            el = time.time() - t0
            rate = pages / el if el else 0
            left = max(0, total_pages - pages)
            print(f'  {i+1}/{len(todo)} papers  {pages} pages  {rate:.3f} pages/s  '
                  f'eta {left/rate/3600:.1f}h  truncated={len(cache["truncated"])} '
                  f'failed={len(cache["failed"])}', flush=True)
        time.sleep(GAP)

    still = [d for d in targets if d not in cache['months'] and d not in cache['failed']]
    incomplete = [d for d in targets if d in cache['next']]
    if still or incomplete:
        sys.exit(f'INCOMPLETE: {len(still)} target(s) never started, {len(incomplete)} '
                 f'mid-pagination; cache saved, re-run to resume')

    print(f'DONE papers={len(cache["months"])} truncated={len(cache["truncated"])} '
          f'failed={len(cache["failed"])}', flush=True)


if __name__ == "__main__":
    main()

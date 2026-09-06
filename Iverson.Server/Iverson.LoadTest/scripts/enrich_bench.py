#!/usr/bin/env python3
"""Measure enrichment backends on a fixed prompt set (spec 2026-09-05-embedding-migration-phase2 §6.2).

Builds 75 prompts from the first SciFact abstracts (read-only): 50 ChunkContext, 10 Summary,
10 Keywords, 5 Extraction. Runs them sequentially, one backend at a time, through the
OpenAI-compatible /v1/chat/completions route the ported EnrichmentService uses (temperature 0,
max_tokens 256), recording per prompt: wall seconds, completion tokens, tokens/s, empty output,
and for Extraction whether the reply parses after the same extraction rule as the service; per
backend: peak container RSS (docker stats, sampled every 5 s) and the box's minimum MemAvailable.

    python3 enrich_bench.py --corpus .../scifact-run-2026-08-26/beir/corpus.jsonl \\
        --out .../enrichment-bench-2026-09-05 \\
        --backend ollama=http://localhost:11434=qwen2.5:3b=iverson-ollama \\
        --backend tgi=http://localhost:8092=Qwen/Qwen2.5-1.5B-Instruct=iverson-tgi

Each --backend is name=base_url=model=container. Outputs results.json and side-by-side.md.
"""
import argparse, json, os, re, statistics, subprocess, threading, time, urllib.request

# EnrichmentPrompts.cs, verbatim.
SUMMARY = "Summarize the following text in 2-3 concise sentences:\n\n{0}"
KEYWORDS = ("Extract the 5-10 most important keywords or key phrases from the following text. "
            "Return them as a comma-separated list:\n\n{0}")
EXTRACTION = "Extract structured information from the following text and return it as JSON:\n\n{0}"
CHUNK_CONTEXT = ("Here is the context of a document:\n\n{0}\n\n"
                 "Here is an excerpt from that same document:\n\n{1}\n\n"
                 "Write a brief 1-2 sentence description of what this excerpt is about and how it "
                 "relates to the surrounding document. Respond with the description only.")
EXTRACT_HINT = "the main finding and the population studied"
MAX_TOKENS = 256
FENCE = re.compile(r"```(?:json)?\s*\n(.*?)\n\s*```", re.S)


def build_prompts(corpus_path):
    docs = []
    with open(corpus_path, encoding="utf-8") as f:
        for line in f:
            d = json.loads(line)
            if d.get("text", "").strip():
                docs.append(d["text"])
            if len(docs) >= 75:
                break
    prompts = []
    for i, text in enumerate(docs[:50]):
        prompts.append(("ChunkContext", i, CHUNK_CONTEXT.format(text[:1500], text[:512])))
    for i, text in enumerate(docs[50:60]):
        prompts.append(("Summary", i, SUMMARY.format(text[:8000])))
    for i, text in enumerate(docs[60:70]):
        prompts.append(("Keywords", i, KEYWORDS.format(text[:8000])))
    for i, text in enumerate(docs[70:75]):
        prompts.append(("Extraction", i, EXTRACTION.format(text[:8000]) + f"\n\nExtract specifically: {EXTRACT_HINT}"))
    assert len(prompts) == 75, len(prompts)
    return prompts


def chat(base_url, model, prompt):
    body = json.dumps({"model": model, "messages": [{"role": "user", "content": prompt}],
                       "max_tokens": MAX_TOKENS, "temperature": 0, "stream": False}).encode()
    req = urllib.request.Request(f"{base_url}/v1/chat/completions", data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    t0 = time.monotonic()
    with urllib.request.urlopen(req, timeout=600) as resp:
        parsed = json.loads(resp.read())
    wall = time.monotonic() - t0
    content = parsed["choices"][0]["message"]["content"]
    tokens = parsed.get("usage", {}).get("completion_tokens")
    return content, wall, tokens


def extract_json(text):
    """The service's rule: first fenced block, else first balanced {...} span (string-aware)."""
    m = FENCE.search(text)
    cand = m.group(1).strip() if m else None
    if cand is None:
        start = text.find("{")
        if start >= 0:
            depth, in_str, esc = 0, False, False
            for i in range(start, len(text)):
                c = text[i]
                if in_str:
                    if esc: esc = False
                    elif c == "\\": esc = True
                    elif c == '"': in_str = False
                    continue
                if c == '"': in_str = True
                elif c == "{": depth += 1
                elif c == "}":
                    depth -= 1
                    if depth == 0:
                        cand = text[start:i + 1]
                        break
    if cand is None:
        return False
    try:
        json.loads(cand)
        return True
    except json.JSONDecodeError:
        return False


class MemorySampler(threading.Thread):
    def __init__(self, container):
        super().__init__(daemon=True)
        self.container, self.peak_rss, self.min_avail, self.stop = container, 0, None, threading.Event()

    def run(self):
        while not self.stop.is_set():
            try:
                out = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", self.container],
                                     capture_output=True, text=True, timeout=20).stdout
                m = re.search(r"([0-9.]+)([KMG]i?B)", out)
                if m:
                    unit = {"KB": 1e3, "MB": 1e6, "GB": 1e9, "KiB": 2**10, "MiB": 2**20, "GiB": 2**30}[m.group(2)]
                    self.peak_rss = max(self.peak_rss, float(m.group(1)) * unit)
                with open("/proc/meminfo") as f:
                    avail = int([l for l in f if l.startswith("MemAvailable")][0].split()[1]) * 1024
                self.min_avail = avail if self.min_avail is None else min(self.min_avail, avail)
            except Exception:
                pass
            self.stop.wait(5)


def run_backend(name, base_url, model, container, prompts):
    sampler = MemorySampler(container)
    sampler.start()
    rows = []
    for kind, idx, prompt in prompts:
        try:
            content, wall, tokens = chat(base_url, model, prompt)
            rows.append({"kind": kind, "index": idx, "wall_s": round(wall, 3), "completion_tokens": tokens,
                         "tokens_per_s": round(tokens / wall, 3) if tokens and wall else None,
                         "empty": not content.strip(),
                         "json_ok": extract_json(content) if kind == "Extraction" else None,
                         "failed": False, "output": content})
        except Exception as e:
            rows.append({"kind": kind, "index": idx, "wall_s": None, "completion_tokens": None, "tokens_per_s": None,
                         "empty": True, "json_ok": False if kind == "Extraction" else None, "failed": True, "output": f"ERROR: {e}"})
        print(f"[{name}] {kind} {idx}: {rows[-1]['wall_s']}s {rows[-1]['completion_tokens']} tok", flush=True)
    sampler.stop.set()
    sampler.join()
    walls = sorted(r["wall_s"] for r in rows if r["wall_s"] is not None)
    by_kind = {}
    for k in ("ChunkContext", "Summary", "Keywords", "Extraction"):
        ws = [r["wall_s"] for r in rows if r["kind"] == k and r["wall_s"] is not None]
        by_kind[k] = {"mean_wall_s": round(statistics.mean(ws), 3) if ws else None, "n": len(ws)}
    return {"name": name, "base_url": base_url, "model": model, "container": container, "rows": rows,
            "summary": {"by_kind": by_kind,
                        "p95_wall_s": walls[max(0, int(round(0.95 * len(walls))) - 1)] if walls else None,
                        "failed": sum(r["failed"] for r in rows), "empty": sum(r["empty"] for r in rows),
                        "extraction_parse_ok": sum(1 for r in rows if r["kind"] == "Extraction" and r["json_ok"]),
                        "peak_rss_bytes": sampler.peak_rss, "min_mem_available_bytes": sampler.min_avail}}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corpus", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--backend", action="append", required=True, metavar="NAME=BASE_URL=MODEL=CONTAINER")
    args = ap.parse_args()
    prompts = build_prompts(args.corpus)
    os.makedirs(args.out, exist_ok=True)
    results = []
    for spec in args.backend:
        name, base_url, model, container = spec.split("=", 3)
        results.append(run_backend(name, base_url, model, container, prompts))
        with open(os.path.join(args.out, "results.json"), "w", encoding="utf-8") as f:
            json.dump({"prompts": len(prompts), "max_tokens": MAX_TOKENS, "backends": results}, f, indent=2)
    with open(os.path.join(args.out, "side-by-side.md"), "w", encoding="utf-8") as f:
        f.write("# Enrichment side-by-side\n\n")
        for kind, idx, prompt in prompts:
            f.write(f"## {kind} {idx}\n\n<details><summary>prompt</summary>\n\n```\n{prompt}\n```\n\n</details>\n\n")
            for r in results:
                row = next(x for x in r["rows"] if x["kind"] == kind and x["index"] == idx)
                f.write(f"**{r['name']}** ({row['wall_s']} s, {row['completion_tokens']} tokens):\n\n```\n{row['output']}\n```\n\n")
    for r in results:
        print(json.dumps({r["name"]: r["summary"]}, indent=2))


if __name__ == "__main__":
    main()

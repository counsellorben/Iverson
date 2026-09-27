#!/usr/bin/env bash
# Runs on the POD only. Implements runbook §11 (docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md)
# verbatim: 8 new queries (seeds 101-108), 2 sequential invocations each of the UNMODIFIED
# teacher_rerank.py (4 attempts total per query), then the summarizer -- see
# docs/specs/2026-09-21-teacher-ceiling-attempt-measurement-design.md for why 2 invocations,
# not a new --retry-budget flag, and why each query gets its own --responses/--out pair.
#
# Usage (after the runbook's steps 2-4: pod provisioned, vLLM serving, structured-output param
# and seed support confirmed) -- run the WHOLE script detached, not just each invocation inside
# it, so an SSH disconnect can't kill the loop between seeds:
#   mkdir -p /workspace/artifacts-v2 &&
#   setsid nohup bash -c 'MODEL_ID=<model-id> VLLM_VERSION=<ver> INSTANCE_SKU=<sku> \
#     bash run_measurement_batch.sh' > /workspace/artifacts-v2/batch.log 2>&1 < /dev/null &
#   disown
#
# Required env vars (no defaults on purpose -- a placeholder value would silently mislabel the
# sidecar of every one of the 16 invocations, which report.py can't catch after the fact):
#   MODEL_ID       -- the --model value vLLM is actually serving
#   VLLM_VERSION   -- the installed vLLM version (runbook step 4)
#   INSTANCE_SKU   -- e.g. "H100-80GB" (goes in the sidecar for the record, not sent to vLLM)
#
# Optional:
#   S              -- scripts dir (default /workspace, matching the runbook's pod block)
#   B              -- inputs dir  (default /workspace/inputs)
#   A              -- artifacts dir (default /workspace/artifacts-v2; created if missing) -- a
#                     NEW default, not the original measurement's /workspace/artifacts, so a
#                     re-run against the same seeds does not resume into pre-fix ledger entries
#   CONCURRENCY    -- max concurrent teacher_rerank.py invocations per pass (default 6)
#   BASE_URL       -- vLLM base URL (default http://127.0.0.1:8000)
#   VLLM_API_KEY   -- if set, forwarded as --api-key (RunPod's vLLM template enforces this even
#                     when --api-key never shows in `ps aux` -- confirmed live 2026-09-21)
#   SEEDS          -- default "101 102 103 104 105 106 107 108"
#   RUN_SUMMARIZER -- "0" to skip the summarizer step (default: run it)

set -euo pipefail

: "${MODEL_ID:?set MODEL_ID to the model vLLM is serving (see runbook step 3)}"
: "${VLLM_VERSION:?set VLLM_VERSION to the installed vLLM version (see runbook step 4)}"
: "${INSTANCE_SKU:?set INSTANCE_SKU, e.g. H100-80GB}"

S="${S:-/workspace}"
B="${B:-/workspace/inputs}"
A="${A:-/workspace/artifacts-v2}"
CONCURRENCY="${CONCURRENCY:-6}"
[[ "$CONCURRENCY" =~ ^[1-9][0-9]*$ ]] || { echo "CONCURRENCY must be a positive integer, got: $CONCURRENCY" >&2; exit 1; }
BASE_URL="${BASE_URL:-http://127.0.0.1:8000}"
SEEDS="${SEEDS:-101 102 103 104 105 106 107 108}"
RUN_SUMMARIZER="${RUN_SUMMARIZER:-1}"

mkdir -p "$A"

for f in "$S/teacher_rerank.py" "$B/runs/rerank-a0prime.chunks.trec" "$B/beir/corpus.jsonl" "$B/beir/queries.jsonl"; do
  if [ ! -f "$f" ]; then
    echo "missing required input: $f (did runbook step 3's upload complete?)" >&2
    exit 1
  fi
done

API_KEY_ARGS=()
if [ -n "${VLLM_API_KEY:-}" ]; then
  API_KEY_ARGS=(--api-key "$VLLM_API_KEY")
fi

echo "=== measurement batch: seeds [$SEEDS], model=$MODEL_ID vllm=$VLLM_VERSION sku=$INSTANCE_SKU ==="

read -ra SEED_ARR <<< "$SEEDS"

for PASS in 1 2; do
  i=0
  while [ "$i" -lt "${#SEED_ARR[@]}" ]; do
    BATCH=("${SEED_ARR[@]:$i:$CONCURRENCY}")
    echo "--- pass $PASS batch [${BATCH[*]}]: launching ---"
    PIDS=()
    for SEED in "${BATCH[@]}"; do
      python3 "$S/teacher_rerank.py" \
        --run "$B/runs/rerank-a0prime.chunks.trec" \
        --corpus "$B/beir/corpus.jsonl" --queries "$B/beir/queries.jsonl" \
        --base-url "$BASE_URL" --model "$MODEL_ID" \
        --seed "$SEED" --shuffle-seed "$SEED" --subsample 1 --subsample-seed "$SEED" \
        --vllm-version "$VLLM_VERSION" --quantisation mxfp4 --instance-type "$INSTANCE_SKU" \
        "${API_KEY_ARGS[@]}" \
        --responses "$A/q$SEED.responses.jsonl" --out "$A/q$SEED.chunks.trec" \
        > "$A/q$SEED.pass$PASS.log" 2>&1 < /dev/null &
      PIDS+=("$!")
    done
    wait "${PIDS[@]}" || true
    for SEED in "${BATCH[@]}"; do
      echo "seed $SEED pass $PASS done -- $(tail -1 "$A/q$SEED.pass$PASS.log")"
    done
    i=$((i + CONCURRENCY))
  done
done

echo "=== all 16 invocations complete ==="

if [ "$RUN_SUMMARIZER" != "0" ]; then
  if [ -f "$S/summarize_teacher_attempts.py" ]; then
    echo "=== summarizer ==="
    LEDGERS=()
    for SEED in $SEEDS; do
      LEDGERS+=("$A/q$SEED.responses.jsonl")
    done
    python3 "$S/summarize_teacher_attempts.py" "${LEDGERS[@]}" | tee "$A/measurement-summary.txt"
  else
    echo "summarize_teacher_attempts.py not found at $S -- copy it up (runpodctl send/receive)" \
         "and run manually: python3 $S/summarize_teacher_attempts.py $A/q101.responses.jsonl ... $A/q108.responses.jsonl" >&2
  fi
fi

echo "=== done. Pull \$A down with runpodctl before terminating the pod (runbook step 9 / §11's own note). ==="

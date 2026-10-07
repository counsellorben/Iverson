# TEI with one embedding model's weights baked in, so the serving pod needs no network (CSR
# round-10 #17). The chart derives this image's tag from the model slug, the pinned revision and
# a hash of this file; deploy/kind/build-and-load-image.sh --model-images builds it.
# Base pinned by multi-arch index digest (resolved 2026-10-06).
FROM ghcr.io/huggingface/text-embeddings-inference:cpu-1.9.4@sha256:2538ea1c9640d3763b15af668039d24172d063b42337b0c27796fc2be180c78d AS fetch
ARG MODEL_ID
ARG REVISION
ENV HUGGINGFACE_HUB_CACHE=/models
# Run TEI once against the pinned revision to fill its hub cache (the same download path it uses
# at runtime), and fail the build if it never reports healthy.
RUN set -e; text-embeddings-router --model-id "$MODEL_ID" --revision "$REVISION" --port 8080 >/tmp/log 2>&1 & pid=$!; \
    for i in $(seq 1 150); do if curl -sf localhost:8080/health >/dev/null; then ok=1; break; fi; sleep 2; done; \
    kill $pid; wait $pid || true; [ "${ok:-}" = 1 ] || { tail -20 /tmp/log; exit 1; }; \
    find /models -name '*.lock' -delete

FROM ghcr.io/huggingface/text-embeddings-inference:cpu-1.9.4@sha256:2538ea1c9640d3763b15af668039d24172d063b42337b0c27796fc2be180c78d
COPY --from=fetch /models /models
ENV HUGGINGFACE_HUB_CACHE=/models HF_HUB_OFFLINE=1

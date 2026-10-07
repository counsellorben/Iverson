# Ollama with the generative model baked in, so the serving pod needs no network (CSR round-10
# #17). The build fails unless the pulled manifest's full SHA-256 equals DIGEST (CSR round-10
# #19: the previous check compared a 12-character prefix). The chart derives this image's tag
# from the model, the digest and a hash of this file.
# Base pinned by multi-arch index digest (resolved 2026-10-06).
FROM docker.io/ollama/ollama:0.12.11@sha256:3d8a05e3432d50ea57594fabe971e46cc8fe963a0f9f8c40400bd56cd5388e47 AS fetch
ARG MODEL
ARG DIGEST
ENV OLLAMA_MODELS=/models HOME=/tmp
# The manifest path mirrors ollama's own name parsing (types/model/name.go ParseNameBare): the tag
# follows the last ":" only when it comes after the last "/" (else "latest"); the last segment is the
# model, the one before it the namespace (default "library"), and anything before that the host
# (default "registry.ollama.ai"). Two segments are always namespace/model, never host/model.
RUN set -e; ollama serve >/tmp/log 2>&1 & pid=$!; \
    for i in $(seq 1 30); do if ollama list >/dev/null 2>&1; then ok=1; break; fi; sleep 1; done; \
    [ "${ok:-}" = 1 ] || { echo "FATAL: ollama serve not ready after 30 s" >&2; tail -20 /tmp/log >&2; exit 1; }; \
    ollama pull "$MODEL"; kill $pid; wait $pid || true; \
    m="$MODEL"; tag=latest; ns=library; host=registry.ollama.ai; \
    case "${m##*/}" in *:*) tag="${m##*:}"; m="${m%:*}";; esac; \
    name="${m##*/}"; \
    case "$m" in */*) m="${m%/*}"; ns="${m##*/}"; case "$m" in */*) host="${m%/*}"; host="${host#*://}";; esac;; esac; \
    manifest="/models/manifests/$host/$ns/$name/$tag"; \
    [ -f "$manifest" ] || { echo "FATAL: $MODEL manifest not found at $manifest" >&2; find /models/manifests -type f >&2; exit 1; }; \
    actual=$(sha256sum "$manifest" | cut -d' ' -f1); \
    [ "$actual" = "$DIGEST" ] || { echo "FATAL: $MODEL manifest $actual != pinned $DIGEST" >&2; exit 1; }

FROM docker.io/ollama/ollama:0.12.11@sha256:3d8a05e3432d50ea57594fabe971e46cc8fe963a0f9f8c40400bd56cd5388e47
COPY --from=fetch /models /models
ENV OLLAMA_MODELS=/models

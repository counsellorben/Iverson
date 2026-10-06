# Ollama with the generative model baked in, so the serving pod needs no network (CSR round-10
# #17). The build fails unless the pulled manifest's full SHA-256 equals DIGEST (CSR round-10
# #19: the previous check compared a 12-character prefix). The chart derives this image's tag
# from the model, the digest and a hash of this file.
# Base pinned by multi-arch index digest (resolved 2026-10-06).
FROM docker.io/ollama/ollama:0.12.11@sha256:3d8a05e3432d50ea57594fabe971e46cc8fe963a0f9f8c40400bd56cd5388e47 AS fetch
ARG MODEL
ARG DIGEST
ENV OLLAMA_MODELS=/models HOME=/tmp
RUN set -e; ollama serve >/tmp/log 2>&1 & pid=$!; sleep 3; ollama pull "$MODEL"; kill $pid; \
    name="${MODEL%%:*}"; tag="${MODEL#*:}"; case "$name" in */*) ;; *) name="library/$name";; esac; \
    actual=$(sha256sum "/models/manifests/registry.ollama.ai/$name/$tag" | cut -d' ' -f1); \
    [ "$actual" = "$DIGEST" ] || { echo "FATAL: $MODEL manifest $actual != pinned $DIGEST" >&2; exit 1; }

FROM docker.io/ollama/ollama:0.12.11@sha256:3d8a05e3432d50ea57594fabe971e46cc8fe963a0f9f8c40400bd56cd5388e47
COPY --from=fetch /models /models
ENV OLLAMA_MODELS=/models

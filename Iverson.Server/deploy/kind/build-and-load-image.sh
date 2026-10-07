#!/usr/bin/env bash
set -euo pipefail

# Builds images and loads them into the kind cluster's containerd, in one of two modes:
#   - default: one app image (defaults to iverson-api), for local testing of the
#     api/worker/admin-ui images (and any other kind-based smoke test that needs one of them).
#     Loaded with `kind load docker-image`.
#   - --model-images: the TEI and Ollama model images, with the image references, model pins and
#     build args read from a `helm template` render of the chart with the --values overlay. Each
#     image is staged as an archive under /var/tmp (never /tmp, which may be RAM-backed) and
#     loaded with `kind load image-archive`.
# Both modes re-tag with the fully qualified docker.io/library/... reference before loading — see
# the comment below for why this step is not optional.
#
# Usage: deploy/kind/build-and-load-image.sh [tag] [cluster-name] [--dockerfile PATH] [--image-name NAME] [--model-images] [--values PATH]
#   tag          defaults to 0.1.0 — must match api.image.tag / worker.image.tag /
#                adminUi.image.tag in values.yaml (and values-local.yaml if it overrides them)
#   cluster-name defaults to iverson — must match the --name used for `kind create cluster`
#   --dockerfile defaults to Iverson.Server/Iverson.Api/Dockerfile (repo-root-relative or absolute)
#   --image-name defaults to iverson-api
#   --model-images builds and loads the TEI and Ollama model images (weights baked in, CSR
#                round-10 #17) instead of the app image. The positional tag is ignored in this
#                mode; cluster-name still applies. Example:
#                deploy/kind/build-and-load-image.sh 0.1.0 iverson --model-images
#   --values     overlay whose model pins the images are built from; defaults to
#                Iverson.Server/deploy/helm/iverson/values-laptop.yaml (repo-root-relative or
#                absolute). Only used with --model-images.
#
# Example (admin-ui image): deploy/kind/build-and-load-image.sh 0.1.0 iverson \
#   --dockerfile Iverson.AdminUI/Dockerfile --image-name iverson-admin-ui

DOCKERFILE_REL="Iverson.Server/Iverson.Api/Dockerfile"
IMAGE_NAME="iverson-api"
POSITIONAL=()
MODEL_IMAGES=0
VALUES_REL="Iverson.Server/deploy/helm/iverson/values-laptop.yaml"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dockerfile)
      DOCKERFILE_REL="$2"
      shift 2
      ;;
    --image-name)
      IMAGE_NAME="$2"
      shift 2
      ;;
    --model-images)
      MODEL_IMAGES=1
      shift
      ;;
    --values)
      VALUES_REL="$2"
      shift 2
      ;;
    *)
      POSITIONAL+=("$1")
      shift
      ;;
  esac
done

TAG="${POSITIONAL[0]:-0.1.0}"
CLUSTER_NAME="${POSITIONAL[1]:-iverson}"
IMAGE_LOCAL="${IMAGE_NAME}:${TAG}"
IMAGE_QUALIFIED="docker.io/library/${IMAGE_NAME}:${TAG}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"

if [[ "${MODEL_IMAGES}" = 1 ]]; then
CHART="${REPO_ROOT}/Iverson.Server/deploy/helm/iverson"
[[ "${VALUES_REL}" = /* ]] && VALUES="${VALUES_REL}" || VALUES="${REPO_ROOT}/${VALUES_REL}"
# The chart is the single source of the model pins and of the image tags (which hash the model
# Dockerfiles). Rebuild the packaged subcharts first: a stale charts/*.tgz would render the tag of
# an older Dockerfile.
(cd "${CHART}" && helm dependency build >/dev/null)
RENDER="$(helm template iverson "${CHART}" -f "${VALUES}")"

stage_and_load() {   # $1 = image as the chart renders it (bare name:tag)
  local qualified="docker.io/library/$1" archive
  archive="/var/tmp/$(echo "$1" | tr ':/' '__').tar"
  docker tag "$1" "${qualified}"   # same docker.io/library qualification as the app image, below
  # Stage on disk: the Ollama image is ~5.7 GB and /tmp may be RAM-backed. A failed save or load
  # removes the (possibly partial) archive before failing, so no multi-GB file is left behind.
  docker save -o "${archive}" "${qualified}" \
    && kind load image-archive "${archive}" --name "${CLUSTER_NAME}" \
    || { rm -f "${archive}"; return 1; }
  rm -f "${archive}"
}

# TEI: one image per embeddingModels entry; image and --model-id/--revision come from the render.
# The loop reads fd 3, so docker build and kind load cannot consume its input from stdin.
while IFS='|' read -r -u 3 image model_id revision; do
  [ -n "${image}" ] || continue
  echo "Building ${image}..."
  docker build --memory 3g -t "${image}" --build-arg MODEL_ID="${model_id}" --build-arg REVISION="${revision}" \
    -f "${CHART}/charts/tei/model-image/Dockerfile" "${CHART}/charts/tei/model-image"
  stage_and_load "${image}"
done 3< <(printf '%s\n' "${RENDER}" | awk '
  /image: "[^"]*iverson-tei-model:/ { match($0, /"[^"]+"/); img = substr($0, RSTART+1, RLENGTH-2) }
  /args: \["--model-id"/ { split($0, a, "\""); print img "|" a[4] "|" a[8] }')

# Ollama: image plus the model and full digest from the StatefulSet's annotations.
ollama_image=$(printf '%s\n' "${RENDER}" | sed -n 's/.*image: "\([^"]*iverson-ollama-model:[^"]*\)".*/\1/p' | head -1)
if [ -n "${ollama_image}" ]; then
  model=$(printf '%s\n' "${RENDER}" | sed -n 's/.*iverson.io\/model: "\(.*\)"/\1/p' | head -1)
  digest=$(printf '%s\n' "${RENDER}" | sed -n 's/.*iverson.io\/model-digest: "\(.*\)"/\1/p' | head -1)
  echo "Building ${ollama_image}..."
  docker build --memory 3g -t "${ollama_image}" --build-arg MODEL="${model}" --build-arg DIGEST="${digest}" \
    -f "${CHART}/charts/ollama/model-image/Dockerfile" "${CHART}/charts/ollama/model-image"
  stage_and_load "${ollama_image}"
fi
exit 0
fi

echo "Building ${IMAGE_LOCAL} from ${REPO_ROOT}..."
docker build -f "${REPO_ROOT}/${DOCKERFILE_REL}" -t "${IMAGE_LOCAL}" "${REPO_ROOT}"

# The Helm chart's image.repository is a bare name (e.g. "iverson-api", no registry prefix) —
# kind's containerd resolves bare names to docker.io/library/... by OCI convention. Real
# Docker's local store already treats "iverson-api:TAG" as an alias for that same qualified
# reference, so this re-tag is a harmless no-op there. But when `docker` is actually a podman
# shim (podman-docker, common on WSL2 setups — see the pids_limit comment in setup.sh for the
# same podman-detection concern), `docker build -t iverson-api:TAG` auto-qualifies the image as
# localhost/iverson-api:TAG instead. `kind load docker-image` then loads it into the node under
# that localhost/... reference, which the pod spec's bare "iverson-api" never resolves to — kubelet
# falls through to an actual registry pull attempt and fails with ErrImagePull ("pull access
# denied ... docker.io/library/iverson-api:TAG"). Explicitly tagging with the qualified reference
# before loading makes this correct under both docker and podman, unconditionally — do not skip
# this step or make it conditional on which provider is in use.
docker tag "${IMAGE_LOCAL}" "${IMAGE_QUALIFIED}"

echo "Loading ${IMAGE_QUALIFIED} into kind cluster '${CLUSTER_NAME}'..."
kind load docker-image "${IMAGE_QUALIFIED}" --name "${CLUSTER_NAME}"

echo "Done. Chart values referencing image.repository=${IMAGE_NAME}, image.tag=${TAG} will now resolve to this image with imagePullPolicy: IfNotPresent."

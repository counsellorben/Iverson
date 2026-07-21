#!/usr/bin/env bash
set -euo pipefail

# Builds an app image (defaults to iverson-api) and loads it into the kind cluster's
# containerd, for local testing of the api/worker/admin-ui images (and any other
# kind-based smoke test that needs one of these images). Always re-tags with the fully
# qualified docker.io/library/... reference before `kind load docker-image` — see the
# comment below for why this step is not optional.
#
# Usage: deploy/kind/build-and-load-image.sh [tag] [cluster-name] [--dockerfile PATH] [--image-name NAME]
#   tag          defaults to 0.1.0 — must match api.image.tag / worker.image.tag /
#                adminUi.image.tag in values.yaml (and values-local.yaml if it overrides them)
#   cluster-name defaults to iverson — must match the --name used for `kind create cluster`
#   --dockerfile defaults to Iverson.Server/Iverson.Api/Dockerfile (repo-root-relative or absolute)
#   --image-name defaults to iverson-api
#
# Example (admin-ui image): deploy/kind/build-and-load-image.sh 0.1.0 iverson \
#   --dockerfile Iverson.AdminUI/Dockerfile --image-name iverson-admin-ui

DOCKERFILE_REL="Iverson.Server/Iverson.Api/Dockerfile"
IMAGE_NAME="iverson-api"
POSITIONAL=()

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

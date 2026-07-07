#!/usr/bin/env bash
set -euo pipefail

# Builds the iverson-api image and loads it into the kind cluster's containerd, for local
# testing of the api/worker split (and any other kind-based smoke test that needs the app
# image). Always re-tags with the fully-qualified docker.io/library/... reference before
# `kind load docker-image` — see the comment below for why this step is not optional.
#
# Usage: deploy/kind/build-and-load-image.sh [tag] [cluster-name]
#   tag          defaults to 0.1.0 — must match api.image.tag / worker.image.tag in
#                values.yaml (and values-local.yaml if it overrides them)
#   cluster-name defaults to iverson — must match the --name used for `kind create cluster`

TAG="${1:-0.1.0}"
CLUSTER_NAME="${2:-iverson}"
IMAGE_LOCAL="iverson-api:${TAG}"
IMAGE_QUALIFIED="docker.io/library/iverson-api:${TAG}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"

echo "Building ${IMAGE_LOCAL} from ${REPO_ROOT}..."
docker build -f "${REPO_ROOT}/Iverson.Server/Iverson.Api/Dockerfile" -t "${IMAGE_LOCAL}" "${REPO_ROOT}"

# The Helm chart's image.repository is a bare name ("iverson-api", no registry prefix) — kind's
# containerd resolves bare names to docker.io/library/... by OCI convention. Real Docker's local
# store already treats "iverson-api:TAG" as an alias for that same qualified reference, so this
# re-tag is a harmless no-op there. But when `docker` is actually a podman shim (podman-docker,
# common on WSL2 setups — see the pids_limit comment in setup.sh for the same podman-detection
# concern), `docker build -t iverson-api:TAG` auto-qualifies the image as
# localhost/iverson-api:TAG instead. `kind load docker-image` then loads it into the node under
# that localhost/... reference, which the pod spec's bare "iverson-api" never resolves to — kubelet
# falls through to an actual registry pull attempt and fails with ErrImagePull ("pull access
# denied ... docker.io/library/iverson-api:TAG"). Explicitly tagging with the qualified reference
# before loading makes this correct under both docker and podman, unconditionally — do not skip
# this step or make it conditional on which provider is in use.
docker tag "${IMAGE_LOCAL}" "${IMAGE_QUALIFIED}"

echo "Loading ${IMAGE_QUALIFIED} into kind cluster '${CLUSTER_NAME}'..."
kind load docker-image "${IMAGE_QUALIFIED}" --name "${CLUSTER_NAME}"

echo "Done. Chart values referencing image.repository=iverson-api, image.tag=${TAG} will now resolve to this image with imagePullPolicy: IfNotPresent."

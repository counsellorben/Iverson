$ErrorActionPreference = "Stop"

# Builds the iverson-api image and loads it into the kind cluster's containerd, for local
# testing of the api/worker split (and any other kind-based smoke test that needs the app
# image). Always re-tags with the fully-qualified docker.io/library/... reference before
# `kind load docker-image` -- see the comment below for why this step is not optional.
#
# Usage: deploy/kind/build-and-load-image.ps1 [-Tag <tag>] [-ClusterName <name>]
#   Tag         defaults to 0.1.0 -- must match api.image.tag / worker.image.tag in
#               values.yaml (and values-local.yaml if it overrides them)
#   ClusterName defaults to iverson -- must match the -Name used for `kind create cluster`

param(
    [string]$Tag = "0.1.0",
    [string]$ClusterName = "iverson"
)

$ImageLocal = "iverson-api:$Tag"
$ImageQualified = "docker.io/library/iverson-api:$Tag"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../../..")

Write-Host "Building $ImageLocal from $RepoRoot..."
docker build -f (Join-Path $RepoRoot "Iverson.Server/Iverson.Api/Dockerfile") -t $ImageLocal $RepoRoot

# The Helm chart's image.repository is a bare name ("iverson-api", no registry prefix) -- kind's
# containerd resolves bare names to docker.io/library/... by OCI convention. Real Docker's local
# store already treats "iverson-api:TAG" as an alias for that same qualified reference, so this
# re-tag is a harmless no-op there. But when `docker` is actually a podman shim (podman-docker,
# common on WSL2 setups -- see the pids_limit comment in setup.ps1 for the same podman-detection
# concern), `docker build -t iverson-api:TAG` auto-qualifies the image as
# localhost/iverson-api:TAG instead. `kind load docker-image` then loads it into the node under
# that localhost/... reference, which the pod spec's bare "iverson-api" never resolves to -- kubelet
# falls through to an actual registry pull attempt and fails with ErrImagePull ("pull access
# denied ... docker.io/library/iverson-api:TAG"). Explicitly tagging with the qualified reference
# before loading makes this correct under both docker and podman, unconditionally -- do not skip
# this step or make it conditional on which provider is in use.
docker tag $ImageLocal $ImageQualified

Write-Host "Loading $ImageQualified into kind cluster '$ClusterName'..."
kind load docker-image $ImageQualified --name $ClusterName

Write-Host "Done. Chart values referencing image.repository=iverson-api, image.tag=$Tag will now resolve to this image with imagePullPolicy: IfNotPresent."

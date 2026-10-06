# Builds the iverson-api image and loads it into the kind cluster's containerd, for local
# testing of the api/worker split (and any other kind-based smoke test that needs the app
# image). Always re-tags with the fully-qualified docker.io/library/... reference before
# `kind load docker-image` -- see the comment below for why this step is not optional.
#
# Usage: deploy/kind/build-and-load-image.ps1 [-Tag <tag>] [-ClusterName <name>] [-ModelImages] [-Values <path>]
#   Tag         defaults to 0.1.0 -- must match api.image.tag / worker.image.tag in
#               values.yaml (and values-local.yaml if it overrides them)
#   ClusterName defaults to iverson -- must match the -Name used for `kind create cluster`
#   ModelImages builds and loads the TEI and Ollama model images (weights baked in, CSR
#               round-10 #17) instead of the app image. Tag is ignored in this mode;
#               ClusterName still applies.
#   Values      overlay whose model pins the images are built from; defaults to
#               Iverson.Server/deploy/helm/iverson/values-laptop.yaml (repo-root-relative or
#               absolute). Only used with -ModelImages.

param(
    [string]$Tag = "0.1.0",
    [string]$ClusterName = "iverson",
    [switch]$ModelImages,
    [string]$Values = "Iverson.Server/deploy/helm/iverson/values-laptop.yaml"
)

$ErrorActionPreference = "Stop"

$ImageLocal = "iverson-api:$Tag"
$ImageQualified = "docker.io/library/iverson-api:$Tag"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../../..")

if ($ModelImages) {
    # $ErrorActionPreference does not cover a native command's exit code, so check it explicitly.
    function Assert-Native($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" } }

    $chart = Join-Path $RepoRoot "Iverson.Server/deploy/helm/iverson"
    if ([IO.Path]::IsPathRooted($Values)) { $valuesPath = $Values } else { $valuesPath = Join-Path $RepoRoot $Values }
    # The chart is the single source of the model pins and of the image tags (which hash the model
    # Dockerfiles). Rebuild the packaged subcharts first: a stale charts/*.tgz would render the tag
    # of an older Dockerfile.
    Push-Location $chart
    try { helm dependency build | Out-Null; Assert-Native "helm dependency build" } finally { Pop-Location }
    $render = helm template iverson $chart -f $valuesPath | Out-String
    Assert-Native "helm template"
    if ([string]::IsNullOrWhiteSpace($render)) { throw "helm template rendered nothing" }

    function Build-And-Load($img, $dockerfile, $contextDir, $buildArgs) {
        Write-Host "Building $img..."
        docker build --memory 3g -t $img @buildArgs -f $dockerfile $contextDir
        Assert-Native "docker build"
        $qualified = "docker.io/library/$img"
        docker tag $img $qualified   # same docker.io/library qualification as the app image, below
        Assert-Native "docker tag"
        # Stage on disk: the Ollama image is ~5.7 GB and a RAM-backed temp dir may not hold it.
        $archive = Join-Path ([IO.Path]::GetTempPath()) (($img -replace '[:/]','_') + '.tar')
        docker save -o $archive $qualified
        Assert-Native "docker save"
        kind load image-archive $archive --name $ClusterName
        Assert-Native "kind load image-archive"
        Remove-Item $archive
    }

    # TEI: one image per embeddingModels entry; each image line pairs with the next args line.
    $teiImages = [regex]::Matches($render, 'image: "([^"]*iverson-tei-model:[^"]*)"')
    $teiArgs = [regex]::Matches($render, 'args: \["--model-id", "([^"]+)", "--revision", "([^"]+)"')
    for ($i = 0; $i -lt $teiImages.Count; $i++) {
        Build-And-Load $teiImages[$i].Groups[1].Value (Join-Path $chart "charts/tei/model-image/Dockerfile") (Join-Path $chart "charts/tei/model-image") @(
            "--build-arg", "MODEL_ID=$($teiArgs[$i].Groups[1].Value)",
            "--build-arg", "REVISION=$($teiArgs[$i].Groups[2].Value)")
    }

    # Ollama: image plus the model and full digest from the StatefulSet's annotations.
    $ollamaImage = [regex]::Match($render, 'image: "([^"]*iverson-ollama-model:[^"]*)"')
    if ($ollamaImage.Success) {
        $model = [regex]::Match($render, 'iverson\.io/model: "([^"]+)"').Groups[1].Value
        $digest = [regex]::Match($render, 'iverson\.io/model-digest: "([^"]+)"').Groups[1].Value
        Build-And-Load $ollamaImage.Groups[1].Value (Join-Path $chart "charts/ollama/model-image/Dockerfile") (Join-Path $chart "charts/ollama/model-image") @(
            "--build-arg", "MODEL=$model",
            "--build-arg", "DIGEST=$digest")
    }
    exit 0
}

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

#!/usr/bin/env bash
# CSR round 12 §4.1: Istio's base, istiod, istio-cni and ztunnel charts, vendored from the release
# tarball because no Helm index or OCI registry carries 1.31.x yet. Move them back to an index once one
# does. modules/operators/main.tf (helm_release) and deploy/kind/setup.sh and setup.ps1 install from the
# four directories this script writes next to itself:
#
#   Source:  https://github.com/istio/istio/releases/download/1.31.3/istio-1.31.3-linux-amd64.tar.gz
#   SHA-256: b32bba09bc6138d738fd19a09ef6d0fcdd5c5c013efeb72969663c24a2bddd47
#
# Each directory is the tarball's manifests/charts/<chart> copied unmodified (istio-discovery is
# manifests/charts/istio-control/istio-discovery). Re-running replaces all four.
set -euo pipefail
VERSION=1.31.3
URL="https://github.com/istio/istio/releases/download/${VERSION}/istio-${VERSION}-linux-amd64.tar.gz"
SHA256=b32bba09bc6138d738fd19a09ef6d0fcdd5c5c013efeb72969663c24a2bddd47
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
curl -fsSL -o "$WORK/istio.tar.gz" "$URL"
echo "$SHA256  $WORK/istio.tar.gz" | sha256sum -c -
tar -xzf "$WORK/istio.tar.gz" -C "$WORK" "istio-${VERSION}/manifests/charts"
for chart in base istio-control/istio-discovery istio-cni ztunnel; do
  rm -rf "${HERE:?}/${chart##*/}"
  cp -R "$WORK/istio-${VERSION}/manifests/charts/$chart" "$HERE/${chart##*/}"
done

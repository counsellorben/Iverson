#!/usr/bin/env bash
# Generate Go gRPC stubs from proto files.
# Run from the Iverson.Clients/Go/ directory.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GO_DIR="$(dirname "$SCRIPT_DIR")"
PROTO_DIR="$GO_DIR/../../Proto"

export PATH=~/sdk/go1.22/bin:~/go/bin:$PATH
export GOPATH=~/go
export GOROOT=~/sdk/go1.22

PROTOC=~/sdk/protoc/bin/protoc
GO_PKG="github.com/iverson/clients/go/generated"

mkdir -p "$GO_DIR/generated"

"$PROTOC" \
  --go_out="$GO_DIR/generated" \
  --go_opt=paths=source_relative \
  "--go_opt=Mobject_mapping.proto=${GO_PKG}" \
  "--go_opt=Mobject_persistence.proto=${GO_PKG}" \
  "--go_opt=Mobject_retrieval.proto=${GO_PKG}" \
  "--go_opt=Mobject_search.proto=${GO_PKG}" \
  --go-grpc_out="$GO_DIR/generated" \
  --go-grpc_opt=paths=source_relative \
  "--go-grpc_opt=Mobject_mapping.proto=${GO_PKG}" \
  "--go-grpc_opt=Mobject_persistence.proto=${GO_PKG}" \
  "--go-grpc_opt=Mobject_retrieval.proto=${GO_PKG}" \
  "--go-grpc_opt=Mobject_search.proto=${GO_PKG}" \
  -I "$PROTO_DIR" \
  "$PROTO_DIR"/*.proto

echo "Proto stubs generated in $GO_DIR/generated/"

#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TS_DIR="$(dirname "$SCRIPT_DIR")"
PROTO_DIR="$(dirname "$(dirname "$TS_DIR")")/Iverson.Clients/Common/Proto"

cd "$TS_DIR"

mkdir -p generated

~/sdk/protoc/bin/protoc \
  --plugin=./node_modules/.bin/protoc-gen-ts_proto \
  --ts_proto_out=generated \
  --ts_proto_opt=outputServices=grpc-js,esModuleInterop=true \
  -I"$PROTO_DIR" \
  "$PROTO_DIR"/*.proto

echo "Proto generation complete."

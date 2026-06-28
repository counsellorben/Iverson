#!/usr/bin/env bash
set -e
cd "$(dirname "$0")/.."
PROTO_DIR="../Proto"
OUT_DIR="iverson_client/generated"
mkdir -p "$OUT_DIR"
touch "$OUT_DIR/__init__.py"
python3 -m grpc_tools.protoc \
  -I"$PROTO_DIR" \
  --python_out="$OUT_DIR" \
  --grpc_python_out="$OUT_DIR" \
  object_mapping.proto object_persistence.proto object_retrieval.proto object_search.proto
# Fix relative imports in generated grpc files so they work as package imports
sed -i 's/^import object_/from iverson_client.generated import object_/' "$OUT_DIR"/*_pb2_grpc.py
echo "Proto stubs generated in $OUT_DIR"

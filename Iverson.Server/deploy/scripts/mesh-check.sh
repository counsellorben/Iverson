#!/usr/bin/env bash
set -uo pipefail

# CSR round 12 (docs/specs/2026-10-19-csr-round12-platform-design.md §4.1, §4.2 and §6): live checks
# of the Istio ambient mesh on a running release.
#
#   M1  every running pod of the release namespace is enrolled (istio-cni set
#       ambient.istio.io/redirection=enabled on it);
#   M2  plaintext from a pod outside the mesh to a StarRocks BE port is refused, while a NetworkPolicy
#       admits that pod, and ztunnel logs the refusal;
#   M3  ztunnel reports mutual TLS for StarRocks FE -> BE and BE -> FE connections;
#   M4  the api is refused on a Jaeger port its rule does not list (16686) and reaches one it lists (4318);
#   M5  the edge gateway reaches the api: a gRPC call through the edge gets the api's own Bearer
#       challenge.
#
# M2 and M3 report SKIP when the release runs no StarRocks, and M4 when it runs no Jaeger (the laptop
# profile runs neither). Run it as the operators group: M4 execs into an api pod. It creates a
# namespace with one pod and one NetworkPolicy in the release namespace, all named
# mesh-check-<random>, and deletes them when it exits.
#
# Usage: mesh-check.sh [--release NAME] [--namespace NS]   (both default to iverson)
# Environment (the defaults fit the kind profile):
#   OPERATOR_AS      kubectl impersonation flags that add the operators group (default:
#                    --as=kubernetes-admin --as-group=kubeadm:cluster-admins --as-group=iverson-operators)
#   EDGE_GRPC        the api's gRPC host on the edge (default https://iverson.local:8443)
#   EDGE_ADDR        the address EDGE_GRPC's host resolves to (default 127.0.0.1)
#   EDGE_TLS_SECRET  the Secret holding the edge certificate, in the release namespace (default iverson-api-tls)
#   PROBE_IMAGE      the image of the pod outside the mesh (default docker.io/library/busybox:1.37.0)
# Exit status: 0 when no check fails, 1 when any fails, 2 on a usage error.

OPERATOR_AS=${OPERATOR_AS:---as=kubernetes-admin --as-group=kubeadm:cluster-admins --as-group=iverson-operators}
EDGE_GRPC=${EDGE_GRPC:-https://iverson.local:8443}
EDGE_ADDR=${EDGE_ADDR:-127.0.0.1}
EDGE_TLS_SECRET=${EDGE_TLS_SECRET:-iverson-api-tls}
PROBE_IMAGE=${PROBE_IMAGE:-docker.io/library/busybox:1.37.0}
RELEASE=iverson
NS=iverson
PASSED=0
FAILED=0
SKIPPED=0

usage() { echo "usage: ${0##*/} [--release NAME] [--namespace NS]" >&2; }

report() {  # <PASS|FAIL|SKIP> <id> <text>
  case "$1" in
    PASS) PASSED=$((PASSED + 1)) ;;
    SKIP) SKIPPED=$((SKIPPED + 1)) ;;
    *) FAILED=$((FAILED + 1)) ;;
  esac
  printf '%s %s %s\n' "$1" "$2" "$3"
}

result() {  # <id> <exit status of the check> <text>
  if [ "$2" -eq 0 ]; then report PASS "$1" "$3"; else report FAIL "$1" "$3"; fi
}

# Reads `kubectl get pods -o json` on stdin; prints the running pods that are not enrolled, one per
# line, after a first line with the number of running pods.
UNENROLLED_PY='
import json, sys
pods = [p for p in json.load(sys.stdin)["items"] if p["status"].get("phase") == "Running"]
print(len(pods))
for p in pods:
    if (p["metadata"].get("annotations") or {}).get("ambient.istio.io/redirection") != "enabled":
        print(p["metadata"]["name"])
'

# True when ztunnel recorded, as the destination, a mutual-TLS connection from workload <src> to
# workload <dst>.
mtls_seen() {  # <metrics file> <source workload> <destination workload>
  command grep -E '^istio_tcp_connections_opened_total\{' "$1" \
    | command grep -F 'reporter="destination"' \
    | command grep -F "source_workload=\"$2\"" \
    | command grep -F "destination_workload=\"$3\"" \
    | command grep -qF 'connection_security_policy="mutual_tls"'
}

# Bytes an api container receives for a plain HTTP request to <host>:<port>. ztunnel closes a
# connection its policy refuses, so a refused hop returns 0 bytes.
hop_bytes() {  # <api pod> <host> <port>
  kubectl "${OPS[@]}" -n "$NS" exec "$1" -c api -- bash -c \
    'exec 3<>"/dev/tcp/$1/$2" || exit 0; printf "GET / HTTP/1.0\r\nHost: probe\r\n\r\n" >&3; timeout 5 cat <&3' \
    _ "$2" "$3" 2>/dev/null | wc -c
}

# Forwards a free local port to <pod>:<port> in <namespace>; sets PF_PID and PF_PORT.
port_forward() {  # <namespace> <pod> <port>
  kubectl -n "$1" port-forward "$2" ":$3" > "$WORK/port-forward.log" 2>&1 &
  PF_PID=$!
  local _
  for _ in $(seq 1 30); do
    PF_PORT=$(sed -nE 's/^Forwarding from 127\.0\.0\.1:([0-9]+) .*/\1/p' "$WORK/port-forward.log" | head -n 1)
    [ -n "$PF_PORT" ] && return 0
    kill -0 "$PF_PID" 2>/dev/null || return 1
    sleep 1
  done
  return 1
}

cleanup() {
  [ -n "${PF_PID:-}" ] && kill "$PF_PID" 2>/dev/null
  kubectl -n "$NS" delete networkpolicy "$PROBE" --ignore-not-found --wait=false > /dev/null 2>&1
  kubectl delete namespace "$PROBE" --ignore-not-found --wait=false > /dev/null 2>&1
  rm -rf "$WORK"
}

if [[ "${BASH_SOURCE[0]}" != "$0" ]]; then
  return 0
fi

while [ $# -gt 0 ]; do
  case "$1" in
    --release) [ $# -ge 2 ] || { usage; exit 2; }; RELEASE=$2; shift 2 ;;
    --namespace) [ $# -ge 2 ] || { usage; exit 2; }; NS=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage; exit 2 ;;
  esac
done

read -r -a OPS <<< "$OPERATOR_AS"
PROBE="mesh-check-$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')"
WORK=$(mktemp -d)
PF_PID=""
trap cleanup EXIT

# M1: enrolment.
mapfile -t lines < <(kubectl -n "$NS" get pods -o json | python3 -c "$UNENROLLED_PY")
running=${lines[0]:-0}
unenrolled=("${lines[@]:1}")
[ "$running" -gt 0 ] && [ "${#unenrolled[@]}" -eq 0 ]
result M1 $? "$running running pods in $NS, not enrolled: ${unenrolled[*]:-none}"

be_ip=$(kubectl -n "$NS" get pods -l "app.starrocks.ownerreference/name=$RELEASE-starrocks-be" \
  -o jsonpath='{.items[0].status.podIP}' 2>/dev/null)
fe_ip=$(kubectl -n "$NS" get pods -l "app.starrocks.ownerreference/name=$RELEASE-starrocks-fe" \
  -o jsonpath='{.items[0].status.podIP}' 2>/dev/null)

# M2: plaintext from outside the mesh to the BE web port, with NetworkPolicy admitting it.
if [ -z "$be_ip" ]; then
  report SKIP M2 "no StarRocks BE pod in $NS"
else
  kubectl create namespace "$PROBE" > /dev/null
  kubectl -n "$NS" apply -f - > /dev/null <<EOF
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: $PROBE
spec:
  podSelector:
    matchLabels:
      app.starrocks.ownerreference/name: $RELEASE-starrocks-be
  policyTypes: ["Ingress"]
  ingress:
    - from:
        - namespaceSelector:
            matchLabels:
              kubernetes.io/metadata.name: $PROBE
      ports:
        - { protocol: TCP, port: 8040 }
EOF
  kubectl -n "$PROBE" run probe --image="$PROBE_IMAGE" --restart=Never -- sleep 600 > /dev/null
  kubectl -n "$PROBE" wait --for=condition=Ready pod/probe --timeout=180s > /dev/null
  probe_ip=$(kubectl -n "$PROBE" get pod probe -o jsonpath='{.status.podIP}')
  since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  kubectl -n "$PROBE" exec probe -- wget -q -O- -T 5 "http://$be_ip:8040/api/health" > /dev/null 2>&1
  answered=$?
  sleep 2
  rejections=$(kubectl -n istio-system logs -l app=ztunnel --since-time="$since" --tail=-1 2>/dev/null \
    | command grep -F "$probe_ip" | command grep -ciE 'rejection|denied')
  [ "$answered" -ne 0 ] && [ "${rejections:-0}" -gt 0 ]
  result M2 $? "plaintext $probe_ip -> BE $be_ip:8040: wget exit $answered, ztunnel refusals logged: ${rejections:-0}"
fi

# M3: FE <-> BE mutual TLS, as every ztunnel reports it.
if [ -z "$be_ip" ] || [ -z "$fe_ip" ]; then
  report SKIP M3 "no StarRocks FE and BE pods in $NS"
else
  : > "$WORK/metrics.txt"
  for zt in $(kubectl -n istio-system get pods -l app=ztunnel -o name); do
    if port_forward istio-system "$zt" 15020; then
      curl -s -m 15 "http://127.0.0.1:$PF_PORT/metrics" >> "$WORK/metrics.txt"
    fi
    kill "$PF_PID" 2>/dev/null
    PF_PID=""
  done
  fe="$RELEASE-starrocks-fe"
  be="$RELEASE-starrocks-be"
  mtls_seen "$WORK/metrics.txt" "$fe" "$be" && fe_be=yes || fe_be=no
  mtls_seen "$WORK/metrics.txt" "$be" "$fe" && be_fe=yes || be_fe=no
  [ "$fe_be" = yes ] && [ "$be_fe" = yes ]
  result M3 $? "ztunnel mutual_tls $fe -> $be: $fe_be, $be -> $fe: $be_fe"
fi

# M4: a port the api's Jaeger rule does not list.
api=$(kubectl -n "$NS" get pods -l "app=$RELEASE-api" --field-selector=status.phase=Running \
  -o jsonpath='{.items[0].metadata.name}' 2>/dev/null)
if ! kubectl -n "$NS" get service "$RELEASE-jaeger" > /dev/null 2>&1; then
  report SKIP M4 "no $RELEASE-jaeger Service in $NS"
else
  refused=$(hop_bytes "$api" "$RELEASE-jaeger" 16686)
  admitted=$(hop_bytes "$api" "$RELEASE-jaeger" 4318)
  [ "$refused" -eq 0 ] && [ "$admitted" -gt 0 ]
  result M4 $? "$api -> $RELEASE-jaeger: 16686 returned $refused bytes, 4318 returned $admitted bytes"
fi

# M5: the edge gateway reaches the api.
hostport=${EDGE_GRPC#https://}
host=${hostport%%:*}
port=${hostport##*:}
kubectl -n "$NS" get secret "$EDGE_TLS_SECRET" -o jsonpath='{.data.tls\.crt}' | base64 -d > "$WORK/edge.crt"
printf '\0\0\0\0\0' > "$WORK/empty.grpc"
headers=$(curl -sS -m 20 --http2 --cacert "$WORK/edge.crt" --resolve "$host:$port:$EDGE_ADDR" \
  -H 'content-type: application/grpc' -H 'te: trailers' --data-binary @"$WORK/empty.grpc" \
  -o /dev/null -D - "$EDGE_GRPC/iverson.ObjectSearchService/Search" 2>&1 | tr -d '\r')
status=$(printf '%s\n' "$headers" | sed -nE 's#^HTTP/[0-9.]+ ([0-9]+).*#\1#p' | head -n 1)
challenge=$(printf '%s\n' "$headers" | command grep -i '^www-authenticate: Bearer' | head -n 1)
[ "$status" = 401 ] && [ -n "$challenge" ]
result M5 $? "unauthenticated gRPC Search through $EDGE_GRPC: HTTP ${status:-none}, ${challenge:-no Bearer challenge}"

echo "${0##*/}: $PASSED passed, $FAILED failed, $SKIPPED skipped"
[ "$FAILED" -eq 0 ]

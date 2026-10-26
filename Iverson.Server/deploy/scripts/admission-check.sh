#!/usr/bin/env bash
set -uo pipefail

# CSR round 12 (docs/specs/2026-10-19-csr-round12-platform-design.md §3.1 and §6): live checks of the
# release's admission policy <release>-<namespace>-sensitive-identities, and of the two Authentik
# pins against a legacy service-account-token Secret's token.
#
# A non-operator here is a user with the namespace's built-in admin role plus the right to add
# ephemeral containers, so every refusal it meets comes from the admission policy, not from RBAC.
# The operators group is OPERATOR_AS. Each line names the request and its outcome: "admitted",
# "refused" (by the admission policy) or "error" (anything else).
#
#   N1-N8  a non-operator is refused: a token for <release>-api and for <release>-admin-automation,
#          exec into and port-forward to an api pod, a bare pod and a Job with a protected account,
#          an ephemeral container on an api pod, and an in-place image change of an api pod;
#   O1-O8  the operators group is admitted for each of those;
#   N9/O9  a non-operator is refused exec into the Postgres instance pod, the operators group admitted;
#   N10/O11 a non-operator is refused, and the operators group admitted, the API server's proxy to an
#          api pod (pods/proxy), to Service <release>-authentik and to the unprotected-name Service
#          <release>-qdrant (services/proxy: every Service's proxy is operator-only, whatever its name);
#   O10    the operators group is refused a protected-account pod named outside <release>-api-*;
#   L1/L2  a legacy service-account-token Secret's token for <release>-api is refused by the
#          orchestrator pin and one for <release>-admin-automation by the admin-automation pin;
#          a TokenRequest token of each account, the api's bound to a running api pod, is accepted.
#
# Bare pods, Jobs and image changes are server-side dry runs, which run admission and store nothing.
# Ephemeral containers cannot be dry-run: the operators' one goes on a throwaway pod named
# <release>-api-admission-check-<random> with the default service account. Tokens are discarded and
# never printed. It creates, and deletes when it exits, that pod, a Role and two RoleBindings named
# admission-check-<random>, and two legacy token Secrets of the same name prefix.
#
# Usage: admission-check.sh [--release NAME] [--namespace NS]   (both default to iverson)
# Environment (the defaults fit the kind profile):
#   OPERATOR_AS  kubectl impersonation flags that add the operators group (default:
#                --as=kubernetes-admin --as-group=kubeadm:cluster-admins --as-group=iverson-operators)
#   PROBE_IMAGE  the image of the probe's own containers (default docker.io/library/busybox:1.37.0)
# Exit status: 0 when every check passes, 1 when any fails, 2 on a usage error.

OPERATOR_AS=${OPERATOR_AS:---as=kubernetes-admin --as-group=kubeadm:cluster-admins --as-group=iverson-operators}
PROBE_IMAGE=${PROBE_IMAGE:-docker.io/library/busybox:1.37.0}
RELEASE=iverson
NS=iverson
PASSED=0
FAILED=0

usage() { echo "usage: ${0##*/} [--release NAME] [--namespace NS]" >&2; }

report() {  # <PASS|FAIL> <id> <text>
  if [ "$1" = PASS ]; then PASSED=$((PASSED + 1)); else FAILED=$((FAILED + 1)); fi
  printf '%s %s %s\n' "$1" "$2" "$3"
}

# admitted when the request succeeded, refused when the error names the release's admission policy,
# error otherwise.
classify() {  # <exit code> <output> <policy name>
  if [ "$1" -eq 0 ]; then
    echo admitted
  elif printf '%s' "$2" | command grep -qF -- "$3"; then
    echo refused
  else
    echo error
  fi
}

# Runs one request and reports it against the wanted outcome. A request's output is shown only when
# it failed, so an issued token is never printed.
expect() {  # <id> <text> <admitted|refused> <command...>
  local id=$1 text=$2 want=$3 out rc got
  shift 3
  out=$("$@" 2>&1)
  rc=$?
  got=$(classify "$rc" "$out" "$POLICY")
  if [ "$got" = "$want" ]; then
    report PASS "$id" "$text: $got"
  elif [ "$rc" -eq 0 ]; then
    report FAIL "$id" "$text: $got, wanted $want"
  else
    report FAIL "$id" "$text: $got, wanted $want ($(printf '%s' "$out" | head -n 1 | cut -c1-200))"
  fi
}

# kubectl port-forward to <pod>:8081 and one HTTPS request through it; prints the classified outcome.
port_forward_outcome() {  # <impersonation flags...>
  local log="$WORK/port-forward-$RANDOM.log" pid lp="" code="000" _
  kubectl "$@" -n "$NS" port-forward "pod/$API_POD" :8081 > "$log" 2>&1 &
  pid=$!
  for _ in $(seq 1 15); do
    lp=$(sed -nE 's/^Forwarding from 127\.0\.0\.1:([0-9]+) .*/\1/p' "$log" | head -n 1)
    [ -n "$lp" ] && break
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
  done
  [ -n "$lp" ] && code=$(curl -sk -o /dev/null -m 10 -w '%{http_code}' "https://127.0.0.1:$lp/health")
  sleep 1
  kill "$pid" 2>/dev/null
  wait "$pid" 2>/dev/null
  if command grep -qF -- "$POLICY" "$log"; then
    echo refused
  elif [ -n "$lp" ] && [ "$code" != 000 ]; then
    echo admitted
  else
    echo error
  fi
}

# The API server's proxy to <pod>:8081 (pods/proxy), to svc/<release>-authentik:9000 and to
# svc/<release>-qdrant:6333 (services/proxy), one GET each. Prints "refused" when the admission policy
# refused all three, "admitted" when all three passed admission and RBAC, and "error" otherwise.
# Passing means a response, or an error the API server returns after admission: the backend's own
# error status, or its failure to reach the backend (on the clouds the API server is outside the
# mesh, so a STRICT port answers that way). No response body is printed.
proxy_outcome() {  # <impersonation flags...>
  local path out rc refused=0 passed=0 total=0
  for path in "pods/https:$API_POD:8081/proxy/health" "services/http:$RELEASE-authentik:9000/proxy/-/health/live/" \
      "services/https:$RELEASE-qdrant:6333/proxy/readyz"; do
    total=$((total + 1))
    out=$(kubectl "$@" get --raw "/api/v1/namespaces/$NS/$path" 2>&1)
    rc=$?
    if printf '%s' "$out" | command grep -qF -- "$POLICY"; then
      refused=$((refused + 1))
    elif [ "$rc" -eq 0 ] || { printf '%s' "$out" | command grep -q '^Error from server' \
        && ! printf '%s' "$out" | command grep -qi 'forbidden'; }; then
      passed=$((passed + 1))
    fi
  done
  if [ "$refused" -eq "$total" ]; then echo refused; elif [ "$passed" -eq "$total" ]; then echo admitted; else echo error; fi
}

expect_proxy() {  # <id> <text> <wanted> <impersonation flags...>
  local id=$1 text=$2 want=$3 got
  shift 3
  got=$(proxy_outcome "$@")
  if [ "$got" = "$want" ]; then report PASS "$id" "$text: $got"; else report FAIL "$id" "$text: $got, wanted $want"; fi
}

expect_port_forward() {  # <id> <text> <wanted> <impersonation flags...>
  local id=$1 text=$2 want=$3 got
  shift 3
  got=$(port_forward_outcome "$@")
  if [ "$got" = "$want" ]; then report PASS "$id" "$text: $got"; else report FAIL "$id" "$text: $got, wanted $want"; fi
}

# A token exchange at Authentik's token endpoint through the port-forward, with the assertion in
# PROBE_ASSERTION. Prints the HTTP status and the OAuth error code, never the issued token.
EXCHANGE_PY='
import json, os, sys, urllib.error, urllib.parse, urllib.request
base, client_id, scope = sys.argv[1:4]
body = urllib.parse.urlencode({
    "grant_type": "client_credentials",
    "client_id": client_id,
    "client_assertion_type": "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
    "client_assertion": os.environ["PROBE_ASSERTION"],
    "scope": scope,
}).encode()
request = urllib.request.Request(base + "/application/o/token/", data=body, method="POST",
                                 headers={"Content-Type": "application/x-www-form-urlencoded"})
try:
    with urllib.request.urlopen(request, timeout=30) as response:
        print(response.status, "-")
except urllib.error.HTTPError as e:
    try:
        error = json.loads(e.read()).get("error", "?")
    except ValueError:
        error = "?"
    print(e.code, error)
'

exchange() {  # <assertion> <client id> <scope>
  PROBE_ASSERTION=$1 python3 -c "$EXCHANGE_PY" "http://127.0.0.1:$PF_PORT" "$2" "$3" 2>&1 | tail -n 1
}

expect_exchange() {  # <id> <text> <wanted "status error"> <assertion> <client id> <scope>
  local got
  got=$(exchange "$4" "$5" "$6")
  if [ "$got" = "$3" ]; then report PASS "$1" "$2: $got"; else report FAIL "$1" "$2: $got, wanted $3"; fi
}

legacy_token() {  # <secret name> <service account>: creates the Secret, prints its token once filled
  kubectl -n "$NS" apply -f - > /dev/null <<EOF
apiVersion: v1
kind: Secret
metadata:
  name: $1
  annotations:
    kubernetes.io/service-account.name: $2
type: kubernetes.io/service-account-token
EOF
  local token="" _
  for _ in $(seq 1 30); do
    token=$(kubectl -n "$NS" get secret "$1" -o jsonpath='{.data.token}' 2>/dev/null | base64 -d)
    [ -n "$token" ] && break
    sleep 1
  done
  printf '%s' "$token"
}

start_port_forward() {  # forwards a free local port to svc/<release>-authentik:9000; sets PF_PID, PF_PORT
  kubectl "${OPS[@]}" -n "$NS" port-forward "svc/$RELEASE-authentik" :9000 > "$WORK/authentik-forward.log" 2>&1 &
  PF_PID=$!
  local _
  for _ in $(seq 1 30); do
    PF_PORT=$(sed -nE 's/^Forwarding from 127\.0\.0\.1:([0-9]+) .*/\1/p' "$WORK/authentik-forward.log" | head -n 1)
    [ -n "$PF_PORT" ] && return 0
    kill -0 "$PF_PID" 2>/dev/null || return 1
    sleep 1
  done
  return 1
}

cleanup() {
  [ -n "${PF_PID:-}" ] && kill "$PF_PID" 2>/dev/null
  kubectl -n "$NS" delete pod "$THROWAWAY" --ignore-not-found --wait=false > /dev/null 2>&1
  kubectl -n "$NS" delete secret "$PROBE-api" "$PROBE-automation" --ignore-not-found > /dev/null 2>&1
  kubectl -n "$NS" delete rolebinding "$PROBE-admin" "$PROBE-ephemeral" --ignore-not-found > /dev/null 2>&1
  kubectl -n "$NS" delete role "$PROBE-ephemeral" --ignore-not-found > /dev/null 2>&1
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
SUFFIX=$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')
PROBE="admission-check-$SUFFIX"
NONOP_USER="admission-check-nonoperator"
NON=(--as="$NONOP_USER")
POLICY="$RELEASE-$NS-sensitive-identities"
THROWAWAY="$RELEASE-api-admission-check-$SUFFIX"
API_SA="$RELEASE-api"
AUTOMATION_SA="$RELEASE-admin-automation"
WORK=$(mktemp -d)
PF_PID=""
PF_PORT=""
trap cleanup EXIT

kubectl -n "$NS" apply -f - > /dev/null <<EOF
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
  name: $PROBE-admin
roleRef: { apiGroup: rbac.authorization.k8s.io, kind: ClusterRole, name: admin }
subjects: [{ apiGroup: rbac.authorization.k8s.io, kind: User, name: $NONOP_USER }]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: $PROBE-ephemeral
rules:
  - { apiGroups: [""], resources: ["pods/ephemeralcontainers"], verbs: ["get", "patch", "update"] }
  - { apiGroups: [""], resources: ["serviceaccounts/token"], verbs: ["create"] }
---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
  name: $PROBE-ephemeral
roleRef: { apiGroup: rbac.authorization.k8s.io, kind: Role, name: $PROBE-ephemeral }
subjects: [{ apiGroup: rbac.authorization.k8s.io, kind: User, name: $NONOP_USER }]
EOF

API_POD=$(kubectl -n "$NS" get pods -l "app=$RELEASE-api" --field-selector=status.phase=Running \
  -o jsonpath='{.items[0].metadata.name}')
API_UID=$(kubectl -n "$NS" get pod "$API_POD" -o jsonpath='{.metadata.uid}')
POSTGRES_POD="$RELEASE-postgres-1"
cat > "$WORK/job.yaml" <<EOF
apiVersion: batch/v1
kind: Job
metadata:
  name: $PROBE
  namespace: $NS
spec:
  template:
    spec:
      serviceAccountName: $AUTOMATION_SA
      restartPolicy: Never
      containers:
        - { name: probe, image: "$PROBE_IMAGE", command: ["true"] }
EOF
bare() {  # <impersonation flags...> -- <pod name> <service account>
  local flags=()
  while [ "$1" != -- ]; do flags+=("$1"); shift; done
  kubectl "${flags[@]}" -n "$NS" run "$2" --image="$PROBE_IMAGE" --restart=Never --dry-run=server \
    --overrides="{\"spec\":{\"serviceAccountName\":\"$3\"}}" -- true
}

# N1-N10: the non-operator.
expect N1 "non-operator: token for $API_SA" refused \
  kubectl "${NON[@]}" -n "$NS" create token "$API_SA" --audience admission-check --duration 10m
expect N2 "non-operator: token for $AUTOMATION_SA" refused \
  kubectl "${NON[@]}" -n "$NS" create token "$AUTOMATION_SA" --audience admission-check --duration 10m
expect N3 "non-operator: exec into $API_POD" refused \
  kubectl "${NON[@]}" -n "$NS" exec "$API_POD" -c api -- true
expect_port_forward N4 "non-operator: port-forward to $API_POD" refused "${NON[@]}"
expect N5 "non-operator: bare pod $RELEASE-api-$SUFFIX with $API_SA (dry run)" refused \
  bare "${NON[@]}" -- "$RELEASE-api-$SUFFIX" "$API_SA"
expect N6 "non-operator: Job with $AUTOMATION_SA (dry run)" refused \
  kubectl "${NON[@]}" create --dry-run=server -f "$WORK/job.yaml"
expect N7 "non-operator: ephemeral container on $API_POD" refused \
  kubectl "${NON[@]}" -n "$NS" debug "$API_POD" --image="$PROBE_IMAGE" --profile=baseline -c "$PROBE" -- true
expect N8 "non-operator: image change of $API_POD (dry run)" refused \
  kubectl "${NON[@]}" -n "$NS" set image "pod/$API_POD" "api=$PROBE_IMAGE" --dry-run=server
expect N9 "non-operator: exec into $POSTGRES_POD" refused \
  kubectl "${NON[@]}" -n "$NS" exec "$POSTGRES_POD" -c postgres -- true
expect_proxy N10 "non-operator: API-server proxy to $API_POD, svc/$RELEASE-authentik and svc/$RELEASE-qdrant" refused "${NON[@]}"

# O1-O11: the operators group.
expect O1 "operators: token for $API_SA" admitted \
  kubectl "${OPS[@]}" -n "$NS" create token "$API_SA" --audience admission-check --duration 10m
expect O2 "operators: token for $AUTOMATION_SA" admitted \
  kubectl "${OPS[@]}" -n "$NS" create token "$AUTOMATION_SA" --audience admission-check --duration 10m
expect O3 "operators: exec into $API_POD" admitted \
  kubectl "${OPS[@]}" -n "$NS" exec "$API_POD" -c api -- true
expect_port_forward O4 "operators: port-forward to $API_POD" admitted "${OPS[@]}"
expect O5 "operators: bare pod $RELEASE-api-$SUFFIX with $API_SA (dry run)" admitted \
  bare "${OPS[@]}" -- "$RELEASE-api-$SUFFIX" "$API_SA"
expect O6 "operators: Job with $AUTOMATION_SA (dry run)" admitted \
  kubectl "${OPS[@]}" create --dry-run=server -f "$WORK/job.yaml"
kubectl "${OPS[@]}" -n "$NS" run "$THROWAWAY" --image="$PROBE_IMAGE" --restart=Never -- sleep 600 > /dev/null
kubectl -n "$NS" wait --for=condition=Ready "pod/$THROWAWAY" --timeout=180s > /dev/null
expect O7 "operators: ephemeral container on $THROWAWAY" admitted \
  kubectl "${OPS[@]}" -n "$NS" debug "$THROWAWAY" --image="$PROBE_IMAGE" --profile=baseline -c "$PROBE" -- true
expect O8 "operators: image change of $API_POD (dry run)" admitted \
  kubectl "${OPS[@]}" -n "$NS" set image "pod/$API_POD" "api=$PROBE_IMAGE" --dry-run=server
expect O9 "operators: exec into $POSTGRES_POD" admitted \
  kubectl "${OPS[@]}" -n "$NS" exec "$POSTGRES_POD" -c postgres -- true
expect O10 "operators: bare pod $PROBE with $API_SA (dry run)" refused \
  bare "${OPS[@]}" -- "$PROBE" "$API_SA"
expect_proxy O11 "operators: API-server proxy to $API_POD, svc/$RELEASE-authentik and svc/$RELEASE-qdrant" admitted "${OPS[@]}"

# L1, L2: legacy service-account-token Secrets against the two pins, each beside an accepted control.
if start_port_forward; then
  legacy=$(legacy_token "$PROBE-api" "$API_SA")
  control=$(kubectl "${OPS[@]}" -n "$NS" create token "$API_SA" --audience iverson-authentik-orchestrator \
    --duration 10m --bound-object-kind Pod --bound-object-name "$API_POD" --bound-object-uid "$API_UID")
  expect_exchange L1 "orchestrator pin, legacy Secret token for $API_SA" "400 invalid_grant" \
    "$legacy" iverson-admin-orchestrator goauthentik.io/api
  expect_exchange L1c "orchestrator pin, TokenRequest token bound to $API_POD" "200 -" \
    "$control" iverson-admin-orchestrator goauthentik.io/api
  automation_client=$(kubectl -n "$NS" get secret "$RELEASE-authentik-admin-automation-client" \
    -o jsonpath='{.data.client-id}' | base64 -d)
  legacy=$(legacy_token "$PROBE-automation" "$AUTOMATION_SA")
  control=$(kubectl "${OPS[@]}" -n "$NS" create token "$AUTOMATION_SA" \
    --audience iverson-authentik-admin-automation --duration 10m)
  expect_exchange L2 "admin-automation pin, legacy Secret token for $AUTOMATION_SA" "400 invalid_grant" \
    "$legacy" "$automation_client" "admin schema_admin tenant_id_admin"
  expect_exchange L2c "admin-automation pin, TokenRequest token for $AUTOMATION_SA" "200 -" \
    "$control" "$automation_client" "admin schema_admin tenant_id_admin"
  unset legacy control
else
  report FAIL L1 "cannot port-forward to svc/$RELEASE-authentik as the operators group"
fi

echo "${0##*/}: $PASSED passed, $FAILED failed"
[ "$FAILED" -eq 0 ]

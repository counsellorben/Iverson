#!/usr/bin/env bash
set -uo pipefail

# CSR round 12 (docs/specs/2026-10-19-csr-round12-platform-design.md §2.5, §2.7 and §6): live probes
# of Authentik's public listener (:9080, reached through the edge) and its in-cluster route (:9000,
# reached through kubectl port-forward) on a running release.
#
#   A1  the public listener refuses a superuser at login, before any password is asked;
#   A2  the in-cluster route takes the same login on to the password stage;
#   A3  the public listener refuses a superuser's recovery link;
#   A4  the in-cluster route opens the same kind of link at its first prompt;
#   A5  the public listener forwards the allowed flow slugs and answers 404 for the refused ones,
#       initial-setup and /if/user/ among them;
#   A6  the public listener redirects / to the console;
#   A7  on the in-cluster route, /setup and the initial-setup executor show no prompt;
#   A8  a failed login through the public listener is recorded under the client address the edge
#       reported (the X-Forwarded-For entry the tls-proxy selects), not a loopback address;
#   A9  a failed login through a port-forward is recorded under ::ffff:127.0.0.1.
#
# Run it as the operators group: it execs into the Authentik worker and port-forwards to Authentik
# (docs/runbooks/csr11-identity-plane.md). It creates and removes its own short-lived material: a
# recovery link for iverson-breakglass (minting one replaces any link outstanding for that account),
# an API token of that account that expires after 10 minutes, and the reputation rows its two failed
# logins create, which that token reads and deletes. The two failed logins use fresh identifiers that
# name no account. It prints outcomes only, never a token, a link or a password.
#
# Usage: authentik-probes.sh [--release NAME] [--namespace NS]   (both default to iverson)
# Environment (the defaults fit the kind profile):
#   OPERATOR_AS  kubectl impersonation flags that add the operators group (default:
#                --as=kubernetes-admin --as-group=kubeadm:cluster-admins --as-group=iverson-operators)
#   EDGE_BASE    the edge as this host reaches it (default http://127.0.0.1:8080)
#   EDGE_HOST    Authentik's public host name on the edge (default authentik.iverson.local)
#   CONSOLE_URL  where the public listener's / must redirect (default http://iverson.local/admin/)
#   EDGE_HOPS    the release's global.trustedProxies.hops (default 1)
# Exit status: 0 when every check passes, 1 when any fails, 2 on a usage error.

OPERATOR_AS=${OPERATOR_AS:---as=kubernetes-admin --as-group=kubeadm:cluster-admins --as-group=iverson-operators}
EDGE_BASE=${EDGE_BASE:-http://127.0.0.1:8080}
EDGE_HOST=${EDGE_HOST:-authentik.iverson.local}
CONSOLE_URL=${CONSOLE_URL:-http://iverson.local/admin/}
EDGE_HOPS=${EDGE_HOPS:-1}
RELEASE=iverson
NS=iverson
PASSED=0
FAILED=0

usage() { echo "usage: ${0##*/} [--release NAME] [--namespace NS]" >&2; }

report() {  # <PASS|FAIL> <id> <text>
  if [ "$1" = PASS ]; then PASSED=$((PASSED + 1)); else FAILED=$((FAILED + 1)); fi
  printf '%s %s %s\n' "$1" "$2" "$3"
}

result() {  # <id> <exit status of the check> <text>
  if [ "$2" -eq 0 ]; then report PASS "$1" "$3"; else report FAIL "$1" "$3"; fi
}

# The client address the edge reported: the X-Forwarded-For entry <hops> positions from the right,
# which the tls-proxy forwards to Authentik when the edge is one of global.trustedProxies.
edge_client_from_xff() {  # <X-Forwarded-For value> <hops>
  local IFS=, entries=() i
  read -r -a entries <<< "$1"
  i=$(( ${#entries[@]} - $2 ))
  [ "${#entries[@]}" -gt 0 ] && [ "$i" -ge 0 ] || return 1
  printf '%s\n' "${entries[$i]// /}"
}

# The X-Forwarded-For field (the last quoted field of nginx's "main" log format) of the last
# access-log line on stdin that carries <marker>, which the probe sends as its User-Agent.
xff_for_marker() {  # <marker>
  command grep -F -- "$1" | tail -n 1 | sed -n 's/.*"\([^"]*\)"$/\1/p'
}

# Authentik stores an address taken from a trusted X-Forwarded-For as sent, and an untrusted IPv4
# peer of its [::]:9000 listener in IPv4-mapped form.
ip_is() { [ "$1" = "$2" ] || [ "$1" = "::ffff:$2" ]; }

is_loopback() {
  case "$1" in
    127.*|::1|::ffff:127.*) return 0 ;;
    *) return 1 ;;
  esac
}

# A6: the redirect target names the console, with or without a trailing slash.
is_console() { [ -n "$1" ] && [ "${1%/}" = "${CONSOLE_URL%/}" ]; }

# A7: /setup shows no prompt when it fails (500, the denied initial-setup flow on a fresh install) or
# redirects anywhere but the initial-setup flow (an install whose setup is already done).
setup_shows_no_prompt() {  # <status> <location>
  [ "$1" = 500 ] || { [ "$1" = 302 ] && [[ "$2" != *initial-setup* ]]; }
}

# Drives /api/v3/flows/executor/<slug>/ the way the browser does, with one cookie jar, and prints the
# final challenge's component and error message. Each step is ident:<uid> or password:<value> and is
# submitted only while the flow shows that stage.
FLOW_PY='
import http.cookiejar, json, sys, urllib.error, urllib.parse, urllib.request
base, host, slug, query, agent = sys.argv[1:6]
class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None
opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
url = f"{base}/api/v3/flows/executor/{slug}/?" + urllib.parse.urlencode({"query": query})
def call(method, body=None):
    headers = {"Accept": "application/json", "User-Agent": agent}
    if host != "-":
        headers["Host"] = host
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    try:
        response = opener.open(urllib.request.Request(url, data=data, headers=headers, method=method), timeout=30)
        return response.status, response.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()
def challenge():
    status, raw = call("GET")
    try:
        return json.loads(raw)
    except ValueError:
        return {"component": f"http-{status}"}
stage = {"ident": ("ak-stage-identification", "uid_field"), "password": ("ak-stage-password", "password")}
current = challenge()
for step in sys.argv[6:]:
    kind, _, value = step.partition(":")
    component, field = stage[kind]
    if current.get("component") != component:
        break
    call("POST", {"component": component, field: value})
    current = challenge()
print(current.get("component", ""), (current.get("error_message") or "").replace("\n", " "), sep="\t")
'
flow() { python3 -c "$FLOW_PY" "$@"; }

# Lists or deletes the reputation rows of one identifier through the API, with the token in
# PROBE_API_TOKEN. Prints the rows' addresses, or how many rows it deleted.
REPUTATION_PY='
import json, os, sys, urllib.parse, urllib.request
base, action, identifier = sys.argv[1:4]
def call(method, path):
    request = urllib.request.Request(base + path, method=method, headers={
        "Authorization": "Bearer " + os.environ["PROBE_API_TOKEN"], "Accept": "application/json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        body = response.read()
        return json.loads(body) if body else None
rows = call("GET", "/api/v3/policies/reputation/scores/?" + urllib.parse.urlencode({"identifier": identifier}))["results"]
if action == "ips":
    print(" ".join(sorted(row["ip"] for row in rows)))
else:
    for row in rows:
        call("DELETE", "/api/v3/policies/reputation/scores/" + str(row["pk"]) + "/")
    print(len(rows))
'

http_status() {  # <base> <Host header or -> <path>: prints "<status> <Location>"
  local host=()
  [ "$2" != - ] && host=(-H "Host: $2")
  curl -s -o /dev/null -m 30 "${host[@]}" -w '%{http_code} %header{location}' "$1$3"
}

ak_shell() {  # <python>: runs it in the Authentik worker as the operators group
  kubectl "${OPS[@]}" -n "$NS" exec "deploy/$RELEASE-authentik-worker" -c worker -- ak shell -c "$1" 2>/dev/null
}

RECOVERY_LINK_PY='
from django.test import RequestFactory
from rest_framework.request import Request
from authentik.brands.utils import get_brand_for_request
from authentik.core.api.users import UserViewSet
from authentik.core.models import User
req = RequestFactory().get("/", HTTP_HOST="localhost:9000")
req.brand = get_brand_for_request(req)
view = UserViewSet()
view.request = Request(req)
view.get_object = lambda: User.objects.get(username="iverson-breakglass")
link, _ = view._create_recovery_link("minutes=5")
print("PROBE-LINK " + link)
'

recovery_link_query() {  # prints flow_token=<key> of a fresh link for iverson-breakglass
  ak_shell "$RECOVERY_LINK_PY" | sed -n 's/^PROBE-LINK .*[?&]\(flow_token=[^&]*\).*/\1/p' | tail -n 1
}

start_port_forward() {  # forwards a free local port to svc/<release>-authentik:9000; sets PF_PID, PF_PORT
  kubectl "${OPS[@]}" -n "$NS" port-forward "svc/$RELEASE-authentik" :9000 > "$WORK/port-forward.log" 2>&1 &
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
  if [ -n "${PROBE_API_TOKEN:-}" ] && [ -n "${PF_PORT:-}" ]; then
    local id
    for id in "${IDENTIFIERS[@]}"; do
      python3 -c "$REPUTATION_PY" "http://127.0.0.1:$PF_PORT" delete "$id" > /dev/null 2>&1
    done
  fi
  ak_shell "
from authentik.core.models import Token, User
from authentik.flows.models import FlowToken
Token.objects.filter(identifier='$TOKEN_ID').delete()
FlowToken.objects.filter(identifier=User.objects.get(username='iverson-breakglass').uid + '-password-reset').delete()
" > /dev/null
  [ -n "${PF_PID:-}" ] && kill "$PF_PID" 2>/dev/null
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
NONCE=$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')
WORK=$(mktemp -d)
TOKEN_ID="authentik-probes-$NONCE"
AGENT="authentik-probes/$NONCE"
IDENTIFIERS=("probe-edge-$NONCE" "probe-forward-$NONCE")
PF_PID=""
PF_PORT=""
PROBE_API_TOKEN=""
trap cleanup EXIT

if ! start_port_forward; then
  echo "cannot port-forward to svc/$RELEASE-authentik as the operators group:" >&2
  head -n 3 "$WORK/port-forward.log" >&2
  exit 1
fi
IN="http://127.0.0.1:$PF_PORT"
PROBE_API_TOKEN=$(ak_shell "
from datetime import timedelta
from django.utils.timezone import now
from authentik.core.models import Token, TokenIntents, User
token = Token.objects.create(identifier='$TOKEN_ID', user=User.objects.get(username='iverson-breakglass'),
    intent=TokenIntents.INTENT_API, expiring=True, expires=now() + timedelta(minutes=10),
    description='authentik-probes.sh; deleted when the probe exits')
print('PROBE-TOKEN ' + token.key)
" | sed -n 's/^PROBE-TOKEN //p' | tail -n 1)
export PROBE_API_TOKEN
if [ -z "$PROBE_API_TOKEN" ]; then
  echo "cannot create the probe's API token through ak shell in deploy/$RELEASE-authentik-worker" >&2
  exit 1
fi

# A1, A2: a superuser at login.
out=$(flow "$EDGE_BASE" "$EDGE_HOST" default-authentication-flow "" "$AGENT" ident:iverson-breakglass)
[ "${out%%$'\t'*}" = ak-stage-access-denied ]; result A1 $? "public listener, superuser login: ${out//$'\t'/ - }"
out=$(flow "$IN" - default-authentication-flow "" "$AGENT" ident:iverson-breakglass)
[ "${out%%$'\t'*}" = ak-stage-password ]; result A2 $? "in-cluster route, superuser login: ${out%%$'\t'*}"

# A3, A4: a superuser's recovery link. A link is spent when it is opened, so each route gets its own.
query=$(recovery_link_query)
out=$(flow "$EDGE_BASE" "$EDGE_HOST" iverson-recovery "$query" "$AGENT")
[ -n "$query" ] && [ "${out%%$'\t'*}" = ak-stage-access-denied ]
result A3 $? "public listener, superuser recovery link: ${out//$'\t'/ - }"
query=$(recovery_link_query)
out=$(flow "$IN" - iverson-recovery "$query" "$AGENT")
[ -n "$query" ] && [ "${out%%$'\t'*}" = ak-stage-prompt ]
result A4 $? "in-cluster route, superuser recovery link: ${out%%$'\t'*}"

# A5: the public listener's flow allow-list.
allowed=(default-authentication-flow iverson-recovery default-provider-authorization-implicit-consent
         default-invalidation-flow default-provider-invalidation-flow)
refused=(initial-setup default-provider-authorization-explicit-consent default-user-settings-flow
         default-password-change default-authenticator-totp-setup default-source-authentication
         default-source-enrollment)
bad=""
for slug in "${allowed[@]}"; do
  for path in "/if/flow/$slug/" "/api/v3/flows/executor/$slug/"; do
    s=$(http_status "$EDGE_BASE" "$EDGE_HOST" "$path"); s=${s%% *}
    case "$s" in 404|000|5*) bad="$bad $path=$s" ;; esac
  done
done
for slug in "${refused[@]}"; do
  for path in "/if/flow/$slug/" "/api/v3/flows/executor/$slug/"; do
    s=$(http_status "$EDGE_BASE" "$EDGE_HOST" "$path"); s=${s%% *}
    [ "$s" = 404 ] || bad="$bad $path=$s"
  done
done
for path in /if/user/ /if/admin/ /api/v3/core/users/me/ /setup; do
  s=$(http_status "$EDGE_BASE" "$EDGE_HOST" "$path"); s=${s%% *}
  [ "$s" = 404 ] || bad="$bad $path=$s"
done
[ -z "$bad" ]
result A5 $? "public listener allow-list (allowed slugs forwarded; initial-setup, /if/user/ and the rest 404)${bad:+, unexpected:$bad}"

# A6: / redirects to the console.
s=$(http_status "$EDGE_BASE" "$EDGE_HOST" /)
[ "${s%% *}" = 302 ] && is_console "${s#* }"; result A6 $? "public listener /: $s"

# A7: no setup prompt on the in-cluster route.
s=$(http_status "$IN" - /setup)
out=$(flow "$IN" - initial-setup "" "$AGENT")
setup_shows_no_prompt "${s%% *}" "${s#* }" && [ "${out%%$'\t'*}" != ak-stage-prompt ]
result A7 $? "in-cluster route, /setup: $s; initial-setup executor: ${out%%$'\t'*}"

# A8: a failed login through the public listener.
flow "$EDGE_BASE" "$EDGE_HOST" default-authentication-flow "" "$AGENT" \
  "ident:${IDENTIFIERS[0]}" "password:wrong-$NONCE" > /dev/null
xff=$(kubectl -n "$NS" logs -l "app=$RELEASE-authentik-server" -c tls-proxy --tail=-1 --since=10m 2>/dev/null \
  | xff_for_marker "$AGENT")
edge=$(edge_client_from_xff "$xff" "$EDGE_HOPS")
ip=$(python3 -c "$REPUTATION_PY" "$IN" ips "${IDENTIFIERS[0]}")
[ -n "$edge" ] && ip_is "$ip" "$edge" && ! is_loopback "$ip"
result A8 $? "failed login on the public listener recorded as '$ip'; the edge reported '${edge:-none}' (X-Forwarded-For '$xff')"

# A9: a failed login through the port-forward.
flow "$IN" - default-authentication-flow "" "$AGENT" "ident:${IDENTIFIERS[1]}" "password:wrong-$NONCE" > /dev/null
ip=$(python3 -c "$REPUTATION_PY" "$IN" ips "${IDENTIFIERS[1]}")
[ "$ip" = ::ffff:127.0.0.1 ]; result A9 $? "failed login through a port-forward recorded as '$ip'"

echo "${0##*/}: $PASSED passed, $FAILED failed"
[ "$FAILED" -eq 0 ]

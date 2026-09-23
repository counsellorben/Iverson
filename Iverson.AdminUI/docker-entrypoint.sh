#!/bin/sh
#
# Renders the two per-environment artefacts the console needs, at CONTAINER START.
#
#   /usr/share/nginx/html/config.js               — the runtime config the SPA reads
#   /etc/nginx/conf.d/admin-ui-security-headers.inc — the CSP and its companions
#
# Neither can be baked into the image. The OIDC client id, the Authentik authority
# and the admin-api base URL are all per-environment values that only exist as
# environment variables once the container is scheduled, and the CSP has to NAME
# two of them: the console is served from its own hostname while the API lives on a
# dedicated `admin-api` one, so `connect-src 'self'` — which was sufficient while the
# two shared an origin — now blocks every call the console makes, and omitting the
# Authentik origin blocks the OIDC discovery fetch and the token exchange outright,
# which means login cannot complete. A `connect-src` written into nginx.conf at build
# time would therefore carry either an unresolved placeholder or a wrong origin.
#
# Run by the base image's /docker-entrypoint.sh, which `set -e`s and aborts before
# exec'ing nginx if this script exits non-zero. That is deliberate and load-bearing:
# a rejected value must stop the container, not serve a broken console.
set -eu

CONFIG_TEMPLATE=/usr/share/nginx/html/config.js.template
CONFIG_OUTPUT=/usr/share/nginx/html/config.js
HEADERS_OUTPUT=/etc/nginx/conf.d/admin-ui-security-headers.inc

fail() {
    echo "admin-ui entrypoint: $1" >&2
    exit 78   # EX_CONFIG
}

# Rejects any value that is empty or contains a character outside [A-Za-z0-9:/._-].
#
# This is an INJECTION control, not input hygiene. `envsubst` has no notion of
# JavaScript syntax: config.js.template writes each value between double quotes, so a
# value containing a double quote closes its own string literal and everything after
# it becomes code — code that runs on every page load of a page holding the
# operator's session and access token. The values are URLs and a client id, none of
# which need a character outside this set, so the narrow allow-list costs nothing.
#
# Excluding `$` is load-bearing too, and for a SECOND consumer: these same values are
# interpolated into an nginx config below, where `$foo` is a variable reference. A value
# carrying a `$` would either resolve to some unrelated nginx variable inside the CSP or
# fail the config parse and stop the container. Do not "simplify" `$` back into the
# character class.
#
# Written with `tr` rather than `grep -Eq '^[A-Za-z0-9:/._-]+$'` because grep is
# LINE-oriented: for a value whose first line is a clean URL and whose second line
# carries the payload, grep -q finds a matching line and returns 0, and the double
# quote sails through. `tr -d` deletes every permitted character from the WHOLE
# value; whatever is left is a character the expression would not have permitted, a
# newline included. The trailing `printf X` is what stops command substitution from
# swallowing a leftover trailing newline and turning that case into a pass.
validate() {
    _name=$1
    _value=$2
    [ -n "$_value" ] || fail "$_name is not set (or is empty); refusing to render config.js"
    _leftover=$(printf '%s' "$_value" | tr -d 'A-Za-z0-9:/._-'; printf 'X')
    [ "$_leftover" = "X" ] || fail "$_name contains characters outside [A-Za-z0-9:/._-]; refusing to render config.js"
}

# Reduces an absolute URL to the CSP source expression for its origin: scheme, host
# and port, with any path discarded. A CSP source that carries a path is PATH-MATCHED,
# so passing the authority URL through whole ("http://idp/application/o/iverson-api/")
# would authorise only that subtree — and oidc-client-ts fetches
# ".../.well-known/openid-configuration" and posts to ".../token", which are not under
# it. Failing loudly when there is no scheme beats emitting a policy that silently
# omits an origin and surfaces days later as "login just stops".
origin_of() {
    _name=$1
    _url=$2
    _origin=$(printf '%s' "$_url" | sed -n -E 's#^([A-Za-z][A-Za-z0-9+.-]*://[^/]+).*$#\1#p')
    [ -n "$_origin" ] || fail "$_name ('$_url') is not an absolute URL; cannot derive its origin for the Content-Security-Policy"
    printf '%s' "$_origin"
}

validate OIDC_CLIENT_ID "${OIDC_CLIENT_ID-}"
validate OIDC_AUTHORITY "${OIDC_AUTHORITY-}"
validate API_BASE_URL "${API_BASE_URL-}"

ADMIN_API_ORIGIN=$(origin_of API_BASE_URL "$API_BASE_URL")
export ADMIN_API_ORIGIN

envsubst '${OIDC_CLIENT_ID} ${OIDC_AUTHORITY} ${API_BASE_URL}' \
  < /usr/share/nginx/html/config.js.template \
  > /usr/share/nginx/html/config.js

# nginx.conf (unlike config.js.template) is not a template file rendered to
# a separate destination — it IS the served config, so it must be rewritten
# in place through a temp file: `envsubst < f > f` truncates f the instant
# the shell opens it for writing, before envsubst reads a byte, which would
# leave a zero-length default.conf and a pod that never binds :8080.
#
# HSTS is emitted only when the deployment terminates (or is fronted by
# something that terminates) TLS as https; advertising it over a plain-http
# origin would tell every browser to force https on this host regardless.
if [ "$EXTERNAL_SCHEME" = "https" ]; then
  export HSTS_LINE='add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;'
else
  export HSTS_LINE=''
fi

# SHELL-FORMAT restricts substitution to exactly these four names. A bare
# envsubst would substitute every $NAME it finds, including the $uri
# references in nginx.conf's `try_files $uri $uri/ /index.html;` line,
# replacing each with an empty string and turning it into
# `try_files  / /index.html;` — a 404 for every client-side route (starting
# with /callback, breaking OIDC login) instead of the SPA fallback.
envsubst '${ADMIN_API_ORIGIN} ${OIDC_ORIGIN} ${EXTERNAL_SCHEME} ${HSTS_LINE}' \
  < /etc/nginx/conf.d/default.conf > /etc/nginx/conf.d/default.conf.tmp \
  && mv /etc/nginx/conf.d/default.conf.tmp /etc/nginx/conf.d/default.conf

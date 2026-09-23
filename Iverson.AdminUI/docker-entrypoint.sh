#!/bin/sh
#
# Renders the two per-environment artefacts the console needs, at CONTAINER START.
#
#   /usr/share/nginx/html/config.js  — the runtime config the SPA reads (rendered from config.js.template)
#   /etc/nginx/conf.d/default.conf   — the CSP and its companions, rendered in place
#
# Neither can be baked into the image. The OIDC client id, the Authentik authority
# and the admin-api base URL are all per-environment values that only exist as
# environment variables once the container is scheduled, and the CSP has to NAME
# two origins: the console is served from its own hostname while the API lives on a
# dedicated `admin-api` one, so `connect-src 'self'` — which was sufficient while the
# two shared an origin — now blocks every call the console makes, and omitting the
# Authentik origin blocks the OIDC discovery fetch and the token exchange outright,
# which means login cannot complete. A `connect-src` written into nginx.conf at build
# time would therefore carry either an unresolved placeholder or a wrong origin.
#
# The two origins do NOT arrive the same way. ADMIN_API_ORIGIN is derived HERE, by
# origin_of, from API_BASE_URL after validate has checked it. The Authentik origin is
# OIDC_ORIGIN, passed in separately (the admin-ui chart sets it) and substituted as given:
# this script neither derives it from OIDC_AUTHORITY nor validates it.
#
# Run by the base image's /docker-entrypoint.sh, which `set -e`s and aborts before
# exec'ing nginx if this script exits non-zero. That is deliberate and load-bearing:
# a rejected value must stop the container, not serve a broken console.
set -eu

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
# Excluding `$` is load-bearing too, and for a SECOND consumer: the origin derived from
# API_BASE_URL is interpolated into an nginx config below, where `$foo` is a variable
# reference. A value carrying a `$` would either resolve to some unrelated nginx variable
# inside the CSP or fail the config parse and stop the container. Do not "simplify" `$`
# back into the character class. (OIDC_ORIGIN reaches that config too, but is not
# validated here — see the header.)
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
# so passing API_BASE_URL through whole — say, if it carried a path like
# ".../admin/console" — would authorise only that one subtree, and the console's
# other calls on the same origin ("/health", ".../admin/console/schema", ...)
# would not be under it. Failing loudly when there is no scheme beats emitting a
# policy that silently omits the origin and surfaces days later as every console
# widget failing to load, for a reason that has nothing to do with the API itself.
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

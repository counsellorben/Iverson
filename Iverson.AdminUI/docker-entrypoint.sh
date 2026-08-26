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

envsubst '${OIDC_CLIENT_ID} ${OIDC_AUTHORITY} ${API_BASE_URL}' \
  < "$CONFIG_TEMPLATE" \
  > "$CONFIG_OUTPUT"

ADMIN_API_ORIGIN=$(origin_of API_BASE_URL "$API_BASE_URL")
OIDC_ORIGIN=$(origin_of OIDC_AUTHORITY "$OIDC_AUTHORITY")
export ADMIN_API_ORIGIN OIDC_ORIGIN

# Included by name from nginx.conf's server block. The `.inc` extension is not an
# accident: the base image's /etc/nginx/nginx.conf globs `/etc/nginx/conf.d/*.conf`
# into the http block, so a `.conf` name here would be pulled in twice, once at http
# level and once where we actually want it.
#
# `always` on every header: without it nginx omits add_header on error responses, so
# the 404 that a client-side route resolves through — and any 4xx/5xx — would come
# back with no CSP at all.
#
# No Strict-Transport-Security. This listener is plaintext HTTP behind a
# TLS-terminating ingress; HSTS belongs on the hop the browser actually speaks TLS to.
envsubst '${ADMIN_API_ORIGIN} ${OIDC_ORIGIN}' > "$HEADERS_OUTPUT" <<'TEMPLATE'
# Rendered at container start by /docker-entrypoint.d/40-admin-ui-config.sh.
# Do not edit: this file is overwritten on every start.
#
# default-src 'self' covers script-src, img-src and font-src — the bundle, the
# self-hosted Fraunces woff2 files and every image are served from this origin, and
# nothing in the build emits a data: URI, a blob:, a Worker or a `new Function`.
# Only the four directives that default-src cannot express are spelled out:
#
#   style-src   MUI/Emotion inject their styles as inline <style> elements at
#               runtime, so 'unsafe-inline' is required here or the console renders
#               completely unstyled.
#   connect-src the two cross-origin hosts the console talks to: admin-api (every
#               widget's fetch, plus the OTLP trace export) and Authentik (discovery,
#               token exchange, refresh and revocation).
#   base-uri    does NOT fall back to default-src; without it an injected <base>
#               could re-point every relative asset URL.
#   frame-ancestors  does NOT fall back to default-src; 'none' is the clickjacking
#               control for a console that is never legitimately framed.
add_header Content-Security-Policy "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; style-src 'self' 'unsafe-inline'; connect-src 'self' ${ADMIN_API_ORIGIN} ${OIDC_ORIGIN}" always;
add_header X-Content-Type-Options "nosniff" always;
# no-referrer rather than the usual strict-origin-when-cross-origin: the OIDC redirect
# lands the browser on /callback?code=..., and a Referer sent from that document would
# carry the authorization code off-origin.
add_header Referrer-Policy "no-referrer" always;
TEMPLATE

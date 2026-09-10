#!/bin/sh
set -eu
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

# SHELL-FORMAT restricts substitution to exactly these three names. A bare
# envsubst would substitute every $NAME it finds, including the $uri
# references in nginx.conf's `try_files $uri $uri/ /index.html;` line,
# replacing each with an empty string and turning it into
# `try_files  / /index.html;` — a 404 for every client-side route (starting
# with /callback, breaking OIDC login) instead of the SPA fallback.
envsubst '${OIDC_ORIGIN} ${EXTERNAL_SCHEME} ${HSTS_LINE}' \
  < /etc/nginx/conf.d/default.conf > /etc/nginx/conf.d/default.conf.tmp \
  && mv /etc/nginx/conf.d/default.conf.tmp /etc/nginx/conf.d/default.conf

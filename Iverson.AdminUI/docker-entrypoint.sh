#!/bin/sh
set -eu
envsubst '${OIDC_CLIENT_ID} ${OIDC_AUTHORITY} ${API_BASE_URL}' \
  < /usr/share/nginx/html/config.js.template \
  > /usr/share/nginx/html/config.js

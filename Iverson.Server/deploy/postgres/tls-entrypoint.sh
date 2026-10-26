#!/bin/sh
# CSR round 12 §4.6: compose's Postgres serves TLS from the compose certificate, which is
# bind-mounted read-only at /etc/pgtls-src. Postgres refuses a key file with group or world access
# and cannot read one owned by another user, so both files are copied into a postgres-owned
# directory, the key at 0600, before the image's own entrypoint runs. This runs as root, as the
# image's entrypoint does until it drops to postgres; the server flags reach the temporary
# init-time server too.
set -eu
install -d -o postgres -g postgres -m 700 /etc/pgtls
install -o postgres -g postgres -m 600 /etc/pgtls-src/tls.key /etc/pgtls/tls.key
install -o postgres -g postgres -m 644 /etc/pgtls-src/tls.crt /etc/pgtls/tls.crt
exec docker-entrypoint.sh "$@" -c ssl=on -c ssl_cert_file=/etc/pgtls/tls.crt -c ssl_key_file=/etc/pgtls/tls.key

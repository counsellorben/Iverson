#!/bin/bash
# CSR round 12 §4.6: compose's StarRocks FE serves TLS on its MySQL port, 9030, as the chart's FE
# does (charts/starrocks/templates/starrockscluster.yaml builds the same keystore in an init
# container). At every start this builds a PKCS12 keystore from the compose certificate into a
# tmpfs, makes sure fe.conf names it, and hands over to the image's own entrypoint. fe.conf lives in
# the starrocks_data volume, so each setting is appended once, the way the image's entrypoint
# appends priority_networks. ssl_force_secure_transport stays unset: compose does not force TLS,
# so the image's own BE registration and healthcheck, which call plain mysql, keep working.
set -euo pipefail
umask 077
src=/etc/iverson/starrocks-tls
keystore=/run/iverson-fe-tls/fe.p12
# Not a secret: the keystore holds the same key as the mounted file it is built from.
password=iverson-fe-keystore
openssl pkcs12 -export -name fe -in "$src/tls.crt" -inkey "$src/tls.key" -certfile "$src/ca.crt" \
    -passout "pass:$password" -out "$keystore"
conf="$SR_HOME/fe/conf/fe.conf"
for setting in "ssl_keystore_location = $keystore" "ssl_keystore_password = $password" "ssl_key_password = $password"; do
    key="${setting%% =*}"
    if ! grep -aEq "^[[:space:]]*${key}[[:space:]]*=" "$conf"; then
        if [ -s "$conf" ] && [ "$(tail -c 1 "$conf" | wc -l)" -eq 0 ]; then echo >> "$conf"; fi
        echo "$setting" >> "$conf"
    fi
done
exec ./entrypoint.sh

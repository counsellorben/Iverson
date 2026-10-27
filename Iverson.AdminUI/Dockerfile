# Pinned by digest (CSR round-3 finding #13): a floating tag lets the base image
# change under this build with no review. Manifest-list (OCI image index) digest
# for docker.io/library/node:22-alpine as of 2026-10-06, confirmed via a direct
# `GET /v2/library/node/manifests/22-alpine` against registry-1.docker.io
# (Docker-Content-Digest response header, requested with an
# application/vnd.oci.image.index.v1+json Accept header) — NOT the same as a local
# `docker inspect --format='{{index .RepoDigests 0}}'` after a plain pull, which
# recorded this host's own single-platform (linux/amd64) manifest digest instead of
# the multi-arch index; pinning that value would have silently broken any other
# platform's build. Bump this deliberately (re-resolve both the tag and its current
# digest) when a newer 22-alpine patch is wanted; the tag is kept alongside the
# digest for human readability only — the digest is what's actually resolved.
# Node 22: package.json requires >=22.22.0 and Node 20 is end-of-life (CSR round-10 #19).
FROM node:22-alpine@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS build
WORKDIR /src
COPY Iverson.AdminUI/package.json Iverson.AdminUI/package-lock.json ./
RUN npm ci
COPY Iverson.AdminUI/ .
COPY Iverson.Clients/Common/Proto ../Iverson.Clients/Common/Proto
RUN npm run build

# Pinned by digest (CSR round-3 finding #13) — same rationale and same caveat about
# platform-specific vs. manifest-list digests as the node stage above. Manifest-list
# digest for docker.io/nginxinc/nginx-unprivileged:1.31.7-alpine as of 2026-10-27 (the 2026-10-19
# rebuild, which carries libexpat 2.9.0-r0 and libx11 1.8.14-r0, the fixes for CVE-2026-77214 and
# CVE-2026-88806).
FROM docker.io/nginxinc/nginx-unprivileged:1.31.7-alpine@sha256:b5047b9b76845eac124358373c6852377c52e4b05834a47647e955dd91cb8f1e AS runtime
# The base image already sets USER 101 as its own default, but /usr/share/nginx/html
# is baked into that base image as root:root 755 — COPY (even with --chown on the
# copied files) never retroactively chowns a pre-existing destination directory
# itself, so uid 101 still can't create new files there (verified empirically: a
# plain `COPY --from=build /src/dist /usr/share/nginx/html` followed by `USER 101`
# fails with "Permission denied" the moment docker-entrypoint.d's envsubst step
# tries to write config.js at container startup — crashing the container). Switch
# to root explicitly to fix the directory's own ownership before dropping back down.
USER root
# That base still ships brotli-libs 1.2.0-r1 and curl/libcurl 8.22.0-r0 (CVE-2026-111497, CVE-2026-92392);
# Alpine 3.24 has the fixed 1.2.1-r0 and 8.23.0-r0, so take just those three until a base rebuild
# carries them (the image-scan workflow's first-party job fails on any fixed HIGH/CRITICAL). Drop this
# line when the pinned base already has them.
RUN apk upgrade --no-cache brotli-libs curl libcurl
COPY --from=build --chown=101:101 /src/dist /usr/share/nginx/html
RUN chown 101:101 /usr/share/nginx/html
# Replaces the base image's stock default.conf, which has no SPA fallback:
# unmatched paths (e.g. /callback, /performance) 404 instead of resolving to
# index.html and letting React Router take over client-side.
COPY --chown=101:101 Iverson.AdminUI/nginx.conf /etc/nginx/conf.d/default.conf
COPY Iverson.AdminUI/docker-entrypoint.sh /docker-entrypoint.d/40-admin-ui-config.sh
RUN chmod +x /docker-entrypoint.d/40-admin-ui-config.sh
USER 101
EXPOSE 8080

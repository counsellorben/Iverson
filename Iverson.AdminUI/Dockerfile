# Pinned by digest (CSR round-3 finding #13): a floating tag lets the base image
# change under this build with no review. Manifest-list (OCI image index) digest
# for docker.io/library/node:20-alpine as of 2026-09-13, confirmed via a direct
# `GET /v2/library/node/manifests/20-alpine` against registry-1.docker.io
# (Docker-Content-Digest response header, requested with an
# application/vnd.oci.image.index.v1+json Accept header) — NOT the same as a local
# `docker inspect --format='{{index .RepoDigests 0}}'` after a plain pull, which
# recorded this host's own single-platform (linux/amd64) manifest digest instead of
# the multi-arch index; pinning that value would have silently broken any other
# platform's build. Bump this deliberately (re-resolve both the tag and its current
# digest) when a newer 20-alpine patch is wanted; the tag is kept alongside the
# digest for human readability only — the digest is what's actually resolved.
FROM node:20-alpine@sha256:fb4cd12c85ee03686f6af5362a0b0d56d50c58a04632e6c0fb8363f609372293 AS build
WORKDIR /src
COPY Iverson.AdminUI/package.json Iverson.AdminUI/package-lock.json ./
RUN npm ci
COPY Iverson.AdminUI/ .
COPY Iverson.Clients/Common/Proto ../Iverson.Clients/Common/Proto
RUN npm run build

# Pinned by digest (CSR round-3 finding #13) — same rationale and same caveat about
# platform-specific vs. manifest-list digests as the node stage above. Manifest-list
# digest for docker.io/nginxinc/nginx-unprivileged:1.27-alpine as of 2026-09-13.
FROM nginxinc/nginx-unprivileged:1.27-alpine@sha256:65e3e85dbaed8ba248841d9d58a899b6197106c23cb0ff1a132b7bfe0547e4c0 AS runtime
# The base image already sets USER 101 as its own default, but /usr/share/nginx/html
# is baked into that base image as root:root 755 — COPY (even with --chown on the
# copied files) never retroactively chowns a pre-existing destination directory
# itself, so uid 101 still can't create new files there (verified empirically: a
# plain `COPY --from=build /src/dist /usr/share/nginx/html` followed by `USER 101`
# fails with "Permission denied" the moment docker-entrypoint.d's envsubst step
# tries to write config.js at container startup — crashing the container). Switch
# to root explicitly to fix the directory's own ownership before dropping back down.
USER root
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

# node:22, not node:20. react-router@8.3.0 declares engines.node ">=22.22.0" — the
# highest floor in the installed tree (164 packages declare engines.node; the next
# highest is vite/rolldown/@vitejs/plugin-react at "^20.19.0 || >=22.12.0", which
# already excludes the whole 21 line). node:20-alpine built only because npm treats an
# unmet engines range as a warning, not an error; the next dependency bump that turns
# it into a hard failure would break the image build with no warning beforehand.
# `engines.node` in Iverson.AdminUI/package.json records that floor so this tag and
# the tree cannot drift apart silently. node:22-alpine currently ships v22.23.2.
# The runtime stage below is a separate nginx image and is unaffected by this.
FROM node:22-alpine AS build
WORKDIR /src
COPY Iverson.AdminUI/package.json Iverson.AdminUI/package-lock.json ./
RUN npm ci
COPY Iverson.AdminUI/ .
COPY Iverson.Clients/Common/Proto ../Iverson.Clients/Common/Proto
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
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
COPY Iverson.AdminUI/nginx.conf /etc/nginx/conf.d/default.conf
COPY Iverson.AdminUI/docker-entrypoint.sh /docker-entrypoint.d/40-admin-ui-config.sh
RUN chmod +x /docker-entrypoint.d/40-admin-ui-config.sh
USER 101
EXPOSE 8080

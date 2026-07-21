FROM node:20-alpine AS build
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
COPY Iverson.AdminUI/docker-entrypoint.sh /docker-entrypoint.d/40-admin-ui-config.sh
RUN chmod +x /docker-entrypoint.d/40-admin-ui-config.sh
USER 101
EXPOSE 8080

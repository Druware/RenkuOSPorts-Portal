# syntax=docker/dockerfile:1
# Web image: nginx serving the Vue SPA (web/) and reverse-proxying the API (root Dockerfile).
# Build context: repository root (see docker-compose.yml / CI).

# ---- build stage: Vite production build of the SPA ----
FROM node:22.23.3-alpine3.24 AS build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# ---- runtime stage: nginx serving dist/ and proxying the API allow-list ----
FROM nginx:1.30.5-alpine3.24

# Replace the stock config entirely (the image's default.conf listens on :80).
RUN rm -f /etc/nginx/conf.d/*.conf
COPY nginx/nginx.conf /etc/nginx/nginx.conf
COPY nginx/snippets/ /etc/nginx/snippets/
# The server block and proxy snippet are templates: the API upstream differs between compose
# (http://api:8080) and Azure (https://<api app>.azurewebsites.net:443).
COPY nginx/templates/ /etc/nginx/templates/
COPY --from=build /web/dist/ /usr/share/nginx/html/

# Stock entrypoint, trimmed to what this image needs:
#   05-validate-api-upstream.sh  (ours) rejects bad API_UPSTREAM_* values; the container exits.
#   20-envsubst-on-templates.sh  (stock) renders /etc/nginx/templates into /tmp.
# The other stock scripts are removed: 10-listen-on-ipv6 and 30-tune-worker-processes try to
# rewrite files under /etc/nginx (impossible read-only / non-root), and 15-local-resolvers would
# export a resolver this config deliberately does not use.
RUN rm -f /docker-entrypoint.d/10-listen-on-ipv6-by-default.sh \
          /docker-entrypoint.d/15-local-resolvers.envsh \
          /docker-entrypoint.d/30-tune-worker-processes.sh
COPY --chmod=755 nginx/entrypoint/05-validate-api-upstream.sh /docker-entrypoint.d/

# Rendered output goes to /tmp (tmpfs in compose), so the root filesystem stays read-only.
# The filter restricts envsubst to API_UPSTREAM_*: nginx's own $host, $uri, ... are untouched.
# Defaults are the compose values; Azure app settings override them.
ENV NGINX_ENVSUBST_OUTPUT_DIR=/tmp \
    NGINX_ENVSUBST_FILTER=^API_UPSTREAM_ \
    API_UPSTREAM_HOST=api \
    API_UPSTREAM_PORT=8080 \
    API_UPSTREAM_SCHEME=http

# Non-root: uid 101 `nginx` from the base image. Port 8080 needs no privileges, and all
# runtime writes (rendered config, pid, temp) go to /tmp.
USER nginx
EXPOSE 8080

# The stock entrypoint only runs /docker-entrypoint.d when the command is `nginx` (or
# `nginx-debug`), so `nginx -t` (CI) renders and validates first as well.
ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["nginx", "-g", "daemon off;"]

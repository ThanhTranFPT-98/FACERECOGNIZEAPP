# Station UI for the lab: vite build served by nginx, which also proxies the API and the hubs to the
# station container. Same origin as in a real station PC, so no CORS and the auth cookie just works.
#   docker build -f deploy/ui.Dockerfile -t dsa-station-ui:lab .
FROM node:22-alpine AS build
WORKDIR /src
COPY ui/package.json ui/package-lock.json ./
RUN npm ci
COPY ui/ ./
RUN npm run build

FROM nginx:1.27-alpine
COPY deploy/ui-nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist /usr/share/nginx/html
EXPOSE 80
# 127.0.0.1, not localhost: the image listens on IPv4 only and localhost can resolve to ::1.
HEALTHCHECK --interval=15s --timeout=3s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1/ || exit 1

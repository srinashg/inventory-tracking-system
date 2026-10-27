# The web container. Build context: the frontend/ folder (docker build -t techstore-web frontend).
# Stage 1 builds the React app with Node; stage 2 is a non-root nginx that serves the result and proxies the API.

FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.31-alpine AS runtime
# Where the API lives on the Docker network, and which DNS server resolves it (Docker's built-in one). Not secrets.
ENV API_UPSTREAM=api:8080 \
    DNS_RESOLVER=127.0.0.11
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY nginx/proxy.conf nginx/security-headers.conf /etc/nginx/
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
# The base image runs as the non-root user "nginx" (uid 101) and fills in the template at startup.

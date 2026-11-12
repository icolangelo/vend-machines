FROM node:22-alpine@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS build

WORKDIR /app
COPY package.json package-lock.json .npmrc ./
RUN npm ci

COPY . .
ARG VITE_API_URL
ENV VITE_API_URL=${VITE_API_URL}
ENV VITE_IS_TEST_ENVIRONMENT=false
RUN npm run build \
  && node scripts/assert-production-bundle.mjs dist \
  && node scripts/render-nginx-config.mjs

FROM ghcr.io/nginx/nginx-unprivileged:stable-alpine@sha256:44e74ca483234093081138b6d7b6f561d55557756a2deefc3a5056148defeb99 AS runtime

COPY --from=build /app/nginx.conf.generated /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/health || exit 1

# ---- build ----
FROM node:26-alpine AS build
WORKDIR /app
ENV NODE_ENV=development

# Node 26 no longer bundles Corepack; keep the existing pnpm version.
RUN npm install --global --ignore-scripts pnpm@10.14.0
COPY package.json pnpm-lock.yaml ./
RUN --mount=type=cache,id=pnpm-store,target=/root/.local/share/pnpm/store pnpm install --frozen-lockfile
COPY . .
RUN pnpm run build   # produit /app/dist avec index.html + assets

# ---- runtime ----
FROM nginx:1.31-alpine AS runtime
# Port 8000 does not require root; keep nginx's writable files accessible.
RUN sed -i -e '/^user /d' -e 's@^pid .*;@pid /tmp/nginx.pid;@' /etc/nginx/nginx.conf \
 && chown -R nginx:nginx /var/cache/nginx
# SPA fallback vers index.html
COPY <<'NGINX' /etc/nginx/conf.d/default.conf
server {
  listen 8000;
  server_name _;
  root /usr/share/nginx/html;
  index index.html;

  location / {
    try_files $uri $uri/ /index.html;
  }
}
NGINX

COPY --from=build /app/dist/ /usr/share/nginx/html/
USER nginx
EXPOSE 8000
CMD ["nginx","-g","daemon off;"]

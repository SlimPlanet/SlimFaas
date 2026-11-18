# ---- build ----
FROM node:26.13.0-alpine3.24 AS build
WORKDIR /app
ENV NODE_ENV=development

RUN npm install --global --ignore-scripts pnpm@12.18.0
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
RUN --mount=type=cache,id=pnpm-store,target=/root/.local/share/pnpm/store pnpm install --frozen-lockfile
COPY . .
RUN pnpm run build   # produit /app/dist avec index.html + assets

# ---- runtime ----
FROM nginx:1.30.6-alpine3.24 AS runtime
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
EXPOSE 8000
CMD ["nginx","-g","daemon off;"]

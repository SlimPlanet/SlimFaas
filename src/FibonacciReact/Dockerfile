# syntax=docker/dockerfile:1.7

######## Base (pnpm & cache) ########
FROM node:22-alpine AS base
WORKDIR /app
ENV NODE_ENV=production

# Active pnpm via Corepack (évite un npm i -g pnpm)
RUN corepack enable && corepack prepare pnpm@10.14.0 --activate

# (Facultatif mais conseillé si deps natives: node-gyp, sharp, etc.)
RUN apk add --no-cache --virtual .build-deps python3 make g++

# On ne copie que les manifests pour profiter du cache
COPY package.json pnpm-lock.yaml ./

# Pré-télécharge le store PNPM (cache par lockfile)
RUN --mount=type=cache,id=pnpm-store,target=/root/.local/share/pnpm/store \
    pnpm fetch

######## Prod deps ########
FROM base AS prod-deps
RUN --mount=type=cache,id=pnpm-store,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile --prod --offline

######## Build ########
FROM base AS build
ENV NODE_ENV=development
RUN --mount=type=cache,id=pnpm-store,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile --offline
# On copie le reste du code seulement ici
COPY . .
RUN pnpm run build

######## Runtime ########
FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production

# tini (clean PID 1) — optionnel
RUN apk add --no-cache tini

# On copie juste ce qu'il faut pour run
COPY --from=prod-deps /app/node_modules ./node_modules
COPY --from=build /app/dist ./dist
COPY package.json ./

EXPOSE 8000
ENTRYPOINT ["/sbin/tini","--"]
# Remplace "node dist/index.js" si ton script start lance autre chose
CMD ["node","dist/index.js"]
# ou: CMD ["pnpm","start"]

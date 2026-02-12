# Node.js LTS (24.x) sur Alpine, utilisé uniquement pour fournir node/npm/corepack/pnpm au build
FROM --platform=$BUILDPLATFORM node:24-alpine3.22 AS node
ARG PNPM_VERSION=10.14.0
RUN corepack enable \
 && corepack prepare "pnpm@${PNPM_VERSION}" --activate

FROM --platform=$BUILDPLATFORM alpine:3.22 AS base
RUN apk update && apk upgrade
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
RUN adduser -u 1000 --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

EXPOSE 80
EXPOSE 443

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.22 AS build
RUN apk update && apk upgrade
RUN apk add --no-cache clang build-base zlib-dev
# Injecte Node.js + npm + corepack + pnpm depuis l'image officielle node
COPY --from=node /usr/local/ /usr/local/
WORKDIR /src

FROM --platform=$BUILDPLATFORM build AS publish
COPY . .
ARG RUNTIME_ID=x64

# Si ton csproj déclenche des commandes pnpm pendant publish, Node+pnpm sont maintenant dispo.
RUN dotnet publish "./SlimFaasMcp.csproj" -c Release -a "$RUNTIME_ID" -o /app/publish \
    -p:DebugType=none \
    -p:DebugSymbols=false

FROM --platform=$BUILDPLATFORM base AS final
WORKDIR /app
COPY --chown=appuser --from=publish /app/publish .
ENTRYPOINT ["./SlimFaasMcp"]

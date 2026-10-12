# Node.js LTS (24.x) sur Alpine, utilisé uniquement pour fournir node/npm/corepack/pnpm au build
FROM --platform=$TARGETPLATFORM node:24.21.0-alpine3.24 AS node
ARG PNPM_VERSION=10.14.0
RUN corepack enable \
 && corepack prepare "pnpm@${PNPM_VERSION}" --activate

FROM --platform=$TARGETPLATFORM alpine:3.24.2 AS base
RUN apk update && apk upgrade
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
RUN adduser -u 1000 --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

EXPOSE 80
EXPOSE 443

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.24 AS build
RUN apk update && apk upgrade
RUN apk add --no-cache clang build-base zlib-dev
# Injecte Node.js + npm + corepack + pnpm depuis l'image officielle node
COPY --from=node /usr/local/ /usr/local/
WORKDIR /src

FROM --platform=$TARGETPLATFORM build AS publish
# Build context: the repository root, so the shared build settings (Directory.Build.*, Directory.Packages.props, .editorconfig, eng/) apply.
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json .editorconfig ./
COPY eng/ eng/
COPY src/SlimFaasMcp/ src/SlimFaasMcp/

# Le csproj déclenche npm ci / npm run build pendant le publish ; Node+pnpm sont disponibles.
ARG RELEASE_VERSION=0.0.0-dev.0
ARG SOURCE_REVISION_ID
RUN dotnet publish "./src/SlimFaasMcp/SlimFaasMcp.csproj" -c Release -o /app/publish \
    -p:Version="$RELEASE_VERSION" \
    -p:SourceRevisionId="$SOURCE_REVISION_ID" \
    -p:RestorePackagesWithLockFile=true \
    -p:DebugType=none \
    -p:DebugSymbols=false

RUN mkdir -p /app/dependencies/frontend && find . -name packages.lock.json -exec cp --parents {} /app/dependencies/ \;
RUN cp src/SlimFaasMcp/ClientApp/package-lock.json /app/dependencies/frontend/package-lock.json

FROM --platform=$TARGETPLATFORM base AS final
COPY --from=publish /app/dependencies /usr/share/slimfaas/dependencies
WORKDIR /app
COPY --chown=appuser --from=publish /app/publish .
# A clean publish can collect static assets before the ClientApp build finishes.
COPY --chown=appuser --from=publish /src/src/SlimFaasMcp/ClientApp/dist/ ./wwwroot/
ENTRYPOINT ["./SlimFaasMcp"]

# Node.js provides node/npm for the ClientApp build.
FROM --platform=$BUILDPLATFORM node:26-alpine3.22 AS node

FROM --platform=$BUILDPLATFORM alpine:3.24 AS base
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
# Copy Node.js and npm from the official image.
COPY --from=node /usr/local/ /usr/local/
WORKDIR /src

FROM --platform=$BUILDPLATFORM build AS publish
# Build context: the repository root, so the shared build settings (Directory.Build.*, Directory.Packages.props, .editorconfig, eng/) apply.
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json .editorconfig ./
COPY eng/ eng/
COPY src/SlimFaasMcp/ src/SlimFaasMcp/
ARG RUNTIME_ID=x64

# The project runs npm ci and npm run build during publish.
RUN dotnet publish "./src/SlimFaasMcp/SlimFaasMcp.csproj" -c Release -a "$RUNTIME_ID" -o /app/publish \
    -p:DebugType=none \
    -p:DebugSymbols=false

FROM --platform=$BUILDPLATFORM base AS final
WORKDIR /app
COPY --chown=appuser --from=publish /app/publish .
# The frontend is generated after MSBuild collects static assets on a clean build.
COPY --chown=appuser --from=publish /src/src/SlimFaasMcp/ClientApp/dist ./wwwroot
ENTRYPOINT ["./SlimFaasMcp"]

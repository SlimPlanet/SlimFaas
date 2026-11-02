FROM --platform=$TARGETPLATFORM alpine:3.24.2 AS base
RUN apk update && apk upgrade
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
RUN adduser -u 1000 --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

# ---- Node stage: build the Vite/React dashboard ----
FROM --platform=$TARGETPLATFORM node:24.21.0-alpine3.24 AS clientapp
WORKDIR /clientapp
COPY src/SlimFaas/ClientApp/package.json src/SlimFaas/ClientApp/package-lock.json ./
RUN npm ci
COPY src/SlimFaas/ClientApp/ ./
RUN npm run build

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.24 AS build
RUN apk update && apk upgrade
# Install compilation tools for native AOT
RUN apk add --no-cache build-base zlib-dev musl-dev
WORKDIR /src

FROM build AS publish
COPY . .
# Copy pre-built wwwroot from the Node stage
COPY --from=clientapp /wwwroot src/SlimFaas/wwwroot
ARG TARGETARCH
# Map Docker's TARGETARCH to .NET RID for native compilation
ARG RELEASE_VERSION=0.0.0-dev.0
ARG SOURCE_REVISION_ID
RUN if [ "$TARGETARCH" = "arm64" ]; then \
        RUNTIME_ID="linux-musl-arm64"; \
        PUBLISH_AOT="false"; \
    elif [ "$TARGETARCH" = "amd64" ]; then \
        RUNTIME_ID="linux-musl-x64"; \
        PUBLISH_AOT="true"; \
    else \
        echo "Unsupported architecture: $TARGETARCH" && exit 1; \
    fi && \
    echo "Building NATIVE for architecture: $TARGETARCH, RID: $RUNTIME_ID (PublishAot=$PUBLISH_AOT)" && \
    dotnet publish "./src/SlimFaas/SlimFaas.csproj" -c Release -r "$RUNTIME_ID" -o /app/publish \
    -p:Version="$RELEASE_VERSION" \
    -p:SourceRevisionId="$SOURCE_REVISION_ID" \
    -p:RestorePackagesWithLockFile=true \
     -p:DebugType=none \
     -p:DebugSymbols=false \
     -p:PublishAot=$PUBLISH_AOT \
     -p:StripSymbols=true \
     -p:IlcMultiThreaded=false \
     -p:SkipClientAppBuild=true

RUN mkdir -p /app/dependencies && find . -name packages.lock.json -exec cp --parents {} /app/dependencies/ \;
RUN mkdir -p /app/dependencies/frontend \
    && cp src/SlimFaas/ClientApp/package-lock.json /app/dependencies/frontend/package-lock.json

FROM base AS final
COPY --from=publish /app/dependencies /usr/share/slimfaas/dependencies
WORKDIR /app
COPY --chown=appuser --from=publish /app/publish .
ENTRYPOINT ["./SlimFaas"]

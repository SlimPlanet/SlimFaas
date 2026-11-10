ARG TARGETARCH

FROM --platform=$BUILDPLATFORM node:26.13.0-alpine3.24 AS clientapp
WORKDIR /clientapp
COPY src/SlimFaasMcp/ClientApp/package.json src/SlimFaasMcp/ClientApp/package-lock.json ./
RUN npm ci --ignore-scripts
COPY src/SlimFaasMcp/ClientApp/ ./
RUN npm run build

FROM --platform=$TARGETPLATFORM alpine:3.24.4 AS base
RUN apk update && apk upgrade && apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
RUN adduser -u 1000 --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser
EXPOSE 80
EXPOSE 443

# Native AOT uses the target linker. ARM publishes trimmed managed code using
# the build host SDK, matching the SlimFaas multi-architecture image strategy.
FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-alpine3.24 AS sdk-amd64
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-alpine3.24 AS sdk-arm64
FROM sdk-${TARGETARCH} AS publish
RUN apk update && apk upgrade && apk add --no-cache build-base clang zlib-dev
WORKDIR /src
# Build context is the repository root, including shared build settings.
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json .editorconfig ./
COPY eng/ eng/
COPY src/SlimFaasMcp/ src/SlimFaasMcp/
COPY --from=clientapp /clientapp/dist/ src/SlimFaasMcp/wwwroot/
ARG TARGETARCH
RUN if [ "$TARGETARCH" = "arm64" ]; then \
        RUNTIME_ID="linux-musl-arm64"; PUBLISH_AOT="false"; \
    elif [ "$TARGETARCH" = "amd64" ]; then \
        RUNTIME_ID="linux-musl-x64"; PUBLISH_AOT="true"; \
    else \
        echo "Unsupported architecture: $TARGETARCH"; exit 1; \
    fi && \
    dotnet publish src/SlimFaasMcp/SlimFaasMcp.csproj -c Release -r "$RUNTIME_ID" -o /app/publish \
        -p:DebugType=none -p:DebugSymbols=false -p:PublishAot="$PUBLISH_AOT" \
        -p:PublishTrimmed=true -p:SelfContained=true -p:IlcMultiThreaded=false \
        -p:SkipClientAppBuild=true

FROM base AS final
WORKDIR /app
COPY --chown=appuser --from=publish /app/publish .
ENTRYPOINT ["./SlimFaasMcp"]

FROM --platform=$TARGETPLATFORM alpine:3.23 AS base
RUN apk update && apk upgrade
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
RUN adduser -u 1000 --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.23 AS build
RUN apk update && apk upgrade
# Install compilation tools for native AOT
RUN apk add --no-cache build-base zlib-dev musl-dev
WORKDIR /src

FROM build AS publish
COPY . .
ARG TARGETARCH
# Map Docker's TARGETARCH to .NET RID for native compilation
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
     -p:DebugType=none \
     -p:DebugSymbols=false \
     -p:PublishAot=$PUBLISH_AOT \
     -p:StripSymbols=true \
     -p:IlcMultiThreaded=false

FROM base AS final
WORKDIR /app
COPY --chown=appuser --from=publish /app/publish .
ENTRYPOINT ["./SlimFaas"]

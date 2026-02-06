FROM alpine:3.23 AS base
RUN apk update && apk upgrade
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
RUN adduser -u 1000 --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.23 AS build
RUN apk update && apk upgrade
RUN apk add --no-cache clang18 build-base zlib-dev lld
# Use clang for better AOT compilation and cross-compilation support
ENV CC=clang-18
ENV CXX=clang++-18
WORKDIR /src

FROM --platform=$BUILDPLATFORM build AS publish
COPY . .
ARG TARGETARCH
# Map Docker's TARGETARCH to .NET RID for native compilation
RUN if [ "$TARGETARCH" = "arm64" ]; then \
        RUNTIME_ID="linux-musl-arm64"; \
    elif [ "$TARGETARCH" = "amd64" ]; then \
        RUNTIME_ID="linux-musl-x64"; \
    else \
        echo "Unsupported architecture: $TARGETARCH" && exit 1; \
    fi && \
    echo "Building for architecture: $TARGETARCH, RID: $RUNTIME_ID" && \
    dotnet publish "./src/SlimFaas/SlimFaas.csproj" -c Release -r "$RUNTIME_ID" -o /app/publish \
     -p:DebugType=none \
     -p:DebugSymbols=false \
     -p:PublishAot=true \
     -p:StripSymbols=true \
     -p:CppCompilerAndLinker=clang-18 \
     -p:LinkerFlavor=lld

FROM base AS final
WORKDIR /app
COPY --chown=appuser --from=publish /app/publish .
ENTRYPOINT ["./SlimFaas"]



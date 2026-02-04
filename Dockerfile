FROM --platform=$BUILDPLATFORM  alpine:3.23 AS base
RUN apk update && apk upgrade
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
RUN adduser -u 1000 --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.23 AS build
RUN apk update && apk upgrade
RUN apk add --no-cache clang18 llvm18 build-base zlib-dev
# Force .NET AOT to use clang instead of gcc
RUN ln -sf /usr/bin/clang-18 /usr/bin/gcc && \
    ln -sf /usr/bin/clang++-18 /usr/bin/g++
ENV CC=clang-18
ENV CXX=clang++-18
WORKDIR /src

FROM --platform=$BUILDPLATFORM  build AS publish
COPY . .
ARG RUNTIME_ID=x64
RUN dotnet publish "./src/SlimFaas/SlimFaas.csproj" -c Release -a "$RUNTIME_ID"  -o /app/publish \
     -p:DebugType=none \
     -p:DebugSymbols=false

FROM --platform=$BUILDPLATFORM  base AS final
WORKDIR /app
COPY --chown=appuser --from=publish /app/publish .
ENTRYPOINT ["./SlimFaas"]



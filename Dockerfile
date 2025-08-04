FROM --platform=$BUILDPLATFORM  alpine:3.22 AS base
RUN apk update && apk upgrade
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
RUN adduser -u 1000 --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

EXPOSE 80
EXPOSE 443

FROM --platform=$BUILDPLATFORM  mcr.microsoft.com/dotnet/sdk:9.0-alpine3.22 AS build
RUN apk update && apk upgrade
RUN apk add --no-cache clang build-base zlib-dev
WORKDIR /src

FROM --platform=$BUILDPLATFORM  build AS publish
COPY . .
ARG RUNTIME_ID=x64
RUN dotnet publish "./src/SlimFaas/SlimFaas.csproj" -c Release -a "$RUNTIME_ID"  -o /app/publish
RUN ls -la /app/publish
RUN rm /app/publish/*.pdb
RUN rm /app/publish/*.dbg

FROM --platform=$BUILDPLATFORM  base AS final
WORKDIR /app
COPY --chown=appuser --from=publish /app/publish .
RUN ls -la
ENTRYPOINT ["./SlimFaas"]



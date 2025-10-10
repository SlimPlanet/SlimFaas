FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore "FibonacciBatch.csproj"
RUN dotnet publish "FibonacciBatch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

ARG APP_USER=app
ARG APP_UID=10001
ARG APP_GID=10001
RUN set -eux; \
    if [ -f /etc/alpine-release ]; then \
        addgroup -g "${APP_GID}" -S "${APP_USER}" 2>/dev/null || true; \
        adduser  -u "${APP_UID}" -S -G "${APP_USER}" -s /sbin/nologin "${APP_USER}" 2>/dev/null || true; \
    else \
        getent group "${APP_USER}" >/dev/null || groupadd -g "${APP_GID}" "${APP_USER}"; \
        id -u "${APP_USER}" >/dev/null 2>&1 || useradd -m -u "${APP_UID}" -g "${APP_GID}" -s /usr/sbin/nologin "${APP_USER}"; \
    fi

COPY --from=build /app/publish ./
RUN chown -R "${APP_USER}:${APP_USER}" /app
USER ${APP_USER}

ENTRYPOINT ["dotnet", "FibonacciBatch.dll"]

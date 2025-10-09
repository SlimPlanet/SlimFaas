# syntax=docker/dockerfile:1.6

# --- Build ---
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore "FibonacciBatch.csproj"
RUN dotnet publish "FibonacciBatch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
# ^ pas de -r / pas de self-contained => multi-arch via runtime .NET

# --- Runtime ---
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

# (optionnel) créer un user non-root stable
ARG APP_UID=10001
ARG APP_GID=10001
RUN groupadd -g ${APP_GID} app && useradd -m -u ${APP_UID} -g ${APP_GID} -s /usr/sbin/nologin app
COPY --from=build /app/publish ./
RUN chown -R app:app /app
USER app

ENTRYPOINT ["dotnet", "FibonacciBatch.dll"]

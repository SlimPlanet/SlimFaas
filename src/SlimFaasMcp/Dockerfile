# 1. Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copie tout le projet d’un coup (et pas juste le .csproj)
COPY . ./

# Restore complet, avec le runtime cible
RUN dotnet restore --runtime linux-x64

# Publish optimisé
RUN dotnet publish -c Release -r linux-x64 -o out \
    --no-restore \
    -p:PublishReadyToRun=false \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=None \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:InvariantGlobalization=true

# 2. Runtime stage (smallest possible image)
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/out ./

# Optionally run as non-root
# RUN useradd -m appuser && chown -R appuser /app
# USER appuser

# Expose port (update if needed)
EXPOSE 8080
ENV ASPNETCORE_URLS=http://*:8080

ENTRYPOINT ["./SlimFaasMcp"]

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

COPY . ./

RUN dotnet restore --runtime linux-x64

RUN dotnet publish -c Release -r linux-x64 -o out \
    --no-restore \
    -p:PublishReadyToRun=false \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=None \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:InvariantGlobalization=true

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/out ./

RUN useradd -m appuser && chown -R appuser /app
USER appuser

ENTRYPOINT ["./SlimFaasMcp"]

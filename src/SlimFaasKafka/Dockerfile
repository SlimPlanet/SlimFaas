# Build context: the repository root, so the shared build settings (Directory.Build.*, .editorconfig, eng/) apply.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Build.targets global.json .editorconfig ./
COPY eng/ eng/
COPY src/SlimFaasKafka/SlimFaasKafka.csproj src/SlimFaasKafka/
RUN dotnet restore src/SlimFaasKafka/SlimFaasKafka.csproj
COPY src/SlimFaasKafka/ src/SlimFaasKafka/
RUN dotnet publish src/SlimFaasKafka/SlimFaasKafka.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

# L'API écoute sur 8080
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SlimFaasKafka.dll"]

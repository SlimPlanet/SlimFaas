# Build context: the repository root, so the shared build settings (Directory.Build.*, Directory.Packages.props, .editorconfig, eng/) apply.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json .editorconfig ./
COPY eng/ eng/
COPY samples/FibonacciKafkaProducer/FibonacciKafkaProducer.csproj samples/FibonacciKafkaProducer/
ARG RELEASE_VERSION=0.0.0-dev.0
ARG SOURCE_REVISION_ID
RUN dotnet restore samples/FibonacciKafkaProducer/FibonacciKafkaProducer.csproj -p:RestorePackagesWithLockFile=true
COPY samples/FibonacciKafkaProducer/ samples/FibonacciKafkaProducer/
RUN dotnet publish samples/FibonacciKafkaProducer/FibonacciKafkaProducer.csproj -c Release -o /app/publish --no-restore -p:Version="$RELEASE_VERSION" -p:SourceRevisionId="$SOURCE_REVISION_ID" -p:RestorePackagesWithLockFile=true

RUN mkdir -p /app/dependencies && find . -name packages.lock.json -exec cp --parents {} /app/dependencies/ \;

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS final
COPY --from=build /app/dependencies /usr/share/slimfaas/dependencies
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FibonacciKafkaProducer.dll"]

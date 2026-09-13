# Build context: the repository root, so the shared build settings (Directory.Build.*, .editorconfig, eng/) apply.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Build.targets global.json .editorconfig ./
COPY eng/ eng/
COPY samples/FibonacciKafkaListener/FibonacciKafkaListener.csproj samples/FibonacciKafkaListener/
RUN dotnet restore samples/FibonacciKafkaListener/FibonacciKafkaListener.csproj
COPY samples/FibonacciKafkaListener/ samples/FibonacciKafkaListener/
RUN dotnet publish samples/FibonacciKafkaListener/FibonacciKafkaListener.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FibonacciKafkaListener.dll"]

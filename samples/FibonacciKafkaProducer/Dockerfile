# Build context: the repository root, so the shared build settings (Directory.Build.*, .editorconfig, eng/) apply.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Build.targets global.json .editorconfig ./
COPY eng/ eng/
COPY samples/FibonacciKafkaProducer/FibonacciKafkaProducer.csproj samples/FibonacciKafkaProducer/
RUN dotnet restore samples/FibonacciKafkaProducer/FibonacciKafkaProducer.csproj
COPY samples/FibonacciKafkaProducer/ samples/FibonacciKafkaProducer/
RUN dotnet publish samples/FibonacciKafkaProducer/FibonacciKafkaProducer.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FibonacciKafkaProducer.dll"]

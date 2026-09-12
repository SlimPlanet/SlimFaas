# Build context: the repository root, so the shared build settings (Directory.Build.*, .editorconfig, eng/) apply.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Build.targets global.json .editorconfig ./
COPY eng/ eng/
COPY samples/Fibonacci/Fibonacci.csproj samples/Fibonacci/
RUN dotnet restore samples/Fibonacci/Fibonacci.csproj
COPY samples/Fibonacci/ samples/Fibonacci/
RUN dotnet publish samples/Fibonacci/Fibonacci.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Fibonacci.dll"]

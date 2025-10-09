FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "Fibonacci.csproj"
RUN dotnet publish "Fibonacci.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Multi-arch grâce à l'image aspnet, pas de RID nécessaire
ENTRYPOINT ["dotnet", "Fibonacci.dll"]

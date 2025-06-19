FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "Fibonacci.csproj"
RUN dotnet build "Fibonacci.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Fibonacci.csproj" -r linux-x64 --self-contained=true -p:PublishSingleFile=true -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["./Fibonacci"]

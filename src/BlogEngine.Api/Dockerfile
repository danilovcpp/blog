# Build from repository root: docker build --platform linux/amd64 -f src/BlogEngine.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:8dd1a57fe6b7b8b1b357635db67e272c2f9e9d76d1dd0a32711c4c33c57d7b43 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/BlogEngine.Api/BlogEngine.Api.csproj --locked-mode \
    && dotnet publish src/BlogEngine.Api/BlogEngine.Api.csproj -c Release --no-restore --self-contained false -p:UseAppHost=false -o /app -warnaserror

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:f879b8a9cb3f6694b8b0c9fa530eb0c39d8920db09c05a7edbd6c4c6c104ae1b
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "BlogEngine.Api.dll"]

# Build from repository root; no HTTP listener or API credentials.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:8dd1a57fe6b7b8b1b357635db67e272c2f9e9d76d1dd0a32711c4c33c57d7b43 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/BlogEngine.Operator/BlogEngine.Operator.csproj --locked-mode \
    && dotnet publish src/BlogEngine.Operator/BlogEngine.Operator.csproj -c Release --no-restore --self-contained false -p:UseAppHost=false -o /app -warnaserror

FROM mcr.microsoft.com/dotnet/runtime:10.0.13@sha256:1bac183f0f5e1b7dd58cf1083fa79e6310d47afe3ca5c9130c910ebba7720929
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_ENVIRONMENT=Production DOTNET_EnableDiagnostics=0
USER $APP_UID
ENTRYPOINT ["dotnet", "BlogEngine.Operator.dll"]

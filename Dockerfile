# Server image: the Vue SPA (web/) built into the ASP.NET Core server's wwwroot.
FROM node:22-bookworm-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/RenkuPorts.Server/RenkuPorts.Server.csproj src/RenkuPorts.Server/
RUN dotnet restore src/RenkuPorts.Server/RenkuPorts.Server.csproj
COPY src/RenkuPorts.Server/ src/RenkuPorts.Server/
RUN dotnet publish src/RenkuPorts.Server/RenkuPorts.Server.csproj -c Release -o /app --no-restore
COPY --from=web /web/dist/ /app/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# git: recipe dates come from the ports tree's history (POST /api/recipes/sync).
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "RenkuPorts.Server.dll"]

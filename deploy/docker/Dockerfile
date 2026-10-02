# syntax=docker/dockerfile:1

FROM node:24-bookworm-slim AS admin-web
WORKDIR /src/src/RelayLink.AdminWeb
RUN corepack enable
COPY src/RelayLink.AdminWeb/package.json src/RelayLink.AdminWeb/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/RelayLink.AdminWeb/ ./
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NuGet.Config Directory.Build.props ./
COPY src/RelayLink.Protocol/RelayLink.Protocol.csproj src/RelayLink.Protocol/
COPY src/RelayLink.Transport/RelayLink.Transport.csproj src/RelayLink.Transport/
COPY src/RelayLink.Server/RelayLink.Server.csproj src/RelayLink.Server/
RUN dotnet restore src/RelayLink.Server/RelayLink.Server.csproj --configfile NuGet.Config
COPY src/ src/
COPY --from=admin-web /src/src/RelayLink.AdminWeb/dist/ src/RelayLink.AdminWeb/dist/
RUN dotnet publish src/RelayLink.Server/RelayLink.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:SkipAdminWebBuild=true \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends libsqlite3-0 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish/ ./
ENTRYPOINT ["dotnet", "RelayLink.Server.dll"]
CMD ["--config", "/etc/relaylink/server.json"]

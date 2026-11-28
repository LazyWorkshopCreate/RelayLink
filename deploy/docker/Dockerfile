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
COPY src/RelayLink.Logging/RelayLink.Logging.csproj src/RelayLink.Logging/
COPY src/RelayLink.Server/RelayLink.Server.csproj src/RelayLink.Server/
RUN dotnet restore src/RelayLink.Server/RelayLink.Server.csproj --configfile NuGet.Config
COPY src/RelayLink.Protocol/ src/RelayLink.Protocol/
COPY src/RelayLink.Transport/ src/RelayLink.Transport/
COPY src/RelayLink.Logging/ src/RelayLink.Logging/
COPY src/RelayLink.Server/ src/RelayLink.Server/
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
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 relaylink \
    && useradd --system --uid 10001 --gid 10001 --no-create-home --shell /usr/sbin/nologin relaylink
COPY --from=build /app/publish/ ./
USER 10001:10001
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${RELAYLINK_HEALTH_PORT:-18080} && printf 'GET /health/ready HTTP/1.1\\r\\nHost: 127.0.0.1\\r\\nConnection: close\\r\\n\\r\\n' >&3 && grep -q '200 OK' <&3"]
ENTRYPOINT ["dotnet", "RelayLink.Server.dll"]
CMD ["--config", "/etc/relaylink/server.json", "--log-directory", "/var/lib/relaylink/logs"]

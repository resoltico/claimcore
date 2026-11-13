FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS sdk
FROM node:26.13.0-bookworm@sha256:a546d3f8acd2af4382668a7e6acd3bc4418e6a27478373e3246f8162f5181509 AS node

# Prepare SOURCE.tar.gz with eng/source-archive.sh before building. Both producers
# consume that exact source inventory, which is also offered by the publications.
FROM sdk AS source
WORKDIR /source
COPY SOURCE.tar.gz /tmp/SOURCE.tar.gz
RUN tar -xzf /tmp/SOURCE.tar.gz -C /source

FROM node AS frontend
WORKDIR /source
COPY --from=source /source/ ./
RUN --mount=type=cache,target=/root/.npm \
    test "$(node --version)" = "v$(cat .node-version)" \
    && npm install --global "npm@$(node -p 'require("./web/package.json").engines.npm')" \
    && npm --prefix web ci \
    && npm --prefix web run build

FROM sdk AS build
WORKDIR /source
COPY --from=source /source/ ./
COPY --from=frontend /source/web/dist ./web/dist
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore ClaimCore.slnx --locked-mode \
    && dotnet publish src/ClaimCore.Web/ClaimCore.Web.fsproj --configuration Release --no-restore --output /published/web \
    && dotnet publish src/ClaimCore.Cli/ClaimCore.Cli.fsproj --configuration Release --no-restore --output /published/cli \
    && dotnet publish src/ClaimCore.Database/ClaimCore.Database.fsproj --configuration Release --no-restore --output /published/database

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242 AS administration
WORKDIR /app
COPY --from=build /published/database/ ./database/
COPY --from=build /published/cli/ ./cli/
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/database/claimcore-database.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242 AS web
WORKDIR /app
COPY --from=build /published/web/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "claimcore-server.dll"]

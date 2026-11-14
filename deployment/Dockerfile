FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS sdk
FROM node:26.13.1-bookworm@sha256:b7087bc4d973034b30854b378ba107f9fab622b47514cb7e2592e41b8bab8d49 AS node

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
    && dotnet publish src/ClaimCore.Server/ClaimCore.Server.fsproj --configuration Release --no-restore --output /published/server

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242 AS runtime
WORKDIR /app
COPY --from=build /published/server/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "claimcore-server.dll"]

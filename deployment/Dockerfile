FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS sdk
FROM node:26.10.0-bookworm@sha256:2aaae6d91f99fee84cfc92da9b52c22a185752d247746052bbc3f961e44478c6 AS node

FROM node AS build
COPY --from=sdk /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}"
WORKDIR /source
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages --mount=type=cache,target=/root/.npm \
    test "$(dotnet --version)" = "$(node -p 'require("./global.json").sdk.version')" \
    && test "$(node --version)" = "v$(cat .node-version)" \
    && npm install --global "npm@$(node -p 'require("./web/package.json").engines.npm')" \
    && dotnet restore ClaimCore.slnx --locked-mode \
    && npm --prefix web ci \
    && npm --prefix web run contract:generate \
    && npm --prefix web run build \
    && dotnet publish src/ClaimCore.Web --configuration Release --no-restore --output /published/web \
    && dotnet publish src/ClaimCore.Cli --configuration Release --no-restore --output /published/cli \
    && dotnet publish src/ClaimCore.Database --configuration Release --no-restore --output /published/database

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS administration
WORKDIR /app
COPY --from=build /published/database/ ./database/
COPY --from=build /published/cli/ ./cli/
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/database/ClaimCore.Database.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS web
WORKDIR /app
COPY --from=build /published/web/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "ClaimCore.Web.dll"]

FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS sdk
FROM node:26.9.0-bookworm@sha256:347d9d5391493c2fc3f0d161b75a9cab64a9805064b1b01b099f8ee97f98ed47 AS node

FROM node AS build
COPY --from=sdk /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}"
WORKDIR /source
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages --mount=type=cache,target=/root/.npm test "$(dotnet --version)" = "$(node -p 'require("./global.json").sdk.version')" \
    && test "$(node --version)" = "v$(cat .node-version)" \
    && npm install --global "npm@$(node -p 'require("./web/package.json").engines.npm')" \
    && dotnet restore ClaimCore.slnx --locked-mode \
    && dotnet tool restore \
    && npm --prefix eng ci \
    && npm --prefix web ci \
    && npm --prefix web run contract:generate \
    && npm --prefix web run sbom \
    && npm --prefix web run build \
    && node eng/ci/publish/main.mjs build --output /published

FROM node AS configuration
WORKDIR /source
COPY eng/operations/ ./eng/operations/
ENTRYPOINT ["node", "/source/eng/operations/configure-local.mjs"]

FROM node AS revocation
WORKDIR /source
COPY eng/operations/revocation-publication.mjs ./
ENTRYPOINT ["node", "/source/revocation-publication.mjs"]

FROM build AS publication

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS administration
WORKDIR /app
COPY --from=publication /published/database/ ./database/
COPY --from=publication /published/cli/ ./cli/
COPY deployment/initialize-local.sh /app/initialize-local.sh
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/database/ClaimCore.Database.dll"]


FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS web
WORKDIR /app
COPY --from=publication /published/web/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "ClaimCore.Web.dll"]

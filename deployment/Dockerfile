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

FROM node AS identity-theme
WORKDIR /source
COPY --from=source /source/web/src/theme/tokens.json ./web/src/theme/tokens.json
COPY --from=source /source/deployment/identity-theme/ ./deployment/identity-theme/
RUN node deployment/identity-theme/generate.mjs /theme \
    && find /theme -type d -exec chmod 0755 {} + \
    && find /theme -type f -exec chmod 0644 {} +

FROM quay.io/keycloak/keycloak:26.8.2@sha256:c68e286cacd87b942ac1939a0a1481461109d6acd3a57dc496969a319a76bbf0 AS keycloak
FROM eclipse-temurin:21-jdk@sha256:db5cc6341a1d7bc40cdb51e1f0d9824f3bb2cc8107d29da421dc4df7d11f05f1 AS identity-locale
COPY --from=keycloak /opt/keycloak/lib/lib/main /keycloak-lib
COPY --from=source /source/deployment/identity-locale/ /locale/
RUN mkdir /classes \
    && javac --release 21 -cp '/keycloak-lib/*' -d /classes /locale/ScriptLocaleFactory.java \
    && jar --create --file /claimcore-script-locale.jar -C /classes . -C /locale META-INF \
    && chmod 0644 /claimcore-script-locale.jar

FROM keycloak AS identity
COPY --from=identity-theme /theme /opt/keycloak/themes/claimcore/login
COPY --from=identity-locale /claimcore-script-locale.jar /opt/keycloak/providers/

FROM sdk AS build
WORKDIR /source
COPY --from=source /source/ ./
COPY --from=frontend /source/web/dist ./web/dist
# Public assets can inherit checkout modes. Package them for non-root readers.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore ClaimCore.slnx --locked-mode \
    && dotnet publish src/ClaimCore.Server/ClaimCore.Server.fsproj --configuration Release --no-restore --output /published/server \
    && find /published/server/wwwroot -type d -exec chmod 0755 {} + \
    && find /published/server/wwwroot -type f -exec chmod 0644 {} +

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute-chiseled-extra@sha256:cea9d5494f358fe43082300c24d1cd9f651066b6b7de1e5735bca8d3ade68649 AS runtime
WORKDIR /app
COPY --from=build /published/server/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "claimcore-server.dll"]

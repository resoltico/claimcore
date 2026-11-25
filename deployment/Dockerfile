FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:2794448a07118a6eaf00f29ab5d2bf44928517ce5ff86505a63a69230374f599 AS sdk
FROM node:26.13.1-bookworm@sha256:893ea48106e988a5f91e69629d6472600daa9d0494adf7db87eb0b886ce82255 AS node

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
COPY --from=source /source/web/public/brand.svg ./web/public/brand.svg
COPY --from=source /source/deployment/identity-theme/ ./deployment/identity-theme/
RUN node deployment/identity-theme/generate.mjs /theme \
    && find /theme -type d -exec chmod 0755 {} + \
    && find /theme -type f -exec chmod 0644 {} +

FROM quay.io/keycloak/keycloak:26.8.2@sha256:7e66a96e4739239070aeaf72e4c5decf0f7f682cfc725b9f7ea2b336eaba38b3 AS keycloak
FROM eclipse-temurin:21-jdk@sha256:8ad118b146aa92da815b576441acd6b42b38236797ad0b6edc8c200b85bb0734 AS identity-locale
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

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute-chiseled-extra@sha256:4f2b9809c1974acd91dc04777bf464808fb5d22a3455c6657a97847077878936 AS runtime
WORKDIR /app
COPY --from=build /published/server/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "claimcore-server.dll"]

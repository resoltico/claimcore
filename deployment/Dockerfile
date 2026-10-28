FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS sdk
FROM node:26.12.1-bookworm@sha256:5c4012d11d9857c42713ba511fbc19645cac1706bb710d51d42b0d301dff7f88 AS node

# The browser build consumes tracked contracts and needs no .NET SDK.
FROM node AS frontend
WORKDIR /source
COPY .node-version ./
COPY web/package.json web/package-lock.json web/.npmrc ./web/
RUN --mount=type=cache,target=/root/.npm \
    test "$(node --version)" = "v$(cat .node-version)" \
    && npm install --global "npm@$(node -p 'require("./web/package.json").engines.npm')" \
    && npm --prefix web ci
COPY contracts ./contracts
COPY src/ClaimCore.Protocol/Messages/Localization ./src/ClaimCore.Protocol/Messages/Localization
COPY web/tsconfig.base.json web/tsconfig.app.json web/tsconfig.protocol.json web/vite.config.ts web/index.html ./web/
COPY web/src ./web/src
COPY web/locales ./web/locales
COPY web/scripts ./web/scripts
COPY LICENSE ./
RUN npm --prefix web run build

FROM sdk AS build
WORKDIR /source
COPY . .
COPY --from=frontend /source/web/dist ./web/dist
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore ClaimCore.slnx --locked-mode \
    && dotnet publish src/ClaimCore.Web --configuration Release --no-restore --output /published/web \
    && dotnet publish src/ClaimCore.Cli --configuration Release --no-restore --output /published/cli \
    && dotnet publish src/ClaimCore.Database --configuration Release --no-restore --output /published/database

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS administration
WORKDIR /app
COPY --from=build /published/database/ ./database/
COPY --from=build /published/cli/ ./cli/
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/database/claimcore-database.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS web
WORKDIR /app
COPY --from=build /published/web/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "claimcore-server.dll"]

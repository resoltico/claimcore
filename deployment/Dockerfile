FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS sdk
FROM node:26.11.1-bookworm@sha256:f1233c415b41ffcf237c717b3dea92e9d4ea006e0e4c71edcb790605d74f5404 AS node

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
COPY src/ClaimCore.Contract/Localization ./src/ClaimCore.Contract/Localization
COPY web ./web
COPY prettier.config.mjs LICENSE ./
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS administration
WORKDIR /app
COPY --from=build /published/database/ ./database/
COPY --from=build /published/cli/ ./cli/
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/database/ClaimCore.Database.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS web
WORKDIR /app
COPY --from=build /published/web/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "ClaimCore.Web.dll"]

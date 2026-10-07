FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS sdk
FROM node:26.10.0-bookworm@sha256:2aaae6d91f99fee84cfc92da9b52c22a185752d247746052bbc3f961e44478c6 AS node

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
COPY --from=publication /published/manifests/ /app/publication-manifests/
COPY deployment/initialize-local.sh /app/initialize-local.sh
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/database/ClaimCore.Database.dll"]


FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS web
WORKDIR /app
COPY --from=publication /published/web/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "ClaimCore.Web.dll"]

FROM node AS browser-qualification
WORKDIR /source
COPY web/package.json web/package-lock.json web/.npmrc ./web/
RUN npm install --global "npm@$(node -p 'require("./web/package.json").engines.npm')" \
    && npm --prefix web ci \
    && npm --prefix web exec -- playwright install --with-deps chromium \
    && apt-get update \
    && apt-get install -y --no-install-recommends libnss3-tools=2:3.87.1-1+deb12u4 \
    && rm -rf /var/lib/apt/lists/* \
    && cp -a /root/.cache/ms-playwright /opt/claimcore-browsers
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/claimcore-browsers
COPY web/e2e/browser-trust-*.mjs ./web/e2e/
USER node
ENTRYPOINT ["node", "/source/web/e2e/browser-trust-driver.mjs"]

# Fresh SDK client filesystems run an engineering probe against exact publication assemblies.
FROM sdk AS transport-qualification
WORKDIR /app
COPY --from=publication /published/database/ ./database/
COPY --from=publication /published/manifests/database.json ./database-manifest.json
COPY eng/ci/deployment/transport-probe.fsx ./transport-probe.fsx
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENV DOTNET_NOLOGO=1
ENTRYPOINT ["dotnet", "fsi", "--exec", "/app/transport-probe.fsx"]

# A source mutation in an owned build must reach the same healthy connection experiment.
FROM node AS transport-mutant-build
COPY --from=sdk /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}"
WORKDIR /mutation
COPY . .
COPY --from=publication /published/database/ /mutant/database/
COPY --from=publication /published/manifests/database.json /mutant/manifests/database.json
RUN --mount=type=cache,target=/root/.nuget/packages --mount=type=cache,target=/root/.npm \
    npm install --global "npm@$(node -p 'require("./eng/package.json").engines.npm')" \
    && npm --prefix eng ci \
    && node eng/ci/deployment/transport-mutant.mjs

FROM transport-qualification AS transport-negative-control
COPY --from=transport-mutant-build /mutant/database/ /app/database/
COPY --from=transport-mutant-build /mutant/manifests/database.json /app/database-manifest.json

# Export one Linux publication for local qualification; all clients consume this exact tree.
FROM scratch AS publication-export
COPY --from=build /published/ /published/
